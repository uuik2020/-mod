# -*- coding: utf-8 -*-
"""
AI 雇工贴图生成器：12 帧 16x24 像素小农夫（4 方向 x 待机/走1/走2）。
图集布局（横向排列，每帧 16px 宽）：
  0-2  朝下(待机/走1/走2)
  3-5  朝左
  6-8  朝右（朝左镜像）
  9-11 朝上
输出 PNG（RGBA, filter 0, zlib level 6）——该格式 MonoGame 可正常加载。
"""
import zlib
import struct
import os

W, H = 16, 24

# ---------------- 调色板 ----------------
OUT = (32, 32, 44, 255)          # 描边
SKIN = (242, 203, 164, 255)
SKIN_D = (214, 164, 116, 255)
HAIR = (110, 68, 40, 255)
HAIR_L = (146, 96, 58, 255)
HAT = (222, 190, 84, 255)        # 草帽
HAT_D = (170, 142, 58, 255)      # 帽带/暗部
HAT_L = (238, 216, 134, 255)     # 高光
SHIRT = (78, 148, 220, 255)      # 蓝衬衫
SHIRT_D = (54, 112, 176, 255)    # 衣袖暗部
PANTS = (96, 78, 60, 255)
PANTS_D = (70, 56, 42, 255)
BOOT = (60, 46, 34, 255)
WHITE = (242, 242, 242, 255)
EYE = (32, 32, 44, 255)
MOUTH = (182, 100, 84, 255)
BLUSH = (235, 164, 140, 255)
SCARF = (214, 84, 66, 255)       # 红领巾
SCARF_D = (172, 58, 46, 255)


def new_frame():
    return [[None for _ in range(W)] for _ in range(H)]


def px(f, x, y, c):
    if 0 <= x < W and 0 <= y < H:
        f[y][x] = c


def rect(f, x0, y0, x1, y1, c):
    for y in range(max(0, y0), min(H - 1, y1) + 1):
        for x in range(max(0, x0), min(W - 1, x1) + 1):
            f[y][x] = c


def mirror(f):
    return [row[::-1] for row in f]


def auto_outline(f):
    """透明像素只要四邻域有实心像素就填描边色，让角色从背景里跳出来。"""
    g = [row[:] for row in f]
    for y in range(H):
        for x in range(W):
            if f[y][x] is None:
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < W and 0 <= ny < H and f[ny][nx] is not None:
                        g[y][x] = OUT
                        break
    return g


# ---------------- 部件 ----------------

def hat(f):
    """草帽（正/后视图：对称）。"""
    rect(f, 6, 0, 9, 0, HAT_L)      # 帽顶高光
    rect(f, 5, 1, 10, 2, HAT)
    rect(f, 4, 3, 11, 3, HAT_D)     # 帽带
    rect(f, 4, 4, 11, 4, HAT)
    rect(f, 2, 5, 13, 5, HAT_L)     # 帽檐


def hat_side(f):
    """草帽侧视图：帽檐朝左伸。"""
    rect(f, 6, 0, 9, 0, HAT_L)
    rect(f, 5, 1, 10, 2, HAT)
    rect(f, 4, 3, 11, 3, HAT_D)
    rect(f, 4, 4, 11, 4, HAT)
    rect(f, 1, 5, 11, 5, HAT_L)     # 帽檐向左
    px(f, 1, 6, HAT_D)              # 檐角下翘


def scarf(f):
    rect(f, 6, 14, 9, 14, SCARF)
    px(f, 5, 14, SCARF_D)
    px(f, 10, 14, SCARF_D)


def face_front(f):
    rect(f, 5, 6, 10, 7, SKIN)
    px(f, 5, 6, HAIR)               # 两侧鬓角
    px(f, 10, 6, HAIR)
    rect(f, 5, 8, 10, 8, SKIN)
    px(f, 6, 8, EYE)                # 眼睛
    px(f, 9, 8, EYE)
    rect(f, 5, 9, 10, 9, SKIN)
    px(f, 5, 9, BLUSH)              # 腮红
    px(f, 10, 9, BLUSH)
    rect(f, 5, 10, 10, 10, SKIN)
    rect(f, 5, 11, 10, 11, SKIN)
    rect(f, 7, 11, 8, 11, MOUTH)    # 嘴
    rect(f, 6, 12, 9, 12, SKIN)     # 下巴
    rect(f, 6, 13, 9, 13, SKIN_D)   # 脖子阴影


def face_back(f):
    rect(f, 5, 6, 10, 12, HAIR)     # 后脑勺全是头发
    rect(f, 6, 6, 9, 7, HAIR_L)     # 顶部亮发
    rect(f, 6, 13, 9, 13, SKIN_D)


def face_side(f):
    """左侧轮廓：鼻子、单眼、嘴都在左缘。"""
    rect(f, 5, 6, 10, 12, SKIN)
    rect(f, 9, 6, 10, 12, HAIR)     # 后脑头发
    px(f, 4, 8, SKIN)               # 鼻子
    px(f, 6, 8, EYE)                # 眼睛
    px(f, 5, 10, SKIN)
    px(f, 5, 11, MOUTH)             # 嘴
    rect(f, 5, 12, 6, 12, SKIN)     # 下巴
    rect(f, 6, 13, 9, 13, SKIN_D)


