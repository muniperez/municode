using System.Text.Json.Serialization;

namespace Municode.Common.Contracts;

public sealed record AccountCapabilitiesResponse(
    bool Password,
    bool Google,
    bool EmailRecovery,
    string BootstrapState,
    bool KeepSignedIn = false,
    bool Passkey = false);

/// <param name="KeepSignedIn">Whether this session is a remembered ("Keep me signed in") session.</param>
/// <param name="RenewAfter">Earliest time <c>POST /api/account/session/renew</c> extends the idle deadline; null at the cap.</param>
/// <param name="KeepSignedInPreference">The user's stored preference, used as the sign-in form default.</param>
public sealed record AccountSessionResponse(
    string UserId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt,
    string CsrfToken,
    bool KeepSignedIn = false,
    DateTimeOffset? RenewAfter = null,
    bool KeepSignedInPreference = false);

public sealed record AccountSessionPreferenceRequest(bool KeepSignedIn);

public sealed record AccountFlowResponse(
    AccountBrowserFlow? Flow,
    AccountSessionResponse? Session);

public sealed record AccountBrowserFlow(
    string Id,
    string Kind,
    DateTimeOffset ExpiresAt,
    AccountFlowUi Ui);

public sealed record AccountFlowUi(
    string Action,
    string Method,
    IReadOnlyList<AccountFlowNode> Nodes,
    IReadOnlyList<AccountFlowMessage> Messages,
    // Omitted unless an enabled optional provider is offered by this flow.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AccountFlowProvider>? Providers = null);

public sealed record AccountFlowProvider(
    string Id,
    string Action,
    string Method);

public sealed record AccountProviderRedirectResponse(
    string Provider,
    string RedirectUrl);

public sealed record AccountFlowNode(
    string Group,
    string Type,
    string Name,
    string? Value,
    bool Required,
    IReadOnlyList<AccountFlowMessage> Messages,
    // Set only on passkey removal nodes.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AccountFlowPasskey? Passkey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Disabled = false);

/// <param name="DisplayName">The enrolled passkey name; null when Kratos has none.</param>
public sealed record AccountFlowPasskey(string? DisplayName, DateTimeOffset? AddedAt);

public sealed record AccountFlowMessage(int Id, string Text, string Type);
