"""纯标准库 PNG 裁剪放大工具（不依赖 Pillow）。

用法：
    python pngcrop.py <输入.png> <输出.png> <x> <y> <w> <h> [放大倍数]
"""
import struct
import sys
import zlib


def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG")
    pos = 8
    idat = b""
    w = h = bitdepth = colortype = interlace = None
    palette = trns = None
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b"IHDR":
            w, h, bitdepth, colortype, _comp, _filt, interlace = struct.unpack(">IIBBBBB", chunk)
        elif ctype == b"IDAT":
            idat += chunk
        elif ctype == b"PLTE":
            palette = chunk
        elif ctype == b"tRNS":
            trns = chunk
        elif ctype == b"IEND":
            break
    if bitdepth != 8:
        raise ValueError(f"只支持 8 位色深，当前 {bitdepth}")
    if interlace:
        raise ValueError("不支持隔行扫描")
    raw = zlib.decompress(idat)
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    stride = w * channels
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(h):
        f = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if f == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, colortype, channels, palette, rows


def to_rgb(colortype, channels, palette, row):
    if colortype == 2:
        return bytes(row)
    if colortype == 6:
        out = bytearray()
        for i in range(0, len(row), 4):
            out += row[i:i + 3]
        return bytes(out)
    if colortype == 3:
        out = bytearray()
        for idx in row:
            out += palette[idx * 3:idx * 3 + 3]
        return bytes(out)
    if colortype in (0, 4):
        step = channels
        out = bytearray()
        for i in range(0, len(row), step):
            v = row[i]
            out += bytes((v, v, v))
        return bytes(out)
    raise ValueError(f"不支持的 colortype {colortype}")


def write_png(path, w, h, rows_rgb):
    raw = bytearray()
    for r in rows_rgb:
        raw.append(0)
        raw += r
    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    out = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 6)) + chunk(b"IEND", b"")
    open(path, "wb").write(out)


def main():
    src, dst = sys.argv[1], sys.argv[2]
    x, y, cw, ch = (int(v) for v in sys.argv[3:7])
    scale = int(sys.argv[7]) if len(sys.argv) > 7 else 4

    w, h, colortype, channels, palette, rows = read_png(src)
    x = max(0, x); y = max(0, y)
    cw = min(cw, w - x); ch = min(ch, h - y)

    out_rows = []
    for ry in range(y, y + ch):
        rgb = to_rgb(colortype, channels, palette, rows[ry])
        seg = rgb[x * 3:(x + cw) * 3]
        big = bytearray()
        for i in range(0, len(seg), 3):
            big += seg[i:i + 3] * scale
        for _ in range(scale):
            out_rows.append(bytes(big))
    write_png(dst, cw * scale, ch * scale, out_rows)
    print(f"saved {dst} {cw * scale}x{ch * scale}")


if __name__ == "__main__":
    main()
