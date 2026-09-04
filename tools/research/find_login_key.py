"""One-off research probe: extract the hardcoded login.db key candidate from
wrapper.node by static string scanning, verifying each candidate directly against
a login.db copy (PBKDF2 + single AES block decrypt of page 1).

No QQ process is touched - this is pure file analysis of the installed client."""
import hashlib
import re
import sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

WRAPPER = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files\Tencent\QQNT\versions\9.9.30-48762\resources\app\wrapper.node"
DB = sys.argv[2] if len(sys.argv) > 2 else "login_copy.db"

data = open(DB, "rb").read()
payload = data[1024:]
salt = payload[:16]
ct = payload[16:16 + 4032]      # page-1 ciphertext (page 4096, reserve 48)
iv = payload[16 + 4032:16 + 4032 + 16]

print(f"salt={salt.hex()}")
print(f"iv ={iv.hex()}")

def check(key: str) -> bool:
    enc = hashlib.pbkdf2_hmac("sha512", key.encode(), salt, 4000, dklen=32)
    pt = Cipher(algorithms.AES(enc), modes.CBC(iv)).decryptor().update(ct[:16])
    # plaintext = SQLite file header bytes 16..32: page size 0x1000, versions, fractions
    return (pt[0:2] == b"\x10\x00" and pt[2] in (1, 2)
            and pt[5] == 64 and pt[6] == 32 and pt[7] == 32)

blob = open(WRAPPER, "rb").read()
print(f"wrapper.node: {len(blob):,} bytes")

# locate interesting markers
for marker in (b"SQLite header 3", b"QQ_NT DB", b"nt_sqlite3_key_v2", b"BD156D6710D54D8782F4"):
    idx = blob.find(marker)
    print(f"marker {marker!r}: {'found @' + hex(idx) if idx >= 0 else 'not found'}")

# the known salt bytes?
idx = blob.find(salt)
print(f"salt bytes in wrapper: {'found @' + hex(idx) if idx >= 0 else 'not found'}")

cands = set(m.group().decode("ascii") for m in re.finditer(rb"[\x20-\x7e]{20}", blob))
print(f"20-char candidates: {len(cands):,}")

hits = []
for i, cand in enumerate(cands):
    if check(cand):
        hits.append(cand)
        print(f"HIT: {cand!r}")

if not hits:
    print("no 20-char candidate matched; trying 16-char special-format (#...@...) too")
    cands16 = set(m.group().decode("ascii") for m in re.finditer(rb"[\x20-\x7e]{16}", blob))
    for cand in cands16:
        if check(cand):
            hits.append(cand)
            print(f"HIT-16: {cand!r}")

print("done", "hits:", hits if hits else "NONE")
