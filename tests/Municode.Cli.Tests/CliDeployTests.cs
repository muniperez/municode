// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Commands;
using Municode.Cli.Deploy;
using Municode.Cli.Instances;
using Municode.Cli.Seed;
using static Municode.Cli.Tests.SeedReleaseTests;

namespace Municode.Cli.Tests;

public sealed class CliDeployTests
{
    [Theory]
    [InlineData("exact", "1.2.4")]
    [InlineData("latest", "1.2.4")]
    [InlineData("prerelease", "1.0.0-acceptance.7")]
    [InlineData("local", "0.0.0-local")]
    public async Task PlanStagesVerifiedExactIdentityAndJournalsApproval(string source, string version)
    {
        using var fixture = new Fixture(version, sourceDirty: source == "local" ? false : null);
        await fixture.RegisterAsync();
        string[] options = source switch
        {
            "exact" => ["--release", "municode-v" + version],
            "latest" => ["--release", "latest"],
            "prerelease" => ["--release", "latest", "--prerelease"],
            _ => ["--bundle-dir", fixture.Release.Root, "--trust", "local-build"]
        };
        Assert.Equal(CliExitCodes.Success, await fixture.Run("plan", ["--instance", "demo", .. options]));
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        var root = output.RootElement;
        var operation = root.GetProperty("operation").Deserialize<DeployOperation>(SeedJournal.Json)!;
        var sha = SeedRelease.FileDigest(fixture.Archive);
        var target = operation.Plan.TargetRelease;
        Assert.Equal("municode-v" + version, target.Tag);
        Assert.Equal("planned", operation.Phase);
        Assert.Equal(sha, target.BundleSha256);
        Assert.Equal(new string('d', 40), target.SourceRevision);
        Assert.Equal(1, operation.Plan.DeployProtocol);
        Assert.Equal(operation.Plan, fixture.PreflightPlan);
        Assert.Empty(root.GetProperty("blockers").EnumerateArray());
        Assert.Matches("^[a-f0-9]{64}$", operation.ApprovalDigest!);
        Assert.Equal(source is "latest" or "prerelease", operation.PublishedAt is not null);
        if (source == "local")
        {
            Assert.Equal($"local-{new string('d', 12)}-{sha[..12]}", target.ReleaseId);
            Assert.Equal("local-build", target.Trust);
            Assert.Null(fixture.Cosign.CertificateIdentity);
            Assert.DoesNotContain("--verified-release-tag", fixture.StageCommand!);
        }
        else
        {
            Assert.Equal(target.Tag, target.ReleaseId);
            Assert.Equal("github-sigstore", target.Trust);
            Assert.EndsWith($"@refs/tags/{target.Tag}", fixture.Cosign.CertificateIdentity);
            Assert.Contains("--verified-release-tag", fixture.StageCommand!);
        }
        using (var journal = new DeployJournal(fixture.Root, operation.Plan.OperationId))
        {
            Assert.Equal(operation, journal.Load());
            SeedFiles.Check(Path.Combine(journal.DirectoryPath, "operation.json"));
            Assert.False(Directory.Exists(Path.Combine(journal.DirectoryPath, "release")));
        }
        Assert.DoesNotContain("synthetic-secret", fixture.Console.Output + fixture.Console.Error);
    }

