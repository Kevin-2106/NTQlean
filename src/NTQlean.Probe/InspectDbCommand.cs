using NTQlean.Core;

internal static class InspectDbCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            ProbeLog.Error("usage: ntqlean-probe inspect-db <path>");
            return 1;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            ProbeLog.Error($"file not found: {path}");
            return 1;
        }

        var ids = CollectAccountIds(path);
        var info = DbHeaderInspector.Inspect(path);
        Shared.PrintHeaderReport(info, ids);
        return 0;
    }

    /// <summary>
    /// Walks up from the database path collecting digit-only directory names so
    /// account ids can be masked in all output.
    /// </summary>
    public static List<string> CollectAccountIds(string path)
    {
        var ids = new List<string>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (name.Length is >= 5 and <= 12 && name.All(char.IsAsciiDigit))
                ids.Add(name);
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return ids;
    }
}
