using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NTQlean.Core;

public sealed class KeyDumpResult
{
    /// <summary>Distinct raw keys (64 hex chars) whose embedded salt matched the target DB.</summary>
    public List<string> Keys { get; init; } = new();
    public int ProcessesScanned { get; init; }
    public long BytesScanned { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Extracts NTQQ account database keys from the running QQ client by READ-ONLY
/// external memory scanning (OpenProcess + VirtualQueryEx + ReadProcessMemory).
/// No injection, no writes, no debugging: the target process cannot be affected.
///
/// SQLCipher keyspecs live in memory as:  x'&lt;64 hex key&gt;&lt;32 hex salt&gt;'
/// The 32-hex suffix is the KDF salt, which we already know from the database
/// file itself (offset 1024) — matching it cross-validates that a found key
/// belongs to THIS database.
///
/// Requires: QQ running and the target account logged in. The user must own the
/// machine and the account; reading another person's data is not a supported use.
/// </summary>
public static partial class KeyDumper
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;

    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
        out MemoryBasicInformation lpBuffer, UIntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] buffer, IntPtr size, out IntPtr bytesRead);

    /// <summary>Reads the 16-byte KDF salt from an NTQQ encrypted database.</summary>
    public static byte[] ReadSalt(string encryptedDbPath)
    {
        using var fs = ReadOnlyFile.Open(encryptedDbPath, DbHeaderInspector.NtqqHeaderSize);
        var salt = new byte[16];
        var got = 0;
        while (got < 16)
        {
            var n = fs.Read(salt, got, 16 - got);
            if (n <= 0) break;
            got += n;
        }
        if (got != 16) throw new IOException($"无法从 {encryptedDbPath} 读取 salt（读到 {got} 字节）");
        return salt;
    }

    /// <summary>
    /// Scans all running QQ processes and buckets every SQLCipher keyspec found
    /// by its embedded salt (upper-case hex → distinct 64-hex keys). One pass
    /// covers all open databases of the account (each DB has its own salt).
    /// </summary>
    public static Dictionary<string, List<string>> DumpAllKeyspecs(
        IProgress<string>? progress, out int processesScanned, out long bytesScanned)
    {
        var buckets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        processesScanned = 0;
        bytesScanned = 0;

        var processes = Process.GetProcessesByName("QQ");
        const int ChunkSize = 4 * 1024 * 1024;
        const int Overlap = 128;
        var buffer = new byte[ChunkSize + Overlap];

        foreach (var proc in processes)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                progress?.Report($"扫描 QQ 进程 {proc.Id} …");
                handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, proc.Id);
                if (handle == IntPtr.Zero) continue;
                processesScanned++;

                long address = 0;
                while (true)
                {
                    var mbi = new MemoryBasicInformation();
                    if (VirtualQueryEx(handle, (IntPtr)address, out mbi,
                            (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;

                    var regionBase = mbi.BaseAddress.ToInt64();
                    var regionSize = mbi.RegionSize.ToInt64();
                    var next = regionBase + regionSize;
                    if (next <= address) break;
                    address = next;

                    var readable = mbi.State == MemCommit
                                   && (mbi.Protect & PageGuard) == 0
                                   && (mbi.Protect & PageNoAccess) == 0
                                   && mbi.Protect is 0x02 or 0x04 or 0x20 or 0x08 or 0x40 or 0x80;
                    if (!readable || regionSize <= 0) continue;

                    long offset = 0;
                    while (offset < regionSize)
                    {
                        var take = (int)Math.Min(ChunkSize, regionSize - offset);
                        var back = offset == 0 ? 0 : Math.Min(Overlap, offset);
                        if (!ReadProcessMemory(handle, (IntPtr)(regionBase + offset - back),
                            buffer, (IntPtr)(take + back), out var bytesRead))
                            break;
                        var got = (int)bytesRead;
                        if (got <= 0) break;

                        ScanChunk(buffer.AsSpan(0, got), buckets, ref bytesScanned);
                        offset += take;
                    }
                }
            }
            catch
            {
                // skip processes we cannot read
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                proc.Dispose();
            }
        }

        return buckets.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Convenience wrapper: keys matching one specific salt.</summary>
    public static KeyDumpResult DumpKeysForSalt(byte[] salt, IProgress<string>? progress = null)
    {
        var specs = DumpAllKeyspecs(progress, out var procs, out var bytes);
        var saltHex = Convert.ToHexString(salt);
        var keys = specs.TryGetValue(saltHex, out var list)
            ? list
            : new List<string>();
        return new KeyDumpResult
        {
            Keys = keys,
            ProcessesScanned = procs,
            BytesScanned = bytes,
            Note = procs == 0 ? "无法打开 QQ 进程（权限不足或已退出）" : "",
        };
    }

    private static void ScanChunk(ReadOnlySpan<byte> data,
        Dictionary<string, HashSet<string>> buckets, ref long scanned)
    {
        static byte HexVal(byte c) => (byte)(c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => 0xFF,
        });

        scanned += data.Length;
        int i = 0;
        var lastStart = data.Length - 99;
        while (i < lastStart)
        {
            if (data[i] != (byte)'x' || data[i + 1] != (byte)'\'') { i++; continue; }

            var valid = true;
            for (var k = 2; k < 98; k++)
            {
                if (HexVal(data[i + k]) == 0xFF) { valid = false; break; }
            }
            if (!valid || data[i + 98] != (byte)'\'') { i += 2; continue; }

            var keyChars = new char[64];
            var saltChars = new char[32];
            for (var k = 0; k < 64; k++) keyChars[k] = char.ToUpperInvariant((char)data[i + 2 + k]);
            for (var k = 0; k < 32; k++) saltChars[k] = char.ToUpperInvariant((char)data[i + 2 + 64 + k]);

            var salt = new string(saltChars);
            if (!buckets.TryGetValue(salt, out var set)) buckets[salt] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(new string(keyChars));
            i += 98;
        }
    }
}
