using System.Buffers.Binary;
using System.Text;

namespace NTQlean.Core;

public enum DbFormatKind
{
    Unknown,
    PlainSqlite,
    NtqqEncrypted,      // QQ_NT DB custom header + SQLCipher payload
    EncryptedUnknown,   // high entropy, no recognizable structure
}

public sealed record DbHeaderInfo
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }

    public DbFormatKind Format { get; init; }

    /// <summary>Raw first 64 bytes as hex.</summary>
    public string MagicPreviewHex { get; init; } = "";

    public bool HasWal { get; init; }
    public bool HasShm { get; init; }

    // --- NTQQ custom header fields (only meaningful for NtqqEncrypted) ---
    public int HeaderSize { get; init; }
    public string? NtTag { get; init; }               // "QQ_NT DB" at offset 32
    public uint NtTagTail { get; init; }              // 4 bytes LE after the tag, meaning unknown
    public ushort ClaimedPageSize { get; init; }      // page size claimed by the fake header
    public string? HeaderProtoVersion { get; init; }  // protobuf field 3, e.g. "1.1.0.1"
    public string? HeaderHmacAlgorithm { get; init; } // protobuf field 4, e.g. "HMAC_SHA1"
    public ulong? HeaderProtoVarints { get; init; }   // raw varint fields, reported without semantics
    public string? HeaderAccountIdHex { get; init; }  // protobuf field 2, 128-char hex id (masked)
    public byte[]? KdfSalt { get; init; }             // 16 bytes at HeaderSize (never logged)

    /// <summary>Shannon entropy of a sampled region past the header, 0..8 bits/byte.</summary>
    public double SampledEntropy { get; init; }
}

public static class DbHeaderInspector
{
    public const int NtqqHeaderSize = 1024;
    private const int SampleBytes = 64 * 1024;

    /// <summary>Genuine SQLite magic (only inside decrypted page 1 for NTQQ DBs).</summary>
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// NTQQ's fake header deliberately spells "SQLite header 3" instead of the real
    /// "SQLite format 3" magic, so stock SQLite tooling refuses the file outright.
    /// </summary>
    private static readonly byte[] NtqqFakeMagic = "SQLite header 3\0"u8.ToArray();

    public static DbHeaderInfo Inspect(string path, bool readSalt = true)
    {
        var fi = new FileInfo(path);
        var info = new DbHeaderInfo
        {
            Path = path,
            Size = fi.Length,
            LastWriteTimeUtc = fi.LastWriteTimeUtc,
            HasWal = File.Exists(path + "-wal"),
            HasShm = File.Exists(path + "-shm"),
        };

        using var fs = ReadOnlyFile.Open(path);
        Span<byte> head = stackalloc byte[64];
        // A running QQ may hold byte-range locks on these files; a single Read can
        // return short. Loop so we still capture the magic/tag region when possible.
        var read = 0;
        while (read < head.Length)
        {
            int n;
            try
            {
                n = fs.Read(head[read..]);
            }
            catch (IOException)
            {
                break; // locked range reached — classify from what we have
            }
            if (n <= 0) break;
            read += n;
        }
        info = info with { MagicPreviewHex = ProbeLog.HexPreview(head[..read], 64) };

        bool hasFakeMagic = read >= NtqqFakeMagic.Length && head[..NtqqFakeMagic.Length].SequenceEqual(NtqqFakeMagic);
        bool hasRealMagic = read >= SqliteMagic.Length && head[..SqliteMagic.Length].SequenceEqual(SqliteMagic);
        bool hasNtTag = read >= 40 && head[32..40].SequenceEqual("QQ_NT DB"u8);

        if (hasFakeMagic && hasNtTag)
        {
            // Current NTQQ layout (verified on this machine, QQ 9.9.30).
            var claimedPageSize = BinaryPrimitives.ReadUInt16BigEndian(head[16..]);
            info = InspectNtqqHeader(fs, info with { ClaimedPageSize = claimedPageSize }, claimedPageSize);
        }
        else if (hasRealMagic)
        {
            var claimedPageSize = BinaryPrimitives.ReadUInt16BigEndian(head[16..]);

            // A genuine SQLite header has payload fractions 64/32/32 at offsets 21..23
            // and read/write versions <= 2.
            bool plainLike = head[21] == 64 && head[22] == 32 && head[23] == 32
                             && head[18] <= 2 && head[19] <= 2;

            if (hasNtTag)
            {
                // Older community-reported variant: real magic + QQ_NT DB tag.
                info = InspectNtqqHeader(fs, info with { ClaimedPageSize = claimedPageSize }, claimedPageSize);
            }
            else if (plainLike)
            {
                info = info with { Format = DbFormatKind.PlainSqlite, ClaimedPageSize = claimedPageSize };
            }
            else
            {
                info = info with { Format = DbFormatKind.EncryptedUnknown, ClaimedPageSize = claimedPageSize };
            }
        }
        else
        {
            info = info with { Format = DbFormatKind.EncryptedUnknown };
        }

        if (info.Format == DbFormatKind.NtqqEncrypted && readSalt)
            info = ReadSalt(fs, info);

        info = info with { SampledEntropy = SampleEntropy(path, info.HeaderSize) };
        return info;
    }

