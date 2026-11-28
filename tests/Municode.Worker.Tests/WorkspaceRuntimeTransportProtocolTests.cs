using System.Buffers;
using System.Text.Json;
using Municode.Common.Models;
using Municode.Worker.DevelopmentWorkspaces;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceRuntimeTransportProtocolTests
{
    [Theory]
    [InlineData("primary")]
    [InlineData("ephemeral")]
    public void T3SessionGrantsOnlyWorkspaceTerminalOrchestrationCloneAndBrowseScopes(string kind)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, kind, "203.0.113.8", "");
        Assert.Equal("python3 -B /opt/municode-workspace/issue-t3-session.py --vm-id vm-current " +
            "--generation 7 --operation-id session-current --subject municode-gateway --ttl 300 " +
            "--scope terminal:operate --scope orchestration:operate --scope terminal:read " +
            "--scope orchestration:read --scope source-control:write --scope filesystem:read",
            WorkspaceRuntimeTransport.IssueT3SessionCommand(target, "session-current", 300));
    }

    [Theory]
    [InlineData("connect", 774, true)]
    [InlineData("connect", 3774, false)]
    [InlineData("preview", 774, false)]
    [InlineData("t3", 774, false)]
    [InlineData("preview", 8080, true)]
    [InlineData("t3", 3773, true)]
    public void ManagementForwardingCannotUseACodingOwnedListener(string service, int port, bool permitted) =>
        Assert.Equal(permitted, WorkspaceTunnelManager.IsPermittedRemotePort(service, port));

    [Theory]
    [InlineData("primary", 7, "vm-current", true)]
    [InlineData("ephemeral", 7, "vm-current", false)]
    [InlineData("primary", 0, "vm-current", false)]
    [InlineData("primary", 7, "vm; echo forged", false)]
    public void ConnectManagementCredentialIsOnlyIssuedForAnExactPrimaryGeneration(
        string kind, long generation, string vmId, bool allowed)
    {
        var target = new WorkspaceRuntimeTarget(vmId, generation, kind, "203.0.113.8", "");
        if (!allowed)
        {
            Assert.Throws<InvalidOperationException>(() => WorkspaceRuntimeTransport.ConnectCredentialCommand(target));
            Assert.Throws<InvalidOperationException>(() => WorkspaceRuntimeTransport.ConnectAdmissionCommand(target));
            return;
        }
        Assert.Equal("python3 -B /opt/municode-workspace/t3_connect.py credential --vm-id vm-current --generation 7",
            WorkspaceRuntimeTransport.ConnectCredentialCommand(target));
        Assert.Equal("python3 -B /opt/municode-workspace/t3_connect.py admission --vm-id vm-current --generation 7",
            WorkspaceRuntimeTransport.ConnectAdmissionCommand(target));
    }

    [Theory]
    [InlineData("ConnectManagementAdmission", true, "vm-current", 7, true)]
    [InlineData("ConnectManagementAdmission", false, "vm-current", 7, false)]
    [InlineData("ConnectManagementAdmission", true, "vm-other", 7, false)]
    [InlineData("ConnectManagementAdmission", true, "vm-current", 6, false)]
    [InlineData("HTTP200", true, "vm-current", 7, false)]
    public void ConnectAdmissionProofMustBeExplicitAndBoundToTheCurrentGeneration(
        string type, bool admitted, string vmId, long generation, bool expected)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, "primary", "203.0.113.8", "");
        var response = JsonSerializer.Serialize(new { type, admitted, vmId, generation });
        Assert.Equal(expected, WorkspaceRuntimeTransport.IsConnectAdmission(target, response));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("200 OK")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"ConnectManagementAdmission\",\"admitted\":\"true\"}")]
    public void MissingMalformedOrNonBooleanConnectAdmissionNeverAuthorizesPublication(string response)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, "primary", "203.0.113.8", "");
        Assert.False(WorkspaceRuntimeTransport.IsConnectAdmission(target, response));
    }

    [Theory]
    [InlineData("refs/heads/main", 6)]
    [InlineData("refs/heads/master", 6)]
    [InlineData("refs/heads/workspaces/foreign/g6", 6)]
    [InlineData("refs/heads/workspaces/vm-current/g5", 5)]
    public void SavedSourceCannotUseDefaultForeignOrStaleGenerationRef(string remoteRef, long generation)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, "ephemeral", "203.0.113.8", "");
        var source = new DevelopmentWorkspaceGitRecoverySource("https://github.com/fixture/repo.git",
            new('b', 40), remoteRef, generation, "prior-stop", new('a', 64));
        Assert.Throws<InvalidOperationException>(() => WorkspaceRuntimeTransport.GitRecoveryCommand(target, source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("first line\nsecond line\r\n'$(synthetic-only)'")]
    public void RuntimeCallProducesExactlyOneTerminatedJsonRequestBoundToCurrentGeneration(string? text)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, "primary", "203.0.113.8", "");
        var command = WorkspaceRuntimeTransport.CallCommand(target, "operation-current", "status",
            text is null ? null : new
            {
                vmId = "stale-vm", generation = -1, operationId = "stale-operation", action = "stop", text
            });
        const string prefix = "printf '%s' '";
        const string suffix = "' | base64 -d | python3 -B /opt/municode-workspace/runtime.py call";
        Assert.StartsWith(prefix, command);
        Assert.EndsWith(suffix, command);
        var bytes = Convert.FromBase64String(command[prefix.Length..^suffix.Length]);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));

        Assert.True(reader.TryReadTo(out ReadOnlySequence<byte> frame, (byte)'\n'));
        Assert.True(reader.End);
        Assert.InRange(frame.Length, 1, 262143);
        using var request = JsonDocument.Parse(frame);
        Assert.Equal(target.VmId, request.RootElement.GetProperty("vmId").GetString());
        Assert.Equal(target.Generation, request.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal("operation-current", request.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("status", request.RootElement.GetProperty("action").GetString());
        if (text is not null)
            Assert.Equal(text, request.RootElement.GetProperty("text").GetString());
    }
}

