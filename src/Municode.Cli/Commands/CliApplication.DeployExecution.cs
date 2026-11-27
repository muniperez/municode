// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Municode.Cli.Deploy;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly TimeSpan DeployPollInterval = TimeSpan.FromSeconds(2);

    private sealed record DeployPublicCheck(string Endpoint, bool Passed);

    private async Task<int> DeployExecuteAsync(string action, string operation,
        Dictionary<string, string> options, bool json, CancellationToken ct)
    {
        using var journal = new DeployJournal(_environment.ConfigHome, operation);
        var local = journal.Load();
        SeedGuard.Require(local.Plan.DeployProtocol == DeployReleaseSource.Protocol,
            "Deploy protocol mismatch; use the CLI from the target release (cli/<rid>/).");
        SeedGuard.Require(DeployApproval.IsDigest(local.ApprovalDigest),
            "This operation has no approved plan. Run deploy plan first.");

        var keyFile = options.GetValueOrDefault("--unseal-key-file");
        DeployExecution execution;
        if (action == "apply")
        {
            SeedGuard.Require(options.GetValueOrDefault("--approve") == local.ApprovalDigest,
                "The approval digest must exactly match deploy plan. Use --approve <digest>.");
            SeedGuard.Require(local.Execution is null && local.Phase == "planned",
                $"Apply has already started. Run 'municode deploy resume {operation}'.");
            int? timeout = null;
            if (options.TryGetValue("--drain-timeout", out var seconds))
            {
                SeedGuard.Require(int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    && value > 0, "--drain-timeout requires a positive whole number of seconds.");
                timeout = value;
            }
            var backup = options.GetValueOrDefault("--offhost-backup");
            execution = new("apply", timeout, backup is null ? null : Path.GetFullPath(backup), keyFile is not null);
        }
        else if (action == "rollback")
        {
            var restore = options.ContainsKey("--restore-database");
            var since = options.GetValueOrDefault("--accept-data-loss-since");
            SeedGuard.Require(restore == (since is not null) && (since is null || DeployTransport.IsTimestamp(since)),
                "--restore-database and --accept-data-loss-since must be used together with the exact openAt timestamp from deploy status.");
            execution = new("rollback", UsesUnsealKey: keyFile is not null,
                RestoreDatabase: restore, AcceptDataLossSince: since,
                AcceptProviderReconciliation: options.ContainsKey("--accept-provider-reconciliation"));
        }
        else
        {
            SeedGuard.Require(local.Execution?.Action is "apply" or "rollback",
                "No apply or rollback has been started for this operation.");
            execution = local.Execution!;
        }

        var instance = await _instances.ShowAsync(local.Plan.InstanceName, ct,
            local.Plan.HostFingerprint, local.Plan.InstallationId);
        var transport = new DeployTransport(_instances, instance);
        var host = await transport.StatusAsync(operation, ct, local.Plan.TargetRelease);

        void Observe(DeployHostOperation observed)
        {
            if (local.Phase == observed.Phase && local.PendingStep == observed.PendingStep) return;
            local = local with { Phase = observed.Phase, PendingStep = observed.PendingStep };
            journal.Save(local);
            if (!json) _console.WriteLine($"[{observed.Phase}]{(observed.PendingStep is { } step ? $" {step}" : "")}");
        }

        if (host.Unit!.IsBusy)
        {
            SeedGuard.Require(action == "resume",
                $"The host deploy unit is already running. Use 'municode deploy resume {operation}' to follow it.");
            if (!json) _console.WriteLine($"Following host deploy unit municode-deploy-{operation}.");
            do
            {
                Observe(host);
                await Task.Delay(DeployPollInterval, ct);
                host = await transport.StatusAsync(operation, ct, local.Plan.TargetRelease);
            } while (host.Unit!.IsBusy);
            return await FinishAsync(OutcomeFromStatus(host, execution));
        }

        if (action == "resume" && (host.Phase == "rolled-back"
            || (execution.Action == "apply" && host.Phase == "committed")))
            return await FinishAsync(OutcomeFromStatus(host, execution));

        if (execution.OffhostBackup is { } directory && action == "apply")
            SeedFiles.CreateDirectory(directory);

        var requiresKeyAgain = execution.UsesUnsealKey
            && (execution.Action == "rollback" || !host.TrafficMayHaveOpened);
        SeedGuard.Require(!requiresKeyAgain || keyFile is not null,
            "This operation used an offline unseal key. Supply --unseal-key-file again to restart the host unit.");
        var keyBytes = keyFile is null ? null : new byte[515];
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        DeployHostResult result;
        try
        {
            using var key = keyFile is null ? null : ReadDeployUnsealKey(keyFile, keyBytes!);
            execution = execution with { UsesUnsealKey = execution.UsesUnsealKey || key is not null };
            local = local with { Execution = execution, Phase = execution.Action == "apply" ? "applying" : "rolling-back",
                PendingStep = null };
            journal.Save(local);
            if (!json) _console.WriteLine($"Starting {execution.Action} in host deploy unit municode-deploy-{operation}.");
            var launch = transport.ExecuteAsync(local, execution, key, follow.Token);
            try
            {
                while (await Task.WhenAny(launch, Task.Delay(DeployPollInterval, follow.Token)) != launch)
                {
                    ct.ThrowIfCancellationRequested();
                    Observe(await transport.StatusAsync(operation, follow.Token, local.Plan.TargetRelease));
                }
                result = await launch;
            }
            finally
            {
                follow.Cancel();
                await ((Task)launch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
        }
        return await FinishAsync(result);

        async Task<int> FinishAsync(DeployHostResult outcome)
        {
            var state = outcome.State;
            if (state is not null) Observe(state);
            var blockers = outcome.Blockers.ToList();
            var committed = execution.Action == "apply" && state?.Phase == "committed";
            var complete = committed || (execution.Action == "rollback" && state?.Phase == "rolled-back");
            SeedGuard.Require(complete || blockers.Count > 0, "The host did not report a terminal deploy outcome.");

            var checks = committed ? await VerifyPublicDeployAsync(instance.Host, local.Plan.TargetRelease, ct) : [];
            foreach (var check in checks.Where(check => !check.Passed))
                blockers.Add($"Public verification failed: {check.Endpoint}. The host release was not rolled back.");

            string? backupPath = null;
            if (state?.BackupCreated == true && execution.OffhostBackup is { } destination)
            {
                try
                {
                    backupPath = await DeployBackup.DownloadAsync(transport, operation, destination, ct);
                }
                catch (CliCommandException error) { blockers.Add(error.Message); }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
                    or FormatException or IOException or UnauthorizedAccessException)
                {
                    blockers.Add("Could not save or verify the protected off-host backup.");
                }
            }

            if (json)
                _console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, operation = local, hostOperation = state, blockers,
                    liveApprovalDigest = outcome.ApprovalDigest, publicChecks = checks, offhostBackup = backupPath
                }, SeedJournal.Json));
            else
            {
                foreach (var check in checks) _console.WriteLine($"[verify] {check.Endpoint}: {(check.Passed ? "ok" : "failed")}");
                if (backupPath is not null) _console.WriteLine($"Verified off-host backup: {backupPath}");
                if (committed) _console.WriteLine($"Deployed {local.Plan.TargetRelease.Tag}. Previous release kept for rollback.");
                else if (state?.Phase == "rolled-back") _console.WriteLine("The host rolled back to the previous release.");
                foreach (var blocker in blockers) _console.WriteError($"Blocker: {blocker}");
                if (outcome.ApprovalDigest is { } digest)
                    _console.WriteError($"Live approval digest: {digest}. Run deploy plan again.");
                if (blockers.Count > 0 && state?.TrafficMayHaveOpened == true && state.Phase != "rolled-back")
                    _console.WriteError($"Use 'municode deploy resume {operation}' to retry or recheck. "
                        + $"See 'municode deploy status {operation}' for the rollback timestamp and worker state.");
            }
            return blockers.Count == 0 ? CliExitCodes.Success : CliExitCodes.ValidationError;
        }
    }

    private static DeployHostResult OutcomeFromStatus(DeployHostOperation host, DeployExecution execution)
    {
        string[] blockers = (execution.Action, host.Phase) switch
        {
            ("apply", "committed") or ("rollback", "rolled-back") => [],
            ("apply", "rolled-back") => ["The interrupted deploy rolled back; create a new plan to retry."],
            _ => [$"The host unit stopped in phase {host.Phase}. Run 'municode deploy resume {host.OperationId}' to continue recovery."]
        };
        return new(host, blockers, null);
    }

    private static MemoryStream ReadDeployUnsealKey(string path, byte[] buffer)
    {
        SeedFiles.Check(path);
        SeedGuard.Require(new FileInfo(path).Length is >= 16 and <= 514,
            "The protected file must contain one valid OpenBao unseal key.");
        using var input = File.OpenRead(path);
        var length = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (length > 0 && buffer[length - 1] == '\n')
        {
            length--;
            if (length > 0 && buffer[length - 1] == '\r') length--;
        }
        SeedGuard.Require(length is >= 16 and <= 512, "The protected file must contain one valid OpenBao unseal key.");
        for (var index = 0; index < length; index++)
        {
            var ch = buffer[index];
            SeedGuard.Require(ch is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'_' or (byte)'-',
                "The protected file must contain one valid OpenBao unseal key.");
        }
        buffer[length] = (byte)'\n';
        return new MemoryStream(buffer, 0, length + 1, writable: false);
    }

    private async Task<DeployPublicCheck[]> VerifyPublicDeployAsync(
        InstanceHost host, DeployReleaseIdentity target, CancellationToken ct)
    {
        const string capabilities = "/api/account/capabilities";
        const string runtimeConfig = "/runtime-config.json";
        var api = new UriBuilder("https", host.ApiHostname).Uri;
        var checks = await Task.WhenAll(
            ProbeAsync(host.ApiHostname, "/health"),
            ProbeAsync(host.ApiHostname, "/health/ready", json => PropertyEquals(json, "status", "ready")),
            CheckVersionAsync(),
            ProbeAsync(host.ApiHostname, capabilities, json => json.TryGetProperty("password", out var password)
                && password.ValueKind is JsonValueKind.True or JsonValueKind.False
                && json.TryGetProperty("bootstrapState", out var bootstrap) && bootstrap.ValueKind == JsonValueKind.String),
            ProbeAsync(host.DashboardHostname, runtimeConfig, json =>
                UrlEquals(json, "apiBaseUrl", api) && json.TryGetProperty("auth", out var auth)
                && UrlEquals(auth, "origin", api) && UrlEquals(auth, "capabilitiesUrl", new Uri(api, capabilities))));
        return checks;

        async Task<DeployPublicCheck> CheckVersionAsync()
        {
            var (version, _) = await ReadPublicDeployVersionAsync(host.ApiHostname, ct);
            return new("/health/version", version == new PublicDeployVersion(target.Tag, target.SourceRevision, target.Trust));
        }

        async Task<DeployPublicCheck> ProbeAsync(string hostname, string endpoint, Func<JsonElement, bool>? validate = null)
        {
            try
            {
                using var response = await _deployHttp.GetAsync(new UriBuilder("https", hostname) { Path = endpoint }.Uri, ct);
                if (!response.IsSuccessStatusCode) return new(endpoint, false);
                if (validate is null) return new(endpoint, true);
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                return new(endpoint, body.RootElement.ValueKind == JsonValueKind.Object && validate(body.RootElement));
            }
            catch (HttpRequestException) { return new(endpoint, false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(endpoint, false); }
            catch (JsonException) { return new(endpoint, false); }
        }

        static bool PropertyEquals(JsonElement json, string name, string expected) =>
            json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

        static bool UrlEquals(JsonElement json, string name, Uri expected) =>
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url)
            && url == expected && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
    }
}
