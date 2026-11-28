// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Municode.Cli;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Commands;

namespace Municode.Cli.Tests;

internal sealed class CliTestContext : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateDirectory(Path.Combine(
        Directory.GetCurrentDirectory(), ".local", "artifacts", "issue7", Guid.NewGuid().ToString("N")));

    public CliTestContext()
    {
        Console = new TestConsole();
        Environment = CliEnvironment.ForTests("https://api.example", _directory.FullName);
        Tokens = new TokenStore(Environment);
        Tokens.SaveToken("synthetic-token");
        Handler = new RecordingHandler();
        Client = new MunicodeApiClient(Environment, Tokens, new HttpClient(Handler));
        Application = new CliApplication(Console, Environment, Tokens, Client);
    }

    public TestConsole Console { get; }
    public CliEnvironment Environment { get; }
    public TokenStore Tokens { get; }
    public RecordingHandler Handler { get; }
    public MunicodeApiClient Client { get; }
    public CliApplication Application { get; }

    public Task<int> Run(params string[] args) =>
        Application.RunAsync(["workspaces", .. args], CancellationToken.None);

    public void Dispose() => _directory.Delete(true);
}

internal sealed class TestConsole : IConsole
{
    public bool SupportsAnsi => false;
    public bool IsInteractive { get; set; }
    public string? Input { get; set; }
    public string? ReadLine() => Input;
    public List<string> Messages { get; } = [];
    public List<string> Errors { get; } = [];
    public void Write(string message) => Messages.Add(message);
    public void WriteLine(string message = "") => Messages.Add(message);
    public void WriteError(string message) => Errors.Add(message);
    public string Output => string.Join(Environment.NewLine, Messages);
    public string Error => string.Join(Environment.NewLine, Errors);
}

internal sealed record CapturedRequest(
    string Method, string Path, JsonElement? Body, bool HasBody, string? Authorization);

