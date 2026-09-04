"""Confirm the exact NTQQ SQLCipher parameters with the working key:
reserve=80 (HMAC-SHA512), full page-1 decrypt + HMAC verification variants."""
import hashlib
import hmac as hmac_mod
import struct
import sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

KEY = "BD156D6710D54D8782F4"
PATH = sys.argv[1] if len(sys.argv) > 1 else "login_copy.db"

data = open(PATH, "rb").read()
payload = data[1024:]
salt = payload[:16]
enc_key = hashlib.pbkdf2_hmac("sha512", KEY.encode(), salt, 4000, dklen=32)
hmac_salt = bytes(b ^ 0x3A for b in salt)
hmac_key = hashlib.pbkdf2_hmac("sha512", enc_key, hmac_salt, 2, dklen=32)

PAGE, RESERVE = 4096, 80
CIPHER_SZ = PAGE - RESERVE  # 4016

for pageno in (1, 2):
    base = (pageno - 1) * PAGE
    off = 16 if pageno == 1 else 0
    buf = payload[base + off: base + PAGE]
    ct, iv, mac = buf[:CIPHER_SZ - off], buf[CIPHER_SZ - off:CIPHER_SZ - off + 16], buf[CIPHER_SZ - off + 16:CIPHER_SZ - off + 16 + 64]
    pt = Cipher(algorithms.AES(enc_key), modes.CBC(iv)).decryptor().update(ct)
    print(f"--- page {pageno}: pt[0:40] = {pt[:40].hex()}")
    if pageno == 1:
        print("    magic check (first 16 salt bytes replaced):", pt[:8].hex(), "... pt[16:32] =", pt[16:32])
    # HMAC variants: alg x pgno endianness; input = ct || iv || pgno
    for alg, hlen in (("sha512", 64), ("sha256", 32), ("sha1", 20)):
        for endian, pg in (("le", struct.pack("<I", pageno)), ("be", struct.pack(">I", pageno))):
            h = hmac_mod.new(hmac_key, ct + iv + pg, getattr(hashlib, alg)).digest()[:hlen]
            if h == mac[:hlen]:
                print(f"    HMAC OK: {alg}, pgno {endian}")

# decrypt several pages and count how many start with a plausible btree page header
ok_pages = 0
total = len(payload) // PAGE
for pageno in range(1, min(total, 64) + 1):
    base = (pageno - 1) * PAGE
    off = 16 if pageno == 1 else 0
    buf = payload[base + off: base + PAGE]
    ct, iv = buf[:CIPHER_SZ - off], buf[CIPHER_SZ - off:CIPHER_SZ - off + 16]
    pt = Cipher(algorithms.AES(enc_key), modes.CBC(iv)).decryptor().update(ct)
    first = pt[off:off + 12] if pageno == 1 else pt[:12]
    ptype = first[0]
    if pageno == 1 and pt[:16 - off].hex().startswith("10000202"):
        ok_pages += 1
    elif ptype in (2, 5, 10, 13) and first[1:3] == b"\x00\x00":
        ok_pages += 1
print(f"plausible btree pages among first {min(total,64)}: {ok_pages}")
