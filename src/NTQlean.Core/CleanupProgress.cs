namespace NTQlean.Core;

/// <summary>Per-item progress of a running recycle-bin cleanup.</summary>
public sealed record CleanupProgress(int Completed, int Total, string CurrentName);