public sealed class WorkspaceProvisionProtocolTests
{
    private static readonly string Recipe = new('a', 64);

    [Theory]
    [InlineData("""{"result":{"state":"Provisioned","recipeSha256":"RECIPE"},"unit":"inactive"}""", "Provisioned")]
    [InlineData("""{"result":{"state":"Provisioned","recipeSha256":"other"},"unit":"inactive"}""", "Failed")]
    [InlineData("""{"result":{"state":"Failed","error":"Ubuntu 24.04 required"},"unit":"failed"}""", "Failed")]
    [InlineData("""{"result":null,"unit":"failed"}""", "Failed")]
    [InlineData("""{"result":null,"unit":"activating"}""", "Running")]
    [InlineData("""{"result":null,"unit":"active"}""", "Running")]
    [InlineData("""{"result":null,"unit":"inactive"}""", "NotStarted")]
    [InlineData("""{"result":null,"unit":""}""", "NotStarted")]
    public void ProvisionStatusTrustsOnlyAResultForTheReservedRecipe(string json, string expected)
    {
        using var document = JsonDocument.Parse(json.Replace("RECIPE", Recipe));
        Assert.Equal(expected, WorkspaceRuntimeTransport.ReadProvisionStatus(document.RootElement, Recipe).State);
    }

    [Fact]
    public void ProvisioningRunsTheUploadedRecipeAsADetachedUnitWithTheReservedDigest()
    {
        Assert.Equal("systemd-run --quiet --unit=municode-provision.service --property=WorkingDirectory=/opt/municode-image-source " +
            "/usr/bin/python3 -I -B /opt/municode-image-source/image/build.py provision " +
            "--acknowledgement PROVISION-FRESH-STOCK-WORKSPACE-VM --recipe-sha256 " + Recipe + " && echo '{\"started\":true}'",
            WorkspaceRuntimeTransport.StartProvisionCommand(Recipe));
    }
}