def body_front(f, arm_raise=False):
    rect(f, 4, 14, 11, 14, SHIRT)   # 肩
    rect(f, 7, 14, 8, 14, WHITE)    # 衣领
    rect(f, 4, 15, 11, 16, SHIRT)
    rect(f, 4, 17, 11, 18, SHIRT)
    # 手臂（走路时右臂抬起）
    rect(f, 3, 14, 3, 17, SHIRT_D)
    px(f, 3, 18, SKIN)
    if arm_raise:
        rect(f, 12, 14, 12, 15, SHIRT_D)
        px(f, 12, 16, SKIN)
    else:
        rect(f, 12, 14, 12, 17, SHIRT_D)
        px(f, 12, 18, SKIN)
    rect(f, 4, 19, 11, 19, PANTS_D)  # 腰带
    px(f, 7, 19, HAT_D)              # 腰带扣
    px(f, 8, 19, HAT_D)


def body_back(f):
    rect(f, 4, 14, 11, 14, SHIRT_D)  # 背面整体暗一档
    rect(f, 4, 15, 11, 18, SHIRT_D)
    rect(f, 3, 14, 3, 17, SHIRT_D)   # 手臂
    px(f, 3, 18, SKIN)
    rect(f, 12, 14, 12, 17, SHIRT_D)
    px(f, 12, 18, SKIN)
    rect(f, 4, 19, 11, 19, PANTS_D)
    px(f, 7, 19, HAT_D)
    px(f, 8, 19, HAT_D)


def body_side(f, arm_forward=True):
    """侧视图身体：只露一只手臂（画面左侧）。"""
    rect(f, 5, 14, 10, 14, SHIRT)
    rect(f, 5, 15, 10, 16, SHIRT)
    rect(f, 5, 17, 10, 18, SHIRT)
    rect(f, 4, 14, 4, 16, SHIRT_D)   # 前臂
    if arm_forward:
        px(f, 3, 17, SKIN)           # 手向前伸
    else:
        px(f, 4, 18, SKIN)           # 手向后收
    px(f, 11, 15, SHIRT_D)           # 后手臂一瞥
    rect(f, 5, 19, 10, 19, PANTS_D)


def legs_idle(f):
    rect(f, 5, 20, 6, 21, PANTS)
    rect(f, 9, 20, 10, 21, PANTS)
    rect(f, 4, 22, 6, 23, BOOT)
    rect(f, 8, 22, 10, 23, BOOT)


def legs_walk_a(f):
    """走路相位1：左腿前伸、右腿在后。"""
    rect(f, 4, 20, 6, 21, PANTS)
    rect(f, 9, 20, 10, 21, PANTS)
    rect(f, 3, 22, 5, 23, BOOT)
    rect(f, 9, 22, 11, 23, BOOT)


def legs_side_idle(f):
    rect(f, 5, 20, 6, 21, PANTS)
    rect(f, 8, 20, 9, 21, PANTS)
    rect(f, 4, 22, 6, 23, BOOT)      # 前靴朝左
    rect(f, 8, 22, 10, 23, BOOT)


def legs_side_walk_a(f):
    rect(f, 4, 20, 6, 21, PANTS)
    rect(f, 9, 20, 10, 21, PANTS)
    rect(f, 3, 22, 5, 23, BOOT)      # 前脚前伸
    rect(f, 9, 22, 11, 23, BOOT)     # 后脚蹬地


def legs_side_walk_b(f):
    rect(f, 6, 20, 7, 21, PANTS)     # 前腿收回落地
    rect(f, 9, 20, 10, 21, PANTS)
    rect(f, 6, 22, 7, 23, BOOT)
    rect(f, 8, 22, 10, 23, BOOT)     # 后脚收拢


# ---------------- 组装 12 帧 ----------------

frames = []

# 朝下：待机 / 走1 / 走2（走2 = 走1 水平镜像）
f = new_frame(); hat(f); face_front(f); body_front(f); scarf(f); legs_idle(f)
frames.append(f)
f = new_frame(); hat(f); face_front(f); body_front(f, arm_raise=True); scarf(f); legs_walk_a(f)
frames.append(f)
frames.append(mirror(frames[-1]))

# 朝左：待机 / 走1 / 走2
f = new_frame(); hat_side(f); face_side(f); body_side(f, arm_forward=False); scarf(f); legs_side_idle(f)
frames.append(f)
f = new_frame(); hat_side(f); face_side(f); body_side(f, arm_forward=True); scarf(f); legs_side_walk_a(f)
frames.append(f)
f = new_frame(); hat_side(f); face_side(f); body_side(f, arm_forward=False); scarf(f); legs_side_walk_b(f)
frames.append(f)

# 朝右：朝左三帧依次镜像
for i in range(3):
    frames.append(mirror(frames[3 + i]))

# 朝上：待机 / 走1 / 走2
f = new_frame(); hat(f); face_back(f); body_back(f); legs_idle(f)
frames.append(f)
f = new_frame(); hat(f); face_back(f); body_back(f); legs_walk_a(f)
frames.append(f)
frames.append(mirror(frames[-1]))

frames = [auto_outline(fr) for fr in frames]
assert len(frames) == 12

# ---------------- 写 PNG ----------------

def write_png(path, frames):
    n = len(frames)
    sheet_w = W * n
    raw = bytearray()
    for y in range(H):
        raw.append(0)  # filter type 0
        for i in range(n):
            for x in range(W):
                c = frames[i][y][x]
                raw += bytes(c) if c else b"\x00\x00\x00\x00"

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", sheet_w, H, 8, 6, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)
    print(f"written {path}  ({len(png)} bytes, {sheet_w}x{H}, {n} frames)")


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "AIHireling", "assets", "ai_sprite.png")
    write_png(os.path.normpath(out), frames)
