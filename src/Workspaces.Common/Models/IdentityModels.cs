using System.ComponentModel.DataAnnotations;

namespace Workspaces.Common.Models;

public sealed class User
{
    [Key]
    public string Id { get; set; } = default!;
    public string? KratosIdentityId { get; set; }
    public string Email { get; set; } = default!;
    public string? DisplayName { get; set; }
    /// <summary>Latest explicit "Keep me signed in" choice; the sign-in form's default for this user.</summary>
    public bool KeepSignedIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrganizationMember> Memberships { get; set; } = [];
}

public sealed class BrowserSession
{
    [Key]
    public string Id { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public User User { get; set; } = default!;
    public string KratosIdentityId { get; set; } = default!;
    public string KratosSessionId { get; set; } = default!;
    public string TokenHash { get; set; } = default!;
    public string CsrfTokenHash { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset IdleExpiresAt { get; set; }
    /// <summary>Start of the current idle window: sign-in or the latest accepted renewal.</summary>
    public DateTimeOffset RenewedAt { get; set; }
    /// <summary>Remembered ("Keep me signed in") session: persistent cookie and remembered policy durations.</summary>
    public bool Persistent { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class BootstrapRecord
{
    [Key]
    public string InstallationId { get; set; } = default!;
    public string OperationId { get; set; } = default!;
    public string State { get; set; } = default!;
    public string? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
    public string? KratosIdentityId { get; set; }
    public string? UserId { get; set; }
    public string? OrganizationId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Organization
{
    [Key]
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<OrganizationMember> Members { get; set; } = [];
    public List<ApiKey> ApiKeys { get; set; } = [];
}

public sealed class OrganizationMember
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public User User { get; set; } = default!;
    public OrganizationRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ApiKey
{
    [Key]
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public Organization Organization { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string KeyPrefix { get; set; } = default!;
    public string KeyHash { get; set; } = default!;
    public string ScopesJson { get; set; } = default!;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CliLoginSession
{
    [Key]
    public string Id { get; set; } = default!;
    public string? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }
    public string Status { get; set; } = default!;
    public string ScopesJson { get; set; } = default!;
    public string? ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ExchangedAt { get; set; }
}

public enum OrganizationRole
{
    Owner,
    Admin,
    Developer,
    Viewer
}
