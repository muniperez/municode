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
            Assert.Equal("host-agent", root.GetProperty("hostOperation").GetProperty("source").GetString());
            Assert.Equal("inactive", root.GetProperty("hostOperation").GetProperty("unit").GetProperty("activeState").GetString());
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
    public async Task ApplyRequiresTheLocalApprovalAndReportsChangedHostApproval()
    {
        using var fixture = new Fixture();
        var (op, digest) = await fixture.PlanAsync();

        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("apply", op, "--approve", new string('0', 64)));
        Assert.Contains("exactly match", fixture.Console.Error);
        Assert.Empty(fixture.Executions);
        Assert.Equal(0, fixture.StatusCalls);

        fixture.ExecutionMode = "changed";
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("apply", op, "--approve", digest));
        Assert.Contains("--approve", Assert.Single(fixture.Executions));
        Assert.Contains(digest, fixture.Executions[0]);
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        Assert.Equal(new string('f', 64), output.RootElement.GetProperty("liveApprovalDigest").GetString());
        Assert.Contains("live preflight", output.RootElement.GetProperty("blockers")[0].GetString());
    }

    [Fact]
    public async Task ApplyPollsTheTransientUnitStreamsOnlyTheKeyAndVerifiesPublicEndpoints()
    {
        using var fixture = new Fixture { HoldExecutionForPoll = true };
        var (op, digest) = await fixture.PlanAsync();
        var key = Path.Combine(fixture.Root, "offline-key");
        SeedFiles.WriteNew(key, "synthetic-secret_12345\r\n"u8);
        Assert.Equal(CliExitCodes.Success, await fixture.Run("apply", op, "--approve", digest,
            "--unseal-key-file", key, "--drain-timeout", "42"));

        var command = Assert.Single(fixture.Executions);
        Assert.Contains($"systemd-run --unit municode-deploy-{op} --collect --pipe", command);
        Assert.Contains("/opt/municode/releases/municode-v1.2.4/deploy_host.py", command);
        Assert.Contains("--approve", command);
        Assert.Contains(digest, command);
        Assert.Contains("--drain-timeout 42", command);
        Assert.Contains("--unseal-key-stdin", command);
        Assert.Equal("synthetic-secret_12345\n", fixture.ExecutionInput);
        Assert.Equal(2, fixture.StatusCalls);
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        Assert.Equal("committed", output.RootElement.GetProperty("hostOperation").GetProperty("phase").GetString());
        var checks = output.RootElement.GetProperty("publicChecks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "/health", "/health/ready", "/health/version", "/api/account/capabilities", "/runtime-config.json" },
            checks.Select(check => check.GetProperty("endpoint").GetString()));
        Assert.All(checks, check => Assert.True(check.GetProperty("passed").GetBoolean()));
        var journal = File.ReadAllText(Path.Combine(fixture.Root, "deploy", op, "operation.json"));
        Assert.DoesNotContain("synthetic-secret", command + journal + fixture.Console.Output + fixture.Console.Error);
        Assert.DoesNotContain("offline-key", command + journal + fixture.Console.Output + fixture.Console.Error);
    }

    [Theory]
    [InlineData(true, "open", "committed", CliExitCodes.Success)]
    [InlineData(false, "open", "committed", CliExitCodes.Success)]
    [InlineData(false, "migrate", "rolled-back", CliExitCodes.ValidationError)]
    public async Task ResumeFollowsAnActiveUnitOrRestartsWithOriginalApproval(
        bool active, string interruptedPhase, string outcome, int exitCode)
    {
        using var fixture = new Fixture();
        var (op, digest) = await fixture.PlanAsync();
        fixture.ExecutionMode = "disconnect";
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("apply", op, "--approve", digest, "--drain-timeout", "42"));
        Assert.Contains($"deploy resume {op}", fixture.Console.Error);
        fixture.Phase = interruptedPhase;
        fixture.ExecutionMode = outcome == "rolled-back" ? "recover-pre-open" : null;
        if (active) fixture.FinishOnNextPoll = true;

        Assert.Equal(exitCode, await fixture.Run("resume", op));
        Assert.Equal(active ? 1 : 2, fixture.Executions.Count);
        if (!active)
        {
            Assert.Contains(digest, fixture.Executions[1]);
            Assert.Contains("--drain-timeout 42", fixture.Executions[1]);
        }
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        Assert.Equal(outcome, output.RootElement.GetProperty("hostOperation").GetProperty("phase").GetString());
    }

    [Fact]
    public async Task RollbackForwardsAllSafetyFlagsAndPreservesTheExactTimestamp()
    {
        using var fixture = new Fixture();
        var (op, digest) = await fixture.PlanAsync();
        Assert.Equal(CliExitCodes.Success, await fixture.Run("apply", op, "--approve", digest));
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("rollback", op, "--restore-database"));
        Assert.Single(fixture.Executions);

        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("rollback", op));
        using (var blocked = JsonDocument.Parse(fixture.Console.Messages.Last()))
            Assert.Contains("openAt", blocked.RootElement.GetProperty("blockers")[0].GetString());
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("rollback", op,
            "--restore-database", "--accept-data-loss-since", Fixture.OpenAt));
        using (var blocked = JsonDocument.Parse(fixture.Console.Messages.Last()))
            Assert.Contains("provider-reconciliation", blocked.RootElement.GetProperty("blockers")[0].GetString());
        var key = Path.Combine(fixture.Root, "offline-key");
        SeedFiles.WriteNew(key, "synthetic-secret_12345"u8);
        Assert.Equal(CliExitCodes.Success, await fixture.Run("rollback", op, "--restore-database",
            "--accept-data-loss-since", Fixture.OpenAt, "--accept-provider-reconciliation", "--unseal-key-file", key));
        var command = fixture.Executions[^1];
        Assert.Contains(" rollback --restore-database --accept-data-loss-since ", command);
        Assert.Contains(Fixture.OpenAt, command);
        Assert.Contains("--accept-provider-reconciliation", command);
        Assert.Contains("--unseal-key-stdin", command);
        Assert.Equal("synthetic-secret_12345\n", fixture.ExecutionInput);
    }

    [Fact]
    public async Task RollbackBeforeOpeningTrafficDoesNotRequireDataLossFlags()
    {
        using var fixture = new Fixture();
        var (op, digest) = await fixture.PlanAsync();
        fixture.ExecutionMode = "disconnect";
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("apply", op, "--approve", digest));
        fixture.Phase = "migrate";
        fixture.ExecutionMode = null;

        Assert.Equal(CliExitCodes.Success, await fixture.Run("rollback", op));
        Assert.Contains(" rollback --plan ", fixture.Executions[^1]);
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        Assert.Equal("rolled-back", output.RootElement.GetProperty("hostOperation").GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffhostBackupOnlyPublishesAnOwnerOnlyVerifiedSnapshot(bool corrupt)
    {
        using var fixture = new Fixture { CorruptBackup = corrupt };
        var (op, digest) = await fixture.PlanAsync();
        var directory = Path.Combine(fixture.Root, "backups");
        Assert.Equal(corrupt ? CliExitCodes.ValidationError : CliExitCodes.Success,
            await fixture.Run("apply", op, "--approve", digest, "--offhost-backup", directory));
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        var backup = output.RootElement.GetProperty("offhostBackup");
        var target = Path.Combine(directory, op);
        if (corrupt)
        {
            Assert.Equal(JsonValueKind.Null, backup.ValueKind);
            Assert.Contains("SHA-256 mismatch", output.RootElement.GetProperty("blockers")[0].GetString());
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        else
        {
            Assert.Equal(target, backup.GetString());
            SeedFiles.Check(target, directory: true);
            var snapshot = Path.Combine(target, "secrets.tar");
            SeedFiles.Check(snapshot);
            Assert.Equal(Fixture.BackupBytes, File.ReadAllBytes(snapshot));
        }
        Assert.DoesNotContain("synthetic-secret", fixture.Console.Output + fixture.Console.Error);
    }

    [Fact]
    public async Task FailedPublicVerificationLeavesTheCommittedHostUntouched()
    {
        using var fixture = new Fixture { StalePublicVersion = true };
        var (op, digest) = await fixture.PlanAsync();
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("apply", op, "--approve", digest));
        using var output = JsonDocument.Parse(fixture.Console.Messages.Last());
        Assert.Equal("committed", output.RootElement.GetProperty("hostOperation").GetProperty("phase").GetString());
        Assert.Contains("/health/version", output.RootElement.GetProperty("blockers")[0].GetString());
        Assert.Single(fixture.Executions);
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
        public const string OpenAt = "2026-11-27T18:12:34.123456Z";
        public static readonly byte[] BackupBytes = [.. "synthetic-secret"u8, 0x00, 0xff];
        private static readonly string[] BackupFiles = ["active-release.json", "caddy-data.tar", "config.tar",
            "kratos.dump", "municode.dump", "openbao.tar", "postgres-globals.sql", "secrets.tar"];
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
        public string Phase { get; set; } = "staged";
        public string? ExecutionMode { get; set; }
        public List<string> Executions { get; } = [];
        public string? ExecutionInput { get; private set; }
        public int StatusCalls { get; private set; }
        public bool StalePublicVersion { get; init; }
        public bool CorruptBackup { get; init; }
        public bool HoldExecutionForPoll { get; init; }
        public bool FinishOnNextPoll { get; set; }
        private TaskCompletionSource? _poll;

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

        public async Task<(string Id, string Digest)> PlanAsync()
        {
            await RegisterAsync();
            Assert.Equal(CliExitCodes.Success, await Run("plan", "--instance", "demo", "--release", "municode-v1.2.4"));
            using var output = JsonDocument.Parse(Console.Messages.Last());
            return (PreflightPlan!.OperationId, output.RootElement.GetProperty("operation").GetProperty("approvalDigest").GetString()!);
        }

        public Task<SeedProcessResult> RunAsync(string file, IReadOnlyList<string> args, byte[]? input, CancellationToken ct)
        {
            var command = args[^1];
            if (args.Contains("StrictHostKeyChecking=accept-new") || command.Contains("operator.env", StringComparison.Ordinal))
                return _instanceSsh.RunAsync(file, args, input, ct);
            CheckPinned(file, args, false);
            if (command.Contains("mktemp -d", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("sudo", command);
                return Result("/var/tmp/municode-deploy.AbCd012345");
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
            if (command.Contains("systemd-run", StringComparison.Ordinal)) return ExecuteAsync(command, ct);
            if (command.Contains("backup-manifest.json", StringComparison.Ordinal))
                return Json(new
                {
                    format = 1, PreflightPlan!.OperationId,
                    files = BackupFiles.ToDictionary(file => file, _ => Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(BackupBytes)))
                });
            if (command.Contains("state.json", StringComparison.Ordinal)) return Result("latest-on-host");
            Assert.Contains("status --operation", command);
            var op = PreflightPlan!.OperationId;
            var busy = FinishOnNextPoll || _poll is not null;
            StatusCalls++;
            var status = Json(new
            {
                state = HostState(command.Contains(op, StringComparison.Ordinal) ? op : "latest-on-host"),
                unit = new { activeState = busy ? "active" : "inactive", subState = busy ? "running" : "dead" }
            });
            _poll?.TrySetResult();
            if (FinishOnNextPoll)
            {
                FinishOnNextPoll = false;
                Phase = "committed";
            }
            return status;
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
            if (command.Contains("systemd-run", StringComparison.Ordinal))
            {
                using var buffer = new StreamReader(input, leaveOpen: true);
                ExecutionInput = await buffer.ReadToEndAsync(ct);
                return await ExecuteAsync(command, ct);
            }
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

        public async Task<SeedProcessResult> RunDownloadAsync(
            string file, IReadOnlyList<string> args, Stream output, CancellationToken ct)
        {
            CheckPinned(file, args, false);
            var command = args[^1];
            Assert.Contains("sudo -n", command);
            var backup = Assert.Single(BackupFiles, name => command.Contains(
                $"/backups/{PreflightPlan!.OperationId}/{name}", StringComparison.Ordinal));
            await output.WriteAsync(CorruptBackup && backup == "secrets.tar" ? [0x00] : BackupBytes, ct);
            return new(0, "");
        }

        private object HostState(string? op = null) => new
        {
            operationId = op ?? PreflightPlan!.OperationId,
            phase = Phase, PreflightPlan!.TargetRelease,
            pendingStep = Phase == "drain" ? "drain-operations" : null,
            backupCreatedAt = Phase == "committed" ? OpenAt : null,
            trafficMayHaveOpened = Phase is "open" or "committed",
            workerResumed = Phase == "committed",
            openAt = Phase is "open" or "committed" ? OpenAt : null,
            ignoredSecret = "synthetic-secret"
        };

        private async Task<SeedProcessResult> ExecuteAsync(string command, CancellationToken ct)
        {
            Executions.Add(command);
            if (HoldExecutionForPoll)
            {
                Phase = "drain";
                _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await _poll.Task.WaitAsync(ct);
                _poll = null;
            }
            if (ExecutionMode == "disconnect") return new(255, "");
            if (ExecutionMode == "changed")
                return Failure(new { state = HostState(), blockers = new[] { "Approved deploy digest does not match live preflight." },
                    approvalDigest = new string('f', 64) });
            if (ExecutionMode == "recover-pre-open")
            {
                Phase = "rolled-back";
                return Failure(new { state = HostState(), blockers = new[] { "Interrupted deploy rolled back; create a new plan to retry." } });
            }
            if (command.Contains(" rollback ", StringComparison.Ordinal))
            {
                if (Phase is "open" or "committed" && (!command.Contains("--restore-database", StringComparison.Ordinal)
                    || !command.Contains(OpenAt, StringComparison.Ordinal)))
                    return Failure(new { state = HostState(), blockers = new[] { "Rollback requires an exact openAt." } });
                if (Phase == "committed" && !command.Contains("--accept-provider-reconciliation", StringComparison.Ordinal))
                    return Failure(new { state = HostState(), blockers = new[] { "Rollback requires --accept-provider-reconciliation." } });
                Phase = "rolled-back";
            }
            else Phase = "committed";
            return await Json(new { state = HostState() });
        }

        private static SeedProcessResult Failure<T>(T value) => new(1, JsonSerializer.Serialize(value, SeedJournal.Json));

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
                else if (uri.Host == "console.example.com")
                {
                    Assert.Equal("/runtime-config.json", uri.AbsolutePath);
                    content = new StringContent("""
                        {"apiBaseUrl":"https://api.example.com","auth":{"origin":"https://api.example.com",
                        "capabilitiesUrl":"https://api.example.com/api/account/capabilities"}}
                        """);
                }
                else if (uri.Host == "api.example.com")
                {
                    if (fixture.HealthUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    var current = fixture.Phase == "committed" && !fixture.StalePublicVersion;
                    content = new StringContent(uri.AbsolutePath switch
                    {
                        "/health/version" => JsonSerializer.Serialize(new
                        {
                            tag = current ? "municode-v1.2.4" : "municode-v1.2.2",
                            sourceRevision = new string(current ? 'd' : 'c', 40), trust = "github-sigstore"
                        }),
                        "/health" => "Healthy",
                        "/health/ready" => """{"status":"ready"}""",
                        "/api/account/capabilities" => """{"password":true,"bootstrapState":"completed"}""",
                        _ => throw new InvalidOperationException("Unexpected API endpoint.")
                    });
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