internal sealed class RecordingHandler : HttpMessageHandler
{
    public const string LaunchUrl = "https://vm-primary-g3.gateway.example/__municode/launch?code=synthetic";
    public List<CapturedRequest> Requests { get; } = [];
    public string[] Organizations { get; set; } = ["org"];
    public string WorkspacePhase { get; set; } = "stopped";
    public string VolumeStatus { get; set; } = "retained";
    public int StorageLayout { get; set; } = 1;
    public string? LastCheckpointAt { get; set; }
    public string? CheckpointStatus { get; set; }
    public string? LastBackupAt { get; set; }
    public string? BackupStatus { get; set; }
    public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.Accepted;
    public bool UnsafeLaunch { get; set; }
    public bool CreateConflict { get; set; }
    public string RecoveryCommit { get; set; } = "b89a48cd27b1b7a423c5c9749316017ad3192eeb";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        JsonElement? body = null;
        var hasBody = request.Content is not null;
        if (hasBody)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            body = document.RootElement.Clone();
        }
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add(new CapturedRequest(
            request.Method.Method, path, body, hasBody, request.Headers.Authorization?.ToString()));

        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/me")
        {
            return Json(HttpStatusCode.OK, new
            {
                id = "usr",
                email = "dev@example.com",
                displayName = "Developer",
                organizations = Organizations.Select(id => new { id, name = id, slug = id, role = "owner" }).ToArray()
            });
        }
        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/cli-login-sessions")
            return Json(HttpStatusCode.Created, new
            {
                sessionId = "cli-login",
                loginUrl = "https://console.example/cli/login?session=cli-login",
                status = "pending",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/cli-login-sessions/cli-login/exchange")
            return Json(HttpStatusCode.OK, new
            {
                token = "synthetic-browser-token",
                email = "dev@example.com",
                organization = new { id = "org", name = "Coding", slug = "coding", role = "owner" },
                scopes = new[] { "workspaces:read", "workspaces:write" }
            });
        if (request.Method == HttpMethod.Post && path == "/api/development-workspaces" && CreateConflict)
            return Json(HttpStatusCode.Conflict, new
            {
                error = new { code = "workspace_idempotency_conflict", message = "Different request.", details = Array.Empty<object>() }
            });
        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/selections", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new
            {
                available = true,
                options = new[]
                {
                    new
                    {
                        imageId = "image", imageName = "Image", profile = "Standard", planId = "plan",
                        planName = "Plan", providerAccountName = "Cloud", region = "region", sku = "sku",
                        cpuCount = 2, memoryGiB = 4, computeHourlyRate = 0.05m, isDefault = true,
                        agents = new[] { new { id = "claude", name = "Claude Code", version = "2.1" } }
                    }
                },
                currency = "EUR"
            });
        if (request.Method == HttpMethod.Get && path.Contains("/vms?organizationId=", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, Vms());
        if (path.Contains("/tasks/", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new
            {
                vmId = "ephemeral", taskId = "build", generation = 3, status = "leased",
                expiresAt = "2026-11-21T23:30:00Z"
            });
        if (path.Contains("/launch?organizationId=", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new
            {
                url = UnsafeLaunch ? "http://unsafe.example/session" : LaunchUrl,
                expiresAt = "2026-11-21T23:30:00Z"
            });
        if (request.Method == HttpMethod.Delete)
            return Json(DeleteStatus, Workspace());
        if (request.RequestUri.AbsolutePath.StartsWith("/api/development-workspaces", StringComparison.Ordinal))
            return Json(request.Method == HttpMethod.Post && path.EndsWith("?organizationId=org", StringComparison.Ordinal)
                    ? HttpStatusCode.OK : HttpStatusCode.OK,
                Workspace());
        return Json(HttpStatusCode.NotFound, new
        {
            error = new { code = "not_found", message = "Synthetic route not found.", details = Array.Empty<object>() }
        });
    }

    private object Workspace() => new
    {
        id = "workspace", organizationId = "org", name = "Coding", imageId = "image", profile = "Standard",
        planId = "plan", providerAccountId = "provider", region = "region", sku = "sku", volumeSizeGb = 20,
        desiredState = "stopped", phase = WorkspacePhase, generation = 3,
        errorCode = WorkspacePhase == "delete_failed" ? "volume_delete_failed" : (string?)null,
        errorMessage = WorkspacePhase == "delete_failed" ? "Restore provider access" : (string?)null,
        estimatedMonthlyCompute = 0m, estimatedMonthlyStorage = 2.5m, currency = "EUR",
        pricingUpdatedAt = "2026-11-21T20:00:00Z", vms = Vms(), volumeStatus = VolumeStatus,
        idleTimeoutMinutes = 45, effectiveIdleTimeoutMinutes = 45, keepRunning = false,
        agents = new[] { new { id = "claude", name = "Claude Code", version = "2.1" } },
        storageLayout = StorageLayout, lastCheckpointAt = LastCheckpointAt, checkpointStatus = CheckpointStatus,
        lastBackupAt = LastBackupAt, backupStatus = BackupStatus
    };

    private object[] Vms() =>
    [
        new
        {
            id = "primary", kind = "primary", generation = 3, desiredState = "running", phase = "ready",
            imageId = "image", profile = "Standard", planId = "plan", providerAccountId = "provider",
            region = "region", sku = "sku", errorCode = (string?)null, errorMessage = (string?)null,
            lastActivityAt = "2026-11-21T20:00:00Z", shutdownDeadline = (string?)null,
            repository = (string?)null, baseRef = (string?)null, baseCommit = (string?)null, workBranch = (string?)null,
            pushAuthorized = false, ignoredDataAcknowledged = false, gitStatus = "none",
            verifiedRemoteRef = (string?)null, verifiedRemoteCommit = (string?)null,
            nextImageId = (string?)null, nextProfile = (string?)null, nextPlanId = (string?)null,
            computeHourlyRate = 0.05m, estimatedMonthlyCompute = 36m, computeReserved = true,
            taskLeases = Array.Empty<object>(), startAfterStop = false
        },
        new
        {
            id = "ephemeral", kind = "ephemeral", generation = 3, desiredState = "stopped", phase = "awaiting_git_save",
            imageId = "image", profile = "Standard", planId = "plan", providerAccountId = "provider",
            region = "region", sku = "sku", errorCode = "remote_unavailable", errorMessage = "Retry after remote recovery.",
            lastActivityAt = "2026-11-21T20:00:00Z", shutdownDeadline = (string?)null,
            repository = "https://git.example/repo.git", baseRef = "refs/heads/main",
            baseCommit = "a1183a9d0b7697213e3f13af040dd2a66ae3241a",
            workBranch = "municode/workspace/3", pushAuthorized = true, ignoredDataAcknowledged = true,
            gitStatus = "awaiting", verifiedRemoteRef = "refs/heads/main", verifiedRemoteCommit = RecoveryCommit,
            nextImageId = (string?)null, nextProfile = (string?)null, nextPlanId = (string?)null,
            computeHourlyRate = 0.05m, estimatedMonthlyCompute = 0m, computeReserved = false,
            taskLeases = Array.Empty<object>(), startAfterStop = false
        }
    ];

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = JsonContent.Create(value) };
}
