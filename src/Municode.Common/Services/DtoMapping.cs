using System.Text.Json;
using Municode.Common.Contracts;
using Municode.Common.Models;

namespace Municode.Common.Services;

public static class DtoMapping
{
    public static OrganizationResponse ToResponse(this Organization organization) =>
        new(organization.Id, organization.Name, organization.Slug, organization.CreatedAt);

    public static ApiKeyResponse ToResponse(this ApiKey apiKey)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(apiKey.ScopesJson) ?? [];
        return new ApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            apiKey.KeyPrefix,
            scopes,
            apiKey.LastUsedAt,
            apiKey.ExpiresAt,
            apiKey.RevokedAt,
            apiKey.CreatedAt);
    }
}
