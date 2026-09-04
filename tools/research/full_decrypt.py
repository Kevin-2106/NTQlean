"""Full decrypt of login.db copy with the empirically-confirmed layout:
page 4096 / kdf 4000 sha512 / page1 = [salt16][ct 4032][iv@4048][mac@4064]
Writes a plain db and opens it with sqlite3 to read sqlite_master."""
import hashlib
import sqlite3
import sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

KEY = "BD156D6710D54D8782F4"
PATH = sys.argv[1] if len(sys.argv) > 1 else "login_copy.db"
OUT = sys.argv[2] if len(sys.argv) > 2 else "login_plain.db"

PAGE = 4096
RESERVE_CANDIDATES = (48, 80)

data = open(PATH, "rb").read()
payload = data[1024:]
salt = payload[:16]
enc_key = hashlib.pbkdf2_hmac("sha512", KEY.encode(), salt, 4000, dklen=32)

for reserve in RESERVE_CANDIDATES:
    ct_sz = PAGE - reserve
    pages = len(payload) // PAGE
    out = bytearray()
    valid = 0
    for pageno in range(1, pages + 1):
        base = (pageno - 1) * PAGE
        off = 16 if pageno == 1 else 0
        buf = payload[base: base + PAGE]
        ct = buf[off:ct_sz]
        iv = buf[ct_sz:ct_sz + 16]
        pt = Cipher(algorithms.AES(enc_key), modes.CBC(iv)).decryptor().update(ct)
        page = bytearray(PAGE)
        if pageno == 1:
            page[:16] = b"SQLite format 3\x00"
            page[16:16 + len(pt)] = pt
        else:
            page[:len(pt)] = pt
        out += page
        first = pt[0] if pageno > 1 else None
        if pageno == 1 or (first in (2, 5, 10, 13) and pt[1:3] == b"\x00\x00"):
            valid += 1
    print(f"reserve={reserve}: pages={pages} plausible={valid} "
          f"p1header={out[16:32].hex()} size_pages={int.from_bytes(out[28:32], 'big')}")
    hdr_ok = out[16:18] == b"\x10\x00" and out[21:24] == b"\x40\x20\x20"
    if hdr_ok:
        open(OUT, "wb").write(out)
        print(f"wrote {OUT}")
        conn = sqlite3.connect(OUT)
        try:
            rows = conn.execute("SELECT type, name FROM sqlite_master ORDER BY type, name").fetchall()
            print(f"sqlite_master rows: {len(rows)}")
            for t, n in rows:
                print(f"  {t}: {n}")
            for (n,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall():
                try:
                    cnt = conn.execute(f"SELECT count(*) FROM \"{n}\"").fetchone()[0]
                    cols = [c[1] for c in conn.execute(f"PRAGMA table_info(\"{n}\")").fetchall()]
                    print(f"  table {n}: {cnt} rows, cols={cols}")
                except Exception as e:
                    print(f"  table {n}: error {e}")
        finally:
            conn.close()
        break