    private static DbHeaderInfo InspectNtqqHeader(FileStream fs, DbHeaderInfo info, ushort claimedPageSize)
    {
        Span<byte> tagTailBuf = stackalloc byte[4];
        fs.Seek(40, SeekOrigin.Begin);
        fs.ReadExactly(tagTailBuf);
        var tagTail = BinaryPrimitives.ReadUInt32LittleEndian(tagTailBuf);

        // Protobuf block starts at offset 44 (field 2 onward; field 1 not observed).
        var protoBuf = new byte[NtqqHeaderSize - 44];
        fs.Seek(44, SeekOrigin.Begin);
        var got = 0;
        while (got < protoBuf.Length)
        {
            var n = fs.Read(protoBuf, got, protoBuf.Length - got);
            if (n <= 0) break;
            got += n;
        }
        Array.Resize(ref protoBuf, got);

        string? version = null, hmac = null, accountId = null;
        var varints = new List<ulong>();

        foreach (var f in ProtoWalker.Walk(protoBuf))
        {
            switch (f.WireType)
            {
                case 2:
                    var content = Encoding.ASCII.GetString(protoBuf, f.ValueOffset, f.ValueSize);
                    switch (f.Number)
                    {
                        case 2: accountId = content; break;
                        case 3: version = content; break;
                        case 4: hmac = content; break;
                        default: break; // unknown length-delimited field, not interpreted
                    }
                    break;
                case 0:
                    var v = ReadVarintAt(protoBuf, f.ValueOffset, f.ValueSize);
                    if (v.HasValue) varints.Add(v.Value);
                    break;
            }
        }

        return info with
        {
            Format = DbFormatKind.NtqqEncrypted,
            HeaderSize = NtqqHeaderSize,
            ClaimedPageSize = claimedPageSize,
            NtTag = "QQ_NT DB",
            NtTagTail = tagTail,
            HeaderProtoVersion = version,
            HeaderHmacAlgorithm = hmac,
            HeaderProtoVarints = varints.Count > 0 ? varints[0] : null,
            HeaderAccountIdHex = MaskHex(accountId),
        };
    }

    private static ulong? ReadVarintAt(byte[] buf, int offset, int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++)
            value |= (ulong)(buf[offset + i] & 0x7F) << (7 * i);
        return value;
    }

    private static string? MaskHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        if (hex.Length <= 12) return new string('*', hex.Length);
        return hex[..6] + new string('*', hex.Length - 12) + hex[^6..];
    }

    private static DbHeaderInfo ReadSalt(FileStream fs, DbHeaderInfo info)
    {
        if (info.Size <= NtqqHeaderSize) return info;
        var salt = new byte[16];
        fs.Seek(NtqqHeaderSize, SeekOrigin.Begin);
        if (fs.Read(salt) != 16) return info with { KdfSalt = null };
        return info with { KdfSalt = salt };
    }

    private static double SampleEntropy(string path, int headerSize)
    {
        try
        {
            using var fs = ReadOnlyFile.Open(path);
            fs.Seek(Math.Max(headerSize, 0), SeekOrigin.Begin);
            var buf = new byte[SampleBytes];
            var read = 0;
            while (read < buf.Length)
            {
                var n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return 0;

            Span<int> hist = stackalloc int[256];
            for (var i = 0; i < read; i++) hist[buf[i]]++;
            double entropy = 0;
            for (var i = 0; i < 256; i++)
            {
                if (hist[i] == 0) continue;
                var p = (double)hist[i] / read;
                entropy -= p * Math.Log2(p);
            }
            return entropy;
        }
        catch
        {
            return 0;
        }
    }
}
