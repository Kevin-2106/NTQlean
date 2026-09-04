using NTQlean.Core;

internal static class DiscoverCommand
{
    public static int Run(string[] args)
    {
        ProbeLog.Info("NTQQ discovery (read-only; known locations only, no drive-wide scan)");

        var install = NtqqDiscovery.FindInstallDir();
        if (install is not null)
            ProbeLog.Info($"NTQQ install dir: {install}");
        else
            ProbeLog.Warn("NTQQ install dir not found in standard Program Files locations");

        if (IsQqRunning())
            ProbeLog.Warning("QQ.exe is currently RUNNING. Copies taken now may be inconsistent " +
                             "(wal/shm mid-flight). Close QQ fully for the most reliable snapshots.");

        var roots = NtqqDiscovery.Discover();
        if (roots.Count == 0)
        {
            ProbeLog.Warning("No 'Tencent Files' data root found in known locations:");
            ProbeLog.Warning("  - shell-known Documents dir (may be OneDrive-redirected)");
            ProbeLog.Warning("  - %USERPROFILE%\\Documents");
            ProbeLog.Warning("  - <drive>\\Tencent Files on fixed drives");
            ProbeLog.Warning("Not scanned (by design): full-drive recursive search.");
            return 1;
        }

        foreach (var root in roots)
        {
            ProbeLog.Info($"Data root: {root.Root}  ({root.Source})");
            if (root.GlobalNtDb is not null)
                ProbeLog.Info($"  global nt_db: {root.GlobalNtDb}");

            if (root.Accounts.Count == 0)
                ProbeLog.Warn("  no account directories found");

            foreach (var account in root.Accounts)
            {
                ProbeLog.Info($"  Account {ProbeLog.MaskId(account.Uin)}:");
                if (account.NtDbDir is not null)
                {
                    ProbeLog.Info($"    nt_db:    {account.NtDbDir}");
                    ProbeLog.Info($"    DB total: {account.DbTotalBytes:N0} bytes");
                }
                if (account.NtDataDir is not null)
                    ProbeLog.Info($"    nt_data:  {account.NtDataDir}");
            }
        }

        // Header classification for the most active account of the first root.
        var primary = roots
            .SelectMany(r => r.Accounts)
            .OrderByDescending(a => a.DbTotalBytes)
            .FirstOrDefault();
        if (primary?.NtDbDir is not null)
        {
            ProbeLog.Info($"Format survey of largest account ({ProbeLog.MaskId(primary.Uin)}):");
            foreach (var db in Directory.EnumerateFiles(primary.NtDbDir, "*.db").Take(30))
            {
                try
                {
                    var info = DbHeaderInspector.Inspect(db, readSalt: false);
                    ProbeLog.Info($"  {Path.GetFileName(db)}: {info.Format} ({info.Size:N0} bytes)");
                }
                catch (Exception ex)
                {
                    ProbeLog.Warn($"  {Path.GetFileName(db)}: inspection failed ({ex.Message})");
                }
            }
        }

        ProbeLog.Info("Key acquisition status:");
        ProbeLog.Info("  login.db            — hardcoded public key (automatic, no injection)");
        ProbeLog.Info("  account databases   — per-account key; community methods require QQ process " +
                       "access (debugger/memory scan) which NTQlean does not perform. " +
                       "Use 'open-db' with an interactively supplied key.");
        ProbeLog.Blocked("Known automatic key extraction for nt_msg.db requires QQ process " +
                         "injection / debugging / memory reading — not performed by NTQlean");

        return 0;
    }

    private static bool IsQqRunning()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("QQ"))
            {
                p.Dispose();
                return true;
            }
        }
        catch
        {
            // best-effort check only
        }
        return false;
    }
}