    [Theory]
    [InlineData(2, null, false, "use the CLI from the target release")]
    [InlineData(2, false, false, "use the CLI from the target release")]
    [InlineData(1, true, false, "clean local build")]
    [InlineData(1, false, true, "component digest")]
    public async Task PlanRefusesUnsafeReleaseBeforeUploading(
        int protocol, bool? dirty, bool tamperImage, string diagnostic)
    {
        using var fixture = new Fixture(protocol: protocol, sourceDirty: dirty, tamperImage: tamperImage);
        await fixture.RegisterAsync();
        string[] source = dirty is null ? ["--release", "municode-v1.2.4"]
            : ["--bundle-dir", fixture.Release.Root, "--trust", "local-build"];
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("plan", ["--instance", "demo", .. source]));
        Assert.Contains(diagnostic, fixture.Console.Error);
        Assert.Null(fixture.StageCommand);
        Assert.Equal(0, fixture.UploadCalls);
    }

    [Theory]
    [InlineData("stage-mismatch", "staged release identity")]
    [InlineData("digest-mismatch", "inconsistent approval digest")]
    [InlineData("blocked", null)]
    public async Task PlanDoesNotApproveMismatchesOrHostBlockers(string mode, string? error)
    {
        using var fixture = new Fixture { Mode = mode };
        await fixture.RegisterAsync();
        Assert.Equal(CliExitCodes.ValidationError,
            await fixture.Run("plan", "--instance", "demo", "--release", "municode-v1.2.4"));
        Assert.Equal(1, fixture.UploadCalls);
        Assert.True(fixture.CleanedUpload);
        if (error is not null)
        {
            Assert.Contains(error, fixture.Console.Error);
            Assert.Single(fixture.Console.Messages); // Registration only.
        }
        else
        {
            using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
            Assert.Equal("Missing required operator environment keys.", output.RootElement.GetProperty("blockers")[0].GetString());
            Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("operation").GetProperty("approvalDigest").ValueKind);
        }
        var op = Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "deploy"))));
        using var journal = new DeployJournal(fixture.Root, op);
        Assert.Null(journal.Load().ApprovalDigest);
    }

    [Fact]
    public async Task StatusReportsActivePublicAndSelectedOrLatestHostOperation()
    {
        using var fixture = new Fixture();
        await fixture.RegisterAsync();
        Assert.Equal(CliExitCodes.Success, await fixture.Run("plan", "--instance", "demo", "--release", "municode-v1.2.4"));
        var op = fixture.PreflightPlan!.OperationId;
        Assert.Equal(CliExitCodes.Success, await fixture.Run("status", op));
        using (var document = JsonDocument.Parse(fixture.Console.Messages.Last()))
        {
            var root = document.RootElement;
            Assert.Equal("municode-v1.2.3", root.GetProperty("activeRelease").GetProperty("tag").GetString());
            Assert.Equal("municode-v1.2.2", root.GetProperty("publicVersion").GetProperty("tag").GetString());
            Assert.Equal("planned", root.GetProperty("operation").GetProperty("phase").GetString());
            Assert.Equal(op, root.GetProperty("hostOperation").GetProperty("operationId").GetString());
            Assert.Equal("state-file", root.GetProperty("hostOperation").GetProperty("source").GetString());
        }
        fixture.HealthUnavailable = true;
        Assert.Equal(CliExitCodes.Success, await fixture.Run("status", "--instance", "demo"));
        using (var document = JsonDocument.Parse(fixture.Console.Messages.Last()))
        {
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("operation").ValueKind);
            Assert.Equal("latest-on-host", root.GetProperty("hostOperation").GetProperty("operationId").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("publicVersion").ValueKind);
            Assert.Equal("unavailable", root.GetProperty("publicVersionStatus").GetString());
        }
    }

    [Fact]
    public void ApprovalDigestMatchesTheHostCanonicalJson()
    {
        // Digest produced by Python json.dumps(sort_keys=True,separators=(',',':'),ensure_ascii=True).
        using var input = JsonDocument.Parse("""{ "z": ["\u00e9", "x\u007f", "<&", "\uD83D\uDE00"], "a": { "y": true, "b": 1 } }""");
        Assert.Equal("de1b13cc9be698c292b9feb54a20f2febb030873bae66365713105a46de023c2",
            DeployApproval.Compute(input.RootElement));
    }

    private sealed class Fixture : IDisposable, ISeedProcess
    {
        private readonly FakeInstanceSsh _instanceSsh = new();
        private readonly CliApplication _app;
        private readonly HttpClient _http;
        private string? _uploadedSha;
        private readonly InstanceRelease _active = new("municode-v1.2.3", "municode-v1.2.3",
            new string('a', 40), new string('b', 64), "github-release", DateTimeOffset.UnixEpoch);
        public ReleaseFixture Release { get; }
        public string Archive { get; }
        public string Root { get; } = SeedTestDirectory.Create("deploy");
        public TestConsole Console { get; } = new();
        public CosignProcess Cosign { get; } = new();
        public string? Mode { get; init; }
        public DeployPlan? PreflightPlan { get; private set; }
        public string? StageCommand { get; private set; }
        public int UploadCalls { get; private set; }
        public bool CleanedUpload { get; private set; }
        public bool HealthUnavailable { get; set; }

        public Fixture(string version = "1.2.4", int protocol = 1, bool? sourceDirty = null, bool tamperImage = false)
        {
            Release = new(deployProtocol: protocol, version: version, sourceDirty: sourceDirty, tamperImage: tamperImage);
            Archive = Path.Combine(Release.Root, $"municode-{version}-linux-any.tar.gz");
            _http = new HttpClient(new DeployHttp(this));
            var environment = CliEnvironment.ForTests("https://unused.example.com", Root);
            var tokens = new TokenStore(environment);
            _app = new(Console, environment, tokens, new MunicodeApiClient(environment, tokens, _http),
                new SeedRuntime(), this, _http, new SeedRelease(Cosign, new DeployHttp(this)));
        }

        public async Task RegisterAsync() =>
            Assert.Equal(CliExitCodes.Success, await _app.RunAsync(
                ["instance", "add", "demo", "--ssh", "operator@demo", "--confirm-host-fingerprint", FakeInstanceSsh.Fingerprint, "--json"],
                CancellationToken.None));

        public Task<int> Run(string action, params string[] args) =>
            _app.RunAsync(["deploy", action, .. args, "--json"], CancellationToken.None);

        public Task<SeedProcessResult> RunAsync(string file, IReadOnlyList<string> args, byte[]? input, CancellationToken ct)
        {
            var command = args[^1];
            if (args.Contains("StrictHostKeyChecking=accept-new") || command.Contains("operator.env", StringComparison.Ordinal))
                return _instanceSsh.RunAsync(file, args, input, ct);
            CheckPinned(file, args, false);
            if (command.Contains("mktemp -d", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("sudo", command);
                return Result("/tmp/municode-deploy.AbCd012345");
            }
            if (command.Contains("rmdir ", StringComparison.Ordinal))
            {
                CleanedUpload = true;
                return Result("");
            }
            Assert.Contains("sudo -n", command);
            if (command.Contains(" stage ", StringComparison.Ordinal))
            {
                StageCommand = command;
                Assert.Contains($"/opt/municode/releases/{_active.ReleaseId}/deploy_host.py", command);
                Assert.Contains(_uploadedSha!, command);
                var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Release.Root, "release-manifest.json")));
                using (manifest)
                {
                    var local = manifest.RootElement.TryGetProperty("sourceDirty", out _);
                    var tag = "municode-v" + manifest.RootElement.GetProperty("version").GetString();
                    var sha = Mode == "stage-mismatch" ? new string('0', 64) : _uploadedSha!;
                    return Json(new
                    {
                        deployProtocol = 1,
                        targetRelease = new DeployReleaseIdentity(local ? $"local-{new string('d', 12)}-{sha[..12]}" : tag,
                            tag, new string('d', 40), sha, local ? "local-build" : "github-sigstore")
                    });
                }
            }
            Assert.Contains("state.json", command);
            var op = PreflightPlan!.OperationId;
            return Json(new DeployHostOperation(command.Contains(op, StringComparison.Ordinal) ? op : "latest-on-host",
                "staged", "state-file"));
        }

        public async Task<SeedProcessResult> RunStreamAsync(string file, IReadOnlyList<string> args, Stream input, CancellationToken ct)
        {
            CheckPinned(file, args, true);
            var command = args[^1];
            if (command.Contains("bundle.tar.gz", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("sudo", command);
                UploadCalls++;
                _uploadedSha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct));
                return new(0, "");
            }
            Assert.Contains("sudo -n", command);
            Assert.Contains("preflight --json --plan", command);
            var plan = await JsonSerializer.DeserializeAsync<DeployPlan>(input, SeedJournal.Json, ct);
            PreflightPlan = plan;
            Assert.Contains($"/opt/municode/releases/{plan!.TargetRelease.ReleaseId}/deploy_host.py", command);
            var applied = new[] { "20260101000000_Initial" };
            var pending = new[] { "20260201000000_Next" };
            var envDiff = new { added = new[] { "MUNICODE_NEW" }, changed = Array.Empty<string>() };
            var images = new[] { "registry.example.com/image@sha256:" + new string('f', 64) };
            var effectiveConfigHash = new string('e', 64);
            if (Mode == "blocked") return new(1, """{"blockers":["Missing required operator environment keys."],"composeConfigValid":false}""");
            return await Json(new
            {
                deployProtocol = 1, currentRelease = _active, plan.TargetRelease, composeConfigValid = true,
                envDiff, migrations = new { applied, pending }, effectiveConfigHash,
                digestInputs = new
                {
                    plan.InstanceName, plan.InstallationId, plan.HostFingerprint, plan.DeployProtocol,
                    currentRelease = _active, plan.TargetRelease, envDiff, appliedMigrations = applied, pendingMigrations = pending,
                    images, effectiveConfigHash = Mode == "digest-mismatch" ? new string('f', 64) : effectiveConfigHash
                },
                secretNotPartOfContract = "synthetic-secret",
                blockers = Array.Empty<string>()
            });
        }

        private static void CheckPinned(string file, IReadOnlyList<string> args, bool stream)
        {
            Assert.Equal("ssh", file);
            Assert.Contains("StrictHostKeyChecking=yes", args);
            Assert.Contains("ForwardAgent=no", args);
            Assert.Contains("ControlPath=none", args);
            Assert.Contains(stream ? "StdinNull=no" : "StdinNull=yes", args);
            Assert.Equal("operator@demo", args[^2]);
        }

        private static Task<SeedProcessResult> Result(string output) => Task.FromResult(new SeedProcessResult(0, output));
        private static Task<SeedProcessResult> Json<T>(T value) => Result(JsonSerializer.Serialize(value, SeedJournal.Json));

        public void Dispose()
        {
            _http.Dispose();
            Release.Dispose();
            Directory.Delete(Root, true);
        }

        private sealed class DeployHttp(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.Null(request.Headers.Authorization);
                var uri = request.RequestUri!;
                HttpContent content;
                if (uri.Host == "api.github.com")
                {
                    Assert.Equal("/repos/muniperez/municode/releases", uri.AbsolutePath);
                    content = new StringContent("""
                        [
                          {"tag_name":"municode-v9.0.0","draft":true,"prerelease":false,"published_at":"2026-11-28T00:00:00Z"},
                          {"tag_name":"workspaces-v9.0.0","draft":false,"prerelease":false,"published_at":"2026-11-28T00:00:00Z"},
                          {"tag_name":"municode-v8.0.0","draft":false,"prerelease":false,"published_at":"2026-11-01T00:00:00Z"},
                          {"tag_name":"municode-v1.0.0-acceptance.7","draft":false,"prerelease":true,"published_at":"2026-11-25T00:00:00Z"},
                          {"tag_name":"municode-v1.2.4","draft":false,"prerelease":false,"published_at":"2026-11-24T00:00:00Z"}
                        ]
                        """);
                }
                else if (uri.Host == "api.example.com")
                {
                    Assert.Equal("/health/version", uri.AbsolutePath);
                    if (fixture.HealthUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag = "municode-v1.2.2", sourceRevision = new string('c', 40), trust = "github-sigstore"
                    }));
                }
                else
                {
                    Assert.Equal("github.com", uri.Host);
                    var name = uri.Segments[^1];
                    content = name.EndsWith(".sigstore.json", StringComparison.Ordinal)
                        ? new StringContent("{}")
                        : new ByteArrayContent(File.ReadAllBytes(Path.Combine(fixture.Release.Root, name)));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        }
    }
}
