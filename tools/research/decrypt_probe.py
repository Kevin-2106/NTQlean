"""One-off research probe: try SQLCipher parameter matrices against an NTQQ DB copy
and print the decrypted page-1 preview, so we can SEE what the right layout is."""
import hashlib
import itertools
import sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

HEADER = 1024
KEY = sys.argv[1] if len(sys.argv) > 1 else "BD156D6710D54D8782F4"
PATH = sys.argv[2] if len(sys.argv) > 2 else "login_copy.db"

data = open(PATH, "rb").read()
payload = data[HEADER:]
print(f"file={PATH} payload={len(payload)}")

def kdf(pass_, salt, iters, alg):
    return hashlib.pbkdf2_hmac(alg, pass_, salt, iters, dklen=32)

def try_cfg(page, iters, kdf_alg, salt, page1_offset):
    hmac_sz = 20 if False else 20  # HMAC-SHA1 => reserve 48; sha512 => 80; sha256 => 48
    # try all three hmac sizes since IV position depends on reserve
    for hmac_alg, hmac_len in (("sha1", 20), ("sha256", 32), ("sha512", 64)):
        reserve_raw = 16 + hmac_len
        reserve = reserve_raw if reserve_raw % 16 == 0 else ((reserve_raw // 16) + 1) * 16
        cipher_sz = page - reserve
        enc_key = kdf(KEY.encode(), salt, iters, kdf_alg)
        for pageno in (1,):
            off = page1_offset if pageno == 1 else 0
            buf = payload[(pageno - 1) * page:]
            ct = buf[off:off + cipher_sz - off]
            iv = buf[cipher_sz:cipher_sz + 16]
            if len(ct) % 16 or len(ct) == 0:
                continue
            c = Cipher(algorithms.AES(enc_key), modes.CBC(iv))
            pt = c.decryptor().update(ct)
            yield (page, iters, kdf_alg, hmac_alg, reserve, page1_offset), pt

salt_payload = payload[:16]          # bytes at offset 1024
salt_fakehdr = data[:16]             # "SQLite header 3\0"

print("salt@1024:", salt_payload.hex())
print("fake hdr :", salt_fakehdr.hex())

results = []
for page, iters, kdf_alg, page1_off in itertools.product(
    (4096, 1024), (4000, 256000, 64000), ("sha512", "sha1"), (16, 0)
):
    for salt_name, salt in (("payload", salt_payload), ("fakehdr", salt_fakehdr)):
        for cfg, pt in try_cfg(page, iters, kdf_alg, salt, page1_off):
            preview = pt[:24].hex()
            # header check on the reconstructed first page:
            # page1off=16 => pt holds bytes 16.. of the header (pt[0:2]=page size 0x1000)
            # page1off=0  => pt starts with the magic itself
            if page1_off == 16:
                ok = pt[0:2] == b"\x10\x00" if page == 4096 else pt[0:2] == b"\x04\x00"
                ok = ok and pt[2] in (1, 2) and pt[5] == 64 and pt[6] == 32 and pt[7] == 32
            else:
                ok = pt[0:16] == b"SQLite format 3\x00"
            results.append((cfg, salt_name, preview, 1 if ok else 0, pt[:32].hex()))

hits = [r for r in results if r[3]]
print(f"total combos tried: {len(results)}, header-valid hits: {len(hits)}")
for cfg, salt_name, preview, ok, pt in (hits + [r for r in results if not r[3]][:12]):
    tag = "HIT " if ok else "    "
    print(f"{tag} page={cfg[0]} iters={cfg[1]} kdf={cfg[2]} hmac={cfg[3]} reserve={cfg[4]} "
          f"p1off={cfg[5]} salt={salt_name} pt={pt}")
