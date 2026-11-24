using System.Text.Json;

namespace Workspaces.Worker.DevelopmentWorkspaces;

internal enum T3IdleBlocker { UnverifiedSnapshot, ActiveRun, PendingRequest, PendingBackgroundTasks }

internal static class T3WorkspaceEvidence
{
    private static readonly HashSet<string> TerminalStates = ["idle", "completed", "interrupted", "failed", "cancelled", "rolled_back"];

    public static bool IsIdle(JsonElement snapshot) => Blocker(snapshot) is null;

    public static T3IdleBlocker? Blocker(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt64(out var schema) || schema < 1 ||
            !snapshot.TryGetProperty("snapshotSequence", out var sequence) || !sequence.TryGetInt64(out var seq) || seq < 0 ||
            !snapshot.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array ||
            !snapshot.TryGetProperty("archivedThreads", out var archived) || archived.ValueKind != JsonValueKind.Array)
            return T3IdleBlocker.UnverifiedSnapshot;
        return threads.EnumerateArray().Concat(archived.EnumerateArray()).Select(ThreadBlocker).FirstOrDefault(x => x is not null);
    }

    private static T3IdleBlocker? ThreadBlocker(JsonElement thread)
    {
        if (thread.ValueKind != JsonValueKind.Object || !thread.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String || !thread.TryGetProperty("activeRunId", out var run) ||
            !thread.TryGetProperty("pendingRuntimeRequest", out var pending))
            return T3IdleBlocker.UnverifiedSnapshot;
        if (!TerminalStates.Contains(status.GetString()!) || run.ValueKind != JsonValueKind.Null ||
            thread.TryGetProperty("activityRunStatus", out var activity) && activity.ValueKind != JsonValueKind.Null)
            return T3IdleBlocker.ActiveRun;
        if (pending.ValueKind != JsonValueKind.Null ||
            thread.TryGetProperty("blockedOnThreadWait", out var wait) && wait.ValueKind != JsonValueKind.False ||
            thread.TryGetProperty("pendingGuestMessageCount", out var guests) && (!guests.TryGetInt64(out var count) || count != 0))
            return T3IdleBlocker.PendingRequest;
        if (thread.TryGetProperty("pendingBackgroundTasks", out var tasks) && (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() != 0))
            return T3IdleBlocker.PendingBackgroundTasks;
        return null;
    }
}
