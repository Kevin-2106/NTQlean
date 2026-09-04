namespace NTQlean.Core;

/// <summary>
/// Layout of an NTQlean analysis workspace. Everything in here is OUR data
/// (decrypted copies, index, reports); QQ originals are never touched.
/// </summary>
public sealed class Workspace
{
    public string Root { get; }
    public string DecryptedDir => Path.Combine(Root, "decrypted");
    public string IndexPath => Path.Combine(Root, "index.db");
    public string ReportsDir => Path.Combine(Root, "reports");

    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(DecryptedDir);
        Directory.CreateDirectory(ReportsDir);
    }

    public string PlainDbPath(string dbName) =>
        Path.Combine(DecryptedDir, Path.GetFileNameWithoutExtension(dbName) + ".plain.db");

    public IEnumerable<string> ExistingPlainDbs() =>
        Directory.EnumerateFiles(DecryptedDir, "*.plain.db");

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "NTQlean", "workspace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
}
