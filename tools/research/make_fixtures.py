"""Creates a synthetic NTQQ-like fixture workspace for integration testing:
plain (already-decrypted) account databases + a fake nt_data tree.
No QQ data involved. Used by tools/research/run_integration_test.sh."""
import os
import random
import sqlite3
import struct
import sys

BASE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ["TEMP"], "ntqlean-it")
random.seed(42)

nt_db = os.path.join(BASE, "plain_dbs")
nt_data = os.path.join(BASE, "nt_data")
for d in [nt_db, nt_data,
          os.path.join(nt_data, "Pic", "2025-06", "Ori"), os.path.join(nt_data, "Pic", "2025-06", "Thumb"),
          os.path.join(nt_data, "Pic", "2024-01", "Ori"),
          os.path.join(nt_data, "Video", "2025-08", "Ori"),
          os.path.join(nt_data, "File", "2025-07")]:
    os.makedirs(d, exist_ok=True)

def md5name(i):
    return f"{i:032x}"

files = {}

def put(rel, data):
    p = os.path.join(nt_data, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, "wb").write(data)
    files[os.path.basename(rel)] = (rel, len(data))
    return data

# --- media files -----------------------------------------------------------
# img1: referenced only by name (path empty in DB), exact size
img1 = put(f"Pic/2025-06/Ori/{md5name(1)}.jpg", b"\xff\xd8" + os.urandom(2 * 1024 * 1024 - 2))
# img2 thumb
put(f"Pic/2025-06/Thumb/{md5name(1)}_720.jpg", b"\xff\xd8" + os.urandom(40_000))
# img2: referenced by path, size recorded in KB (45405 = KB)
img2 = put(f"Pic/2024-01/Ori/{md5name(2)}.png", os.urandom(512 * 1024))
# vid1: referenced by path, size exact bytes
vid1 = put(f"Video/2025-08/Ori/{md5name(3)}.mp4", os.urandom(30 * 1024 * 1024))
# doc: referenced by path with nt_data/ prefix, size mismatch -> strong
doc = put(f"File/2025-07/report.pdf", os.urandom(4 * 1024 * 1024))
# orphan: nothing references this
orph_size = 8 * 1024 * 1024
orph = put(f"Pic/2025-06/Ori/{md5name(9)}.jpg", os.urandom(orph_size))

# --- files_in_chat.db ------------------------------------------------------
fic = sqlite3.connect(os.path.join(nt_db, "files_in_chat.plain.db"))
fic.execute("""CREATE TABLE files_in_chat_table (
  "40001" INTEGER, "45001" INTEGER, "45402" TEXT, "45403" TEXT, "45404" TEXT,
  "45405" INTEGER, "40050" INTEGER, "40021" TEXT, "40010" INTEGER)""")
# (msgid, seq, name, path, thumb, size, time, peer, chatType)
fic.execute('INSERT INTO files_in_chat_table VALUES (1001,1,?,?,?,?,?,?,?)',
            (md5name(1) + ".jpg", "", f"Pic/2025-06/Thumb/{md5name(1)}_720.jpg",
             len(img1), 1748739100, "987654321", 2))                     # group image, name-ref
fic.execute('INSERT INTO files_in_chat_table VALUES (1002,2,?,?,?,?,?,?,?)',
            (md5name(2) + ".png", "Pic/2024-01/Ori/" + md5name(2) + ".png", "",
             len(img2) // 1024, 1705276800, "987654321", 2))             # old group image, KB size
fic.execute('INSERT INTO files_in_chat_table VALUES (1003,3,?,?,?,?,?,?,?)',
            (md5name(3) + ".mp4", "Video/2025-08/Ori/" + md5name(3) + ".mp4", "",
             len(vid1), 1754611200, "111222333", 1))                     # c2c video
fic.execute('INSERT INTO files_in_chat_table VALUES (1004,4,"gone.mp4","Video/2025-08/Ori/deadbeef.mp4","",'
            '123456,1754611300,"111222333",1)')                          # missing file
fic.commit(); fic.close()

# --- rich_media.db (file_table) -------------------------------------------
rm = sqlite3.connect(os.path.join(nt_db, "rich_media.plain.db"))
rm.execute("""CREATE TABLE file_table (
  "40001" INTEGER, "45001" INTEGER, "45402" TEXT, "45403" TEXT, "45405" INTEGER,
  "45503" TEXT, "40021" TEXT)""")
rm.execute('INSERT INTO file_table VALUES (2001,7,"report.pdf","nt_data/File/2025-07/report.pdf",?,?,?)',
           (455016, "{UUID-1}", "987654321"))                            # nt_data/ prefixed + mismatch -> strong
rm.commit(); rm.close()

# --- group_info.db (name extraction fodder) --------------------------------
gi = sqlite3.connect(os.path.join(nt_db, "group_info.plain.db"))
gi.execute("""CREATE TABLE group_detail_info_ver1 (
  "40002" INTEGER, "groupInfo" BLOB)""")

def pb_str(field, s):
    b = s.encode("utf-8")
    # field<=15, wiretype 2 -> single tag byte; len uses varint (assumes <128)
    return bytes([field << 3 | 2, len(b)]) + b

# groupInfo blob: field1=groupCode varint, field4=name string
def group_blob(code, name):
    inner = bytes([0x08]) + struct.pack("B", code % 128) + pb_str(4, name)
    # outer field 1, wiretype 2
    return bytes([0x0A, len(inner)]) + inner

gi.execute('INSERT INTO group_detail_info_ver1 VALUES (?,?)', (987654321, group_blob(987654321, "NTQlean 测试群")))
gi.execute('INSERT INTO group_detail_info_ver1 VALUES (?,?)', (111222333, group_blob(111222333, "另一个测试群")))
gi.commit(); gi.close()

print("fixtures at:", BASE)
print("media files:", len(files))
print("orphan bytes:", orph_size)
