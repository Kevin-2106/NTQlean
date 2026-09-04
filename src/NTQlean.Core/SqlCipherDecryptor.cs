using System.Security.Cryptography;
using System.Text;

namespace NTQlean.Core;

public enum HmacAlgorithm { HmacSha1, HmacSha256, HmacSha512 }

public sealed record SqlCipherConfig
{
    public int PageSize { get; init; } = 4096;
    public int KdfIterations { get; init; } = 4000;
    public bool KdfUseSha512 { get; init; } = true;   // cipher_kdf_algorithm
    public HmacAlgorithm Hmac { get; init; } = HmacAlgorithm.HmacSha1;
    public int PlaintextHeaderSize { get; init; } = 1024;

    public int ReserveSize
    {
        get
        {
            var hmacSz = Hmac switch
            {
                HmacAlgorithm.HmacSha1 => 20,
                HmacAlgorithm.HmacSha256 => 32,
                _ => 64,
            };
            var raw = 16 + hmacSz; // IV + HMAC
            return (raw % 16) == 0 ? raw : ((raw / 16) + 1) * 16;
        }
    }
}

public sealed class SqlCipherDecryptResult
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = "";
    public SqlCipherConfig Config { get; init; } = new();
    public long PagesDecrypted { get; init; }
    public long PagesHmacOk { get; init; }
    public long PagesHmacBad { get; init; }
    public bool RawKeyMode { get; init; }
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Streaming SQLCipher page decryptor, mirroring sqlcipher.c (v4 semantics):
///   enc_key  = PBKDF2-HMAC-(&lt;kdf_alg&gt;)(passphrase, salt[16], kdf_iter, 32)
///   hmac_salt = salt[16] XOR 0x3a
///   hmac_key = PBKDF2-HMAC-(&lt;kdf_alg&gt;)(enc_key, hmac_salt, fast_kdf_iter=2, 32)
/// Per page (reserve = align16(IV + HMAC)):
///   [ciphertext page_sz-reserve][IV 16][HMAC hmac_sz][pad]
/// HMAC input = ciphertext || IV || page-number (4-byte little endian, native order).
/// Page 1 keeps a 16-byte plaintext salt in place of the SQLite magic.
/// </summary>
public static class SqlCipherDecryptor
{
    private const int KeySize = 32;
    private const int IvSize = 16;
    private const byte HmacSaltMask = 0x3a;
    private const int FastKdfIterations = 2;
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// Decrypts a working COPY of an NTQQ database (never the QQ original) into a
    /// plain SQLite file. The output is written page by page; input must be
    /// plaintextHeaderSize + n * pageSize bytes.
    /// </summary>
    public static SqlCipherDecryptResult DecryptCopy(
        string encryptedCopyPath,
        string outputPath,
        string key,
        SqlCipherConfig config,
        long? maxPages = null)
    {
        try
        {
            var (encKey, hmacKey, rawMode) = DeriveKeys(encryptedCopyPath, key, config, out var salt);
            return DecryptCopyWithKeys(encryptedCopyPath, outputPath, config, encKey, hmacKey, salt, rawMode, maxPages);
        }
        catch (Exception ex)
        {
            return new SqlCipherDecryptResult { Success = false, Error = ex.Message, Config = config };
        }
    }

    private static SqlCipherDecryptResult DecryptCopyWithKeys(
        string encryptedCopyPath,
        string outputPath,
        SqlCipherConfig config,
        byte[] encKey,
        byte[] hmacKey,
        byte[] salt,
        bool rawMode,
        long? maxPages)
    {
        const int header = DbHeaderInspector.NtqqHeaderSize;
        var pageSz = config.PageSize;
        var reserve = config.ReserveSize;
        var hmacSz = config.Hmac switch
        {
            HmacAlgorithm.HmacSha1 => 20,
            HmacAlgorithm.HmacSha256 => 32,
            _ => 64,
        };
        var cipherSz = pageSz - reserve;

        var fi = new FileInfo(encryptedCopyPath);
        var payloadLen = fi.Length - header;
        if (payloadLen <= 0)
            return new SqlCipherDecryptResult { Success = false, Error = "file has no data past the NTQQ header", Config = config };

        var pages = payloadLen / pageSz;
        var truncated = false;
        if (maxPages is > 0 && maxPages.Value < pages)
        {
            pages = maxPages.Value; // research mode: decrypt only the first N pages
            truncated = true;
        }
        if (payloadLen % pageSz != 0)
            return new SqlCipherDecryptResult
            {
                Success = false,
                Error = $"payload size {payloadLen} is not a multiple of page size {pageSz} " +
                                $"(NTQQ header assumed {header} bytes)",
                Config = config,
            };

        using var input = new FileStream(encryptedCopyPath, FileMode.Open, FileAccess.Read, ReadOnlyFile.PermissiveShare);
        var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var hmacOk = 0L;
        var hmacBad = 0L;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 256;
        aes.Key = encKey;

        var pageBuf = new byte[pageSz];
        var plainBuf = new byte[pageSz];

        for (var page = 1L; page <= pages; page++)
        {
            var pageFileOffset = header + (page - 1) * pageSz;
            var dataOffset = page == 1 ? IvSize : 0;
            var dataLen = cipherSz - dataOffset;

            input.Seek(pageFileOffset + dataOffset, SeekOrigin.Begin);
            input.ReadExactly(pageBuf, 0, pageSz - dataOffset);

            // Layout inside the buffer read at pageFileOffset + dataOffset:
            //   [ciphertext dataLen][IV 16][HMAC hmacSz][pad ...]
            var iv = pageBuf.AsSpan(dataLen, IvSize);
            var hmacStored = pageBuf.AsSpan(dataLen + IvSize, hmacSz);

            var expected = new byte[hmacSz];
            using (var h = CreateHmac(config.Hmac, hmacKey))
            {
                h.TransformBlock(pageBuf, 0, dataLen + IvSize, null, 0);
                var pgno = BitConverter.GetBytes((int)page); // native (little endian on x64)
                h.TransformFinalBlock(pgno, 0, pgno.Length);
                Array.Copy(h.Hash!, expected, hmacSz);
            }
            if (expected.AsSpan().SequenceEqual(hmacStored)) hmacOk++; else hmacBad++;

            byte[] plain;
            aes.IV = iv.ToArray(); // per-page IV stored in the page reserve
            using (var enc = aes.CreateDecryptor())
                plain = enc.TransformFinalBlock(pageBuf, 0, dataLen);

            if (page == 1)
            {
                SqliteMagic.AsSpan().CopyTo(plainBuf);
                plain.AsSpan(0, dataLen).CopyTo(plainBuf.AsSpan(IvSize));
            }
            else
            {
                plain.AsSpan(0, pageSz - reserve).CopyTo(plainBuf);
            }
            Array.Clear(plainBuf, pageSz - reserve, reserve); // reserved tail stays zero

            output.Write(plainBuf, 0, pageSz);
        }

        output.Flush();
        output.Dispose();

        if (truncated)
        {
            // Keep the truncated image internally consistent: patch the header's
            // "database size in pages" field (offset 28, big endian).
            using var patch = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            Span<byte> sizeField = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sizeField, (uint)pages);
            patch.Seek(28, SeekOrigin.Begin);
            patch.Write(sizeField);
            patch.Flush();
        }

