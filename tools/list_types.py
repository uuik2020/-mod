# -*- coding: utf-8 -*-
"""最小 .NET 元数据解析器 v2：列出所有命名空间下含关键字的类型（只读静态分析）。"""
import struct

PATH = r"E:\SteamLibrary\steamapps\common\Stardew Valley\Stardew Valley.dll"
KEYWORDS = ("weed", "grass")

data = open(PATH, "rb").read()
pe_off = struct.unpack_from("<I", data, 0x3C)[0]
coff = pe_off + 4
num_sections = struct.unpack_from("<H", data, coff + 2)[0]
opt_size = struct.unpack_from("<H", data, coff + 16)[0]
opt = coff + 20
magic = struct.unpack_from("<H", data, opt)[0]
data_dir_off = opt + (112 if magic == 0x20B else 96)
cli_rva, _ = struct.unpack_from("<II", data, data_dir_off + 14 * 8)

sections = []
for i in range(num_sections):
    o = coff + 20 + opt_size + i * 40
    vsize, vaddr, rsize, roff = struct.unpack_from("<IIII", data, o + 8)
    sections.append((vaddr, vsize, roff, rsize))

def rva2off(rva):
    for vaddr, vsize, roff, rsize in sections:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return roff + (rva - vaddr)
    return None

o = rva2off(cli_rva)
md_rva, _ = struct.unpack_from("<II", data, o + 8)
m = rva2off(md_rva)
assert data[m:m + 4] == b"BSJB"
ver_len = struct.unpack_from("<I", data, m + 12)[0]
p = m + 16 + ver_len + 2
nstreams = struct.unpack_from("<H", data, p)[0]
p += 2
streams = {}
for _ in range(nstreams):
    off, size = struct.unpack_from("<II", data, p)
    p += 8
    end = data.index(b"\0", p)
    name = data[p:end].decode("ascii", "replace")
    p = (end + 1 + 3) & ~3
    streams[name] = (m + off, size)

strings_off = streams["#Strings"][0]
strings_size = streams["#Strings"][1]
tables_off = streams["#~"][0]

def s(idx):
    if idx >= strings_size:
        return None
    e = data.index(b"\0", strings_off + idx)
    return data[strings_off + idx:e].decode("utf-8", "replace")

heap_sizes = data[tables_off + 6]
str_idx_size = 4 if (heap_sizes & 0x01) else 2
valid = struct.unpack_from("<Q", data, tables_off + 8)[0]
rows = [0] * 64
p_rows = tables_off + 24
for i in range(64):
    if valid >> i & 1:
        rows[i] = struct.unpack_from("<I", data, p_rows)[0]
        p_rows += 4

p = tables_off + 24 + 4 * bin(valid).count("1")

def coded_size(tag_bits, max_rows):
    return 2 if max_rows < (1 << (16 - tag_bits)) else 4

mod_row = 2 + str_idx_size + 48   # Generation(2) + Name(s) + 3×GUID(16)
refscope_max = rows[0x01] + rows[0x02] + rows[0x1B] + rows[0x23] + rows[0x26]
typeref_row = coded_size(2, refscope_max) + str_idx_size * 2
typedef_start = p + mod_row * rows[0x00] + typeref_row * rows[0x01]

extends_max = rows[0x02] + rows[0x01] + rows[0x1B]
field_max = rows[0x04]
method_max = rows[0x06]

n_typedef = rows[0x02]
print(f"TypeDef={n_typedef} TypeRef={rows[0x01]} Field={field_max} Method={method_max} str_idx={str_idx_size}")

for row_size in (18, 20):
    ext_sz = coded_size(2, extends_max)
    fld_sz = coded_size(1, field_max)
    mth_sz = coded_size(1, method_max)
    size = 4 + str_idx_size * 2 + ext_sz + fld_sz + mth_sz
    if size != row_size:
        continue
    ok = 0
    hits = []
    for i in range(n_typedef):
        base = typedef_start + i * row_size
        if base + row_size > len(data):
            break
        try:
            flags = struct.unpack_from("<I", data, base)[0]
            if str_idx_size == 4:
                ni = struct.unpack_from("<I", data, base + 4)[0]
                nsi = struct.unpack_from("<I", data, base + 8)[0]
            else:
                ni = struct.unpack_from("<H", data, base + 4)[0]
                nsi = struct.unpack_from("<H", data, base + 8)[0]
            name = s(ni)
            ns = s(nsi)
            if name is None or ns is None:
                break
            ok += 1
            low = (ns + "." + name).lower()
            if any(k in low for k in KEYWORDS):
                hits.append(f"{ns}.{name}")
        except Exception:
            break
    print(f"row_size={row_size}: parsed {ok}/{n_typedef}")
    for h in sorted(set(hits)):
        print("   ", h)
