using System.Text.RegularExpressions;
using Workspaces.Common.Contracts;
using Workspaces.Common.Services;

namespace Workspaces.Common.Validation;

public static partial class ControlPlaneValidation
{
    public static ValidationResult ValidateOrganization(CreateOrganizationRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.Name, "name", "Name is required.");
        ValidateSlug(result, request.Slug, "slug", "Organization slug is invalid.");
        return result;
    }

    public static ValidationResult ValidateApiKey(CreateApiKeyRequest request)
    {
        var result = new ValidationResult();
        Require(result, request.OrganizationId, "organizationId", "Organization is required.");
        Require(result, request.Name, "name", "Name is required.");

        if (request.Scopes.Count == 0)
        {
            result.Add("scopes", "At least one scope is required.");
        }

        for (var i = 0; i < request.Scopes.Count; i++)
        {
            if (!ApiKeyScopes.All.Contains(request.Scopes[i]))
            {
                result.Add($"scopes[{i}]", "Scope is not supported.");
            }
        }

        return result;
    }

    public static string ToWireValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
    }

    private static void Require(ValidationResult result, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Add(field, message);
        }
    }

    private static void ValidateSlug(ValidationResult result, string? value, string field, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || !SlugRegex().IsMatch(value))
        {
            result.Add(field, message);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex SlugRegex();
}
