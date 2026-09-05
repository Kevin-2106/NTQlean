using NTQlean.Core;

namespace NTQlean.App;

/// <summary>Cross-page shared state for the NTQlean UI.</summary>
public static class AppState
{
    public static Workspace? Workspace;
    public static IReadOnlyDictionary<string, List<string>>? MemoryKeys;

    // Path fields, kept in sync by SourcesView.
    public static string DbDirBoxText = "";
    public static string DataDirBoxText = "";
    public static string WorkspaceBoxText = "";

    public static Action<string>? LogSink;
    public static void WriteLog(string message) => LogSink?.Invoke(message);

    /// <summary>Raised after a workspace index has been (re)built.</summary>
    public static event Action? IndexBuilt;
    public static void NotifyIndexBuilt() => IndexBuilt?.Invoke();

    /// <summary>Latest dry-run report text, shared with the cleanup page.</summary>
    public static string? CleanupReport { get; set; }
    public static CleanupPlan? Plan { get; set; }
    public static event Action? PlanUpdated;
    public static void NotifyPlanUpdated() => PlanUpdated?.Invoke();
}
