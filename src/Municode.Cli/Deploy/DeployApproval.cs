// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Deploy;

internal sealed record DeployPlan(
    string OperationId, string InstanceName, string InstallationId, string HostFingerprint,
    int DeployProtocol, DeployReleaseIdentity TargetRelease);

internal static class DeployApproval
{
    public static bool IsDigest(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-f0-9]{64}\z");

    public static string ValidateAndCompute(DeployPlan plan, InstanceRelease active, JsonElement preflight)
    {
        var inputs = preflight.GetProperty("digestInputs");
        var current = inputs.GetProperty("currentRelease").Deserialize<InstanceRelease>(SeedJournal.Json);
        var target = inputs.GetProperty("targetRelease").Deserialize<DeployReleaseIdentity>(SeedJournal.Json);
        SeedGuard.Require(current == active && target == plan.TargetRelease
            && preflight.GetProperty("targetRelease").Deserialize<DeployReleaseIdentity>(SeedJournal.Json) == target
            && inputs.GetProperty("instanceName").GetString() == plan.InstanceName
            && inputs.GetProperty("installationId").GetString() == plan.InstallationId
            && inputs.GetProperty("hostFingerprint").GetString() == plan.HostFingerprint,
            "The host preflight identities do not match the pinned instance and staged release. Run deploy plan again.");
        SeedGuard.Require(preflight.GetProperty("deployProtocol").GetInt32() == plan.DeployProtocol
            && inputs.GetProperty("deployProtocol").GetInt32() == plan.DeployProtocol,
            "Host deploy protocol mismatch; use the CLI from the target release (cli/<rid>/).");
        var effective = inputs.GetProperty("effectiveConfigHash").GetString();
        var migrations = preflight.GetProperty("migrations");
        SeedGuard.Require(preflight.GetProperty("composeConfigValid").GetBoolean() && IsDigest(effective)
            && preflight.GetProperty("effectiveConfigHash").GetString() == effective
            && JsonElement.DeepEquals(preflight.GetProperty("envDiff"), inputs.GetProperty("envDiff"))
            && JsonElement.DeepEquals(migrations.GetProperty("applied"), inputs.GetProperty("appliedMigrations"))
            && JsonElement.DeepEquals(migrations.GetProperty("pending"), inputs.GetProperty("pendingMigrations"))
            && inputs.GetProperty("images").ValueKind == JsonValueKind.Array,
            "The host preflight returned inconsistent approval digest inputs.");
        return Compute(inputs);
    }

    public static Dictionary<string, JsonElement> Summary(JsonElement preflight)
    {
        var summary = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "composeConfigValid", "envDiff", "serviceHealth", "imagesToPull",
                     "postgres", "disk", "migrations", "maintenance", "openBao", "effectiveConfigHash" })
            if (preflight.TryGetProperty(name, out var value)) summary.Add(name, value);
        if (preflight.TryGetProperty("digestInputs", out var inputs) && inputs.TryGetProperty("images", out var images))
            summary.Add("images", images);
        return summary;
    }

    public static string Compute(JsonElement inputs)
    {
        // Match the host agent's json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=True).
        var canonical = new StringBuilder();
        Append(inputs, canonical);
        return SeedRelease.Hash(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static void Append(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var separator = "";
                foreach (var field in value.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    output.Append(separator);
                    AppendString(field.Name, output);
                    output.Append(':');
                    Append(field.Value, output);
                    separator = ",";
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var comma = "";
                foreach (var element in value.EnumerateArray())
                {
                    output.Append(comma);
                    Append(element, output);
                    comma = ",";
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(value.GetString()!, output);
                break;
            default:
                output.Append(value.GetRawText());
                break;
        }
    }

    private static void AppendString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var ch in value)
            output.Append(ch switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or >= '\u007f' => $"\\u{(int)ch:x4}",
                _ => ch.ToString()
            });
        output.Append('"');
    }
}
