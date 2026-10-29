# -*- coding: utf-8 -*-
"""
make_ico.py —— 把 tools/icon-src 下的一组 PNG 打包成 assets/whale.ico

为什么不用现成库：
    .NET Framework 自带的 csc /win32icon 只认"传统 DIB 条目"的 ico
    （PNG 压缩帧在部分环节会出问题），所以这里手工组装：
    ICO = ICONDIR + ICONDIRENTRY[] + 每个尺寸的 BITMAPINFOHEADER
          + bottom-up 的 BGRA 像素 + 1bpp AND 掩码。

用法（在 WhaleTheme 目录下）：
    python tools/make_ico.py                    # 默认读 tools/icon-src/eye_<size>.png
    python tools/make_ico.py --ffmpeg "C:\\path\\ffmpeg.exe"
    python tools/make_ico.py --src tools/icon-src --out assets/whale.ico

依赖：ffmpeg（只用来 PNG -> BMP，转出来是 32bpp BGRA）
      没有就把 ffmpeg.exe 放进 PATH，或用 --ffmpeg 指过去。
"""

import os
import struct
import subprocess
import sys
import tempfile

SIZES = [16, 24, 32, 48, 64, 128, 256]

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

DEFAULT_SRC = os.path.join(HERE, "icon-src")
DEFAULT_OUT = os.path.join(ROOT, "assets", "whale.ico")

FFMPEG_HINTS = [
    r"C:\Program Files\FormatFactory\ffmpeg.exe",
    r"C:\Program Files (x86)\FormatFactory\ffmpeg.exe",
    r"C:\ffmpeg\bin\ffmpeg.exe",
]


def find_ffmpeg(explicit):
    if explicit:
        if os.path.isfile(explicit):
            return explicit
        sys.exit("[ERROR] --ffmpeg 指向的文件不存在: %s" % explicit)

    for name in ("ffmpeg.exe", "ffmpeg"):
        for p in os.environ.get("PATH", "").split(os.pathsep):
            cand = os.path.join(p, name)
            if os.path.isfile(cand):
                return cand

    for cand in FFMPEG_HINTS:
        if os.path.isfile(cand):
            return cand

    sys.exit(
        "[ERROR] 找不到 ffmpeg。\n"
        "        装一个并把 ffmpeg.exe 放进 PATH，或者：\n"
        '        python tools/make_ico.py --ffmpeg "D:\\tools\\ffmpeg.exe"'
    )


def png_to_bmp(ffmpeg, png, bmp, size):
    # bgr24：源图是不含 alpha 的 RGB，24bpp + 全 0 AND 掩码是 Explorer 最认的老式
    # DIB 形态，体积也最小（32bpp 也支持，但没必要）
    cmd = [
        ffmpeg, "-y", "-loglevel", "error",
        "-i", png,
        "-vf", "scale=%d:%d:flags=lanczos" % (size, size),
        "-pix_fmt", "bgr24",
        "-frames:v", "1",
        bmp,
    ]
    subprocess.check_call(cmd)


def bmp_to_entry(raw, size):
    """BMP 文件 -> ico 条目数据（DIB + 像素 + AND 掩码）"""
    if raw[:2] != b"BM":
        raise ValueError("不是 BMP: %d" % size)

    off_bits = struct.unpack_from("<I", raw, 10)[0]
    w = struct.unpack_from("<i", raw, 18)[0]
    h = struct.unpack_from("<i", raw, 22)[0]
    bpp = struct.unpack_from("<H", raw, 28)[0]
    assert w == size and abs(h) == size, "尺寸不符 %d: %dx%d" % (size, w, h)
    assert bpp in (24, 32), "位深不符 %d: %d" % (size, bpp)

    # BMP 与 ico 里的 DIB 都是 bottom-up，直接截取
    dib = bytearray(raw[14:off_bits])
    pixels = raw[off_bits:]

    row = ((size * bpp + 31) // 32) * 4
    need = row * size
    if len(pixels) < need:
        pixels += b"\x00" * (need - len(pixels))
    pixels = pixels[:need]

    # ico 里的 biHeight 必须写成 实际高度 x2（含 AND 掩码）
    struct.pack_into("<i", dib, 8, size * 2)
    struct.pack_into("<I", dib, 16, 0)          # BI_RGB，关掉压缩

    # AND 掩码全 0 = 完全不透明（透明由 32bpp 的 alpha 通道负责）
    mask_row = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_row * size)

    return bytes(dib) + pixels + mask


def main():
    src = DEFAULT_SRC
    out = DEFAULT_OUT
    ffmpeg_arg = None

    argv = sys.argv[1:]
    i = 0
    while i < len(argv):
        if argv[i] == "--src" and i + 1 < len(argv):
            src = argv[i + 1]; i += 2
        elif argv[i] == "--out" and i + 1 < len(argv):
            out = argv[i + 1]; i += 2
        elif argv[i] == "--ffmpeg" and i + 1 < len(argv):
            ffmpeg_arg = argv[i + 1]; i += 2
        else:
            sys.exit("[ERROR] 不认识的参数: %s" % argv[i])

    ffmpeg = find_ffmpeg(ffmpeg_arg)
    print("ffmpeg : %s" % ffmpeg)
    print("源目录 : %s" % src)
    print("输出   : %s" % out)

    missing = [s for s in SIZES
               if not os.path.isfile(os.path.join(src, "eye_%d.png" % s))]
    if missing:
        sys.exit("[ERROR] 缺少源图: %s" % ", ".join("eye_%d.png" % s for s in missing))

    tmpdir = tempfile.mkdtemp(prefix="ico_")
    entries = []
    for s in SIZES:
        png = os.path.join(src, "eye_%d.png" % s)
        bmp = os.path.join(tmpdir, "i_%d.bmp" % s)
        png_to_bmp(ffmpeg, png, bmp, s)
        blob = bmp_to_entry(open(bmp, "rb").read(), s)
        entries.append((s, blob))
        print("  %3dx%-3d %7d bytes" % (s, s, len(blob)))

    n = len(entries)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    dirs = bytearray()
    data = bytearray()
    for s, blob in entries:
        dim = 0 if s >= 256 else s          # 256 在 ICONDIRENTRY 里写 0
        dirs += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob

    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "wb") as f:
        f.write(header + bytes(dirs) + bytes(data))
    print("OK: %s (%d 字节, %d 帧)" % (out, len(header + bytes(dirs) + bytes(data)), n))


if __name__ == "__main__":
    main()
