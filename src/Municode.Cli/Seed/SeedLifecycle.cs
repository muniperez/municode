// SPDX-License-Identifier: MPL-2.0

namespace Municode.Cli.Seed;

internal sealed class SeedLifecycle(
    ISeedProvider provider,
    ISeedHost host,
    SeedJournal journal,
    string releaseDirectory,
    Action<string> write)
{
    public async Task ResumeAsync(
        SeedState state,
        string? approval,
        string? consoleFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResumeCoreAsync(state, approval, consoleFingerprint, cancellationToken);
        }
        catch
        {
            if (!state.IsComplete)
            {
                state.LastFailurePhase = state.Phase;
                state.Phase = state.ServerCreateStarted && state.Server is null
                    ? "reconciliation_required"
                    : state.Phase is "planned" ? "planned" : "failed";
                journal.Save(state);
            }
            throw;
        }
    }

    private async Task ResumeCoreAsync(
        SeedState state,
        string? approval,
        string? consoleFingerprint,
        CancellationToken cancellationToken)
    {
        SeedGuard.Require(provider.CredentialScope(state.Id) == state.Plan.Quote.CredentialScope
            && await provider.GetAccountIdentityAsync(cancellationToken) == state.Plan.Quote.Account,
            "Resume requires the exact provider credential/account scope approved by the plan.");
        if (!state.ServerCreateStarted)
        {
            var fresh = await provider.QuoteAsync(state.Plan.Selection, state.Id, cancellationToken);
            var updated = state.Plan with { Quote = fresh };
            if (updated.Approval != state.Plan.Approval)
            {
                state.Plan = updated;
                journal.Save(state);
            }
            WriteCost(state.Plan);
            if (approval != state.Plan.Approval)
            {
                write("No provider mutations. Confirm the exact displayed costs and rerun apply with "
                    + "--approve " + state.Plan.Approval);
                return;
            }
            await host.CheckPrerequisitesAsync(cancellationToken);
            await host.GenerateSshKeyAsync(journal.DirectoryPath, cancellationToken);
            var publicKey = File.ReadAllText(Path.Combine(
                journal.DirectoryPath, "id_ed25519.pub")).Trim();
            SeedGuard.Require(await provider.FindServerAsync(state.Id, cancellationToken) is null,
                "An operation-owned VPS exists before durable creation intent. Reconcile it; seed will not adopt before intent.");
            state.ServerCreateStarted = true;
            state.Phase = "creating";
            journal.Save(state);
            state.Server = await provider.CreateServerAsync(state, publicKey, cancellationToken);
            state.Phase = state.Server.Address is null ? "creating" : "awaiting_fingerprint";
            journal.Save(state);
        }

        var server = await provider.FindServerAsync(state.Id, cancellationToken);
        if (server is null)
        {
            state.Phase = "reconciliation_required";
            journal.Save(state);
            write("VPS creation outcome is uncertain or the operation-owned server is missing. "
                + "No second create will be sent. Reconcile provider billing/resources by operation "
                + state.Id + " and resume only after the exact single server is present.");
            return;
        }
        if (state.Server is null)
            write("Reconciled the original create by operation ownership; no second create was sent.");
        Match(state, server);
        state.Server = server;
        journal.Save(state);
        if (server.Address is null)
        {
            write($"One billable {state.Plan.Selection.Provider} control-plane VPS {server.Id} "
                + "is still allocating its public IPv4. Resume later; no second create will be sent.");
            return;
        }
        write($"One control-plane VPS retained: {server.Id}, public IPv4 {server.Address}. "
            + "This operation never creates a coding VM.");
        WriteDns(state);
        state.Phase = "awaiting_fingerprint";
        journal.Save(state);
        if (state.HostFingerprint is null && consoleFingerprint is null)
        {
            write("Pause for independent SSH identity. Obtain the ed25519 host fingerprint from the "
                + "provider's authenticated console, then run: municode seed resume " + state.Id
                + " --credential-file <owner-only-path> --confirm-console-fingerprint SHA256:...");
            return;
        }
        if (state.HostFingerprint is null)
            await host.TrustAsync(state, journal.DirectoryPath, consoleFingerprint!, cancellationToken);
        else
            SeedGuard.Require(consoleFingerprint is null || consoleFingerprint == state.HostFingerprint,
                "The established SSH fingerprint is immutable for this operation.");
        journal.Save(state);
        if (!state.HostPrepared)
        {
            state.Phase = "preparing";
            journal.Save(state);
            await host.PrepareAsync(state, journal.DirectoryPath, cancellationToken);
            state.HostPrepared = true;
            journal.Save(state);
        }
        state.Phase = "awaiting_dns";
        journal.Save(state);
        if (!await host.DnsReadyAsync(state, cancellationToken))
        {
            write("DNS is not ready or contains an address other than the operation VPS. "
                + "Apply the exact records above, remove stale/competing AAAA records, and resume; billing continues.");
            return;
        }
        state.Phase = "installing";
        state.InstallStarted = true;
        state.InstallationId ??= "municode-" + state.Id;
        journal.Save(state);
        await host.InstallAsync(state, journal.DirectoryPath, releaseDirectory, cancellationToken);
        state.Phase = "verifying";
        journal.Save(state);
        await host.VerifyHttpsAndHealthAsync(state, cancellationToken);
        state.HealthVerified = true;
        state.Phase = "complete";
        journal.Save(state);
        var setup = state.Plan.Selection.DashboardHostname;
        write($"HTTPS and API health verified for https://{setup}/ and https://{state.Plan.Selection.ApiHostname}/health.");
        write($"First owner: read the protected one-time token from {Path.Combine(journal.DirectoryPath, "setup-token")} "
            + $"and enter it at https://{setup}/setup. Never place it in a URL or paste it into support output.");
        write("After sign-in, explicitly register a provider, then qualify an image and plan. "
            + "The bootstrap credential remains local; seed did not register it and created no coding VM.");
    }

    private void WriteCost(SeedPlan plan)
    {
        foreach (var cost in plan.Quote.Costs)
        {
            write("Cost: " + SeedGuard.FormatCost(cost));
            write("  Exclusions/source: " + cost.Exclusions + " " + cost.Source);
        }
        write("Restrictions: " + plan.Quote.Restrictions);
        write("Approval digest: " + plan.Approval);
    }

    private void WriteDns(SeedState state)
    {
        var address = state.Server!.Address!;
        var selection = state.Plan.Selection;
        write($"DNS record: {selection.DashboardHostname}.  A  {address}");
        write($"DNS record: {selection.ApiHostname}.  A  {address}");
        write($"DNS record: *.{selection.GatewayDomain}.  A  {address}");
        write("DNS must resolve only to the one control-plane IPv4 shown above. "
            + "HTTPS is issued by Caddy after exact public DNS propagation; no provider or DNS credential is sent to the control plane.");
    }

    private static void Match(SeedState state, SeedServer resource)
    {
        SeedGuard.Require(resource.OperationId == state.Id
            && resource.Region == state.Plan.Selection.Region
            && resource.Size == state.Plan.Selection.Size
            && resource.Image == state.Plan.Quote.Image
            && (state.Server is null || state.Server.Id == resource.Id),
            "The provider resource does not match the immutable operation, region, size, image or recorded server ID.");
        if (state.Server?.Address is { } address)
            SeedGuard.Require(resource.Address == address,
                "The control-plane public IPv4 changed. Refusing adoption or reconfiguration.");
    }
}