        // Verify the reconstructed header of page 1 via a fresh read-only handle.
        bool headerLooksValid;
        using (var verify = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> headerCheck = stackalloc byte[32];
            verify.ReadExactly(headerCheck);
            headerLooksValid =
                headerCheck[..16].SequenceEqual(SqliteMagic) &&
                headerCheck[21] == 64 && headerCheck[22] == 32 && headerCheck[23] == 32 &&
                headerCheck[18] is 1 or 2;
        }

        if (!headerLooksValid)
        {
            string preview;
            using (var dbg = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> b = stackalloc byte[32];
                dbg.ReadExactly(b);
                preview = ProbeLog.HexPreview(b, 32);
            }
            File.Delete(outputPath);
            return new SqlCipherDecryptResult
            {
                Success = false,
                Error = "decrypted page 1 does not contain a valid SQLite header " +
                                $"(wrong key or wrong cipher parameters); pt16..48={preview}",
                Config = config,
                RawKeyMode = rawMode,
            };
        }

        return new SqlCipherDecryptResult
        {
            Success = true,
            OutputPath = outputPath,
            Config = config,
            PagesDecrypted = pages,
            PagesHmacOk = hmacOk,
            PagesHmacBad = hmacBad,
            RawKeyMode = rawMode,
            Truncated = truncated,
        };
    }

    private static (byte[] EncKey, byte[] HmacKey, bool RawMode) DeriveKeys(
        string encryptedCopyPath, string key, SqlCipherConfig config, out byte[] salt)
    {
        // The KDF salt is the first 16 bytes of the SQLCipher payload (right after
        // the NTQQ plaintext header) in the working copy.
        salt = new byte[IvSize];
        using (var fs = new FileStream(encryptedCopyPath, FileMode.Open, FileAccess.Read, ReadOnlyFile.PermissiveShare))
        {
            fs.Seek(config.PlaintextHeaderSize, SeekOrigin.Begin);
            fs.ReadExactly(salt);
        }

        var kdfHash = config.KdfUseSha512 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA1;

        // Raw key forms: x'<64 hex>' or bare 64-char hex.
        var trimmed = key.Trim();
        if (trimmed.StartsWith("x'", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("'"))
            trimmed = trimmed[2..^1];
        if (trimmed.Length == KeySize * 2 && IsHex(trimmed))
        {
            var encKey = Convert.FromHexString(trimmed);
            return (encKey, DeriveHmacKey(encKey, salt, kdfHash), true);
        }

        var pass = Encoding.UTF8.GetBytes(key);
        byte[] derived;
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(pass, salt, config.KdfIterations, kdfHash);
            derived = pbkdf2.GetBytes(KeySize);
        }
        return (derived, DeriveHmacKey(derived, salt, kdfHash), false);
    }

    private static byte[] DeriveHmacKey(byte[] encKey, byte[] salt, HashAlgorithmName kdfHash)
    {
        var hmacSalt = new byte[salt.Length];
        Array.Copy(salt, hmacSalt, salt.Length);
        for (var i = 0; i < hmacSalt.Length; i++) hmacSalt[i] ^= HmacSaltMask;
        using var pbkdf2 = new Rfc2898DeriveBytes(encKey, hmacSalt, FastKdfIterations, kdfHash);
        return pbkdf2.GetBytes(KeySize);
    }

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    private static HMAC CreateHmac(HmacAlgorithm alg, byte[] key) => alg switch
    {
        HmacAlgorithm.HmacSha1 => new HMACSHA1(key),
        HmacAlgorithm.HmacSha256 => new HMACSHA256(key),
        _ => new HMACSHA512(key),
    };
}
