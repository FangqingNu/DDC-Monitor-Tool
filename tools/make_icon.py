"""生成 DdcMonitorTool 的应用图标。

设计：圆角方形渐变底（系统蓝 -> 靛紫），中间一台白色显示器轮廓，
屏内一条滑块轨道 + 圆点，呼应"用软件调显示器"这件事。

每个尺寸单独绘制（8x 超采样），不是把 256px 缩到底，所以 16px 任务栏图标仍然清晰。
用法：python tools/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT_DIR = Path(__file__).resolve().parent.parent / "assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

TOP_LEFT = (10, 132, 255)
BOTTOM_RIGHT = (94, 92, 230)

SS = 8  # 超采样倍数


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def gradient(size):
    """对角线渐变，按像素生成，避免 PIL 没有原生渐变。"""
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1)) if size > 1 else 0
            px[x, y] = lerp(TOP_LEFT, BOTTOM_RIGHT, t)
    return img


def draw_icon(size):
    s = size * SS
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 圆角方形底
    radius = round(s * 0.225)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    canvas.paste(gradient(s).convert("RGBA"), (0, 0), mask)

    d = ImageDraw.Draw(canvas)
    white = (255, 255, 255, 255)

    # 显示器外框
    left, top = round(s * 0.20), round(s * 0.24)
    right, bottom = round(s * 0.80), round(s * 0.63)
    stroke = max(round(s * 0.058), 1)
    box_r = round(s * 0.055)
    d.rounded_rectangle(
        [left, top, right, bottom], radius=box_r, outline=white, width=stroke
    )

    # 支架 + 底座
    stem_w = round(s * 0.055)
    stem_top = bottom - stroke // 2
    stem_bottom = round(s * 0.73)
    d.rectangle(
        [s // 2 - stem_w // 2, stem_top, s // 2 + stem_w // 2, stem_bottom], fill=white
    )
    base_w = round(s * 0.20)
    base_h = max(round(s * 0.042), 1)
    d.rounded_rectangle(
        [s // 2 - base_w // 2, stem_bottom, s // 2 + base_w // 2, stem_bottom + base_h],
        radius=base_h // 2,
        fill=white,
    )

    # 屏内滑块（小尺寸时省略，避免糊成一团）
    if size >= 32:
        track_y = (top + bottom) // 2
        track_h = max(round(s * 0.030), 1)
        track_l = round(s * 0.30)
        track_r = round(s * 0.70)
        d.rounded_rectangle(
            [track_l, track_y - track_h // 2, track_r, track_y + track_h // 2],
            radius=track_h // 2,
            fill=(255, 255, 255, 150),
        )
        knob_r = round(s * 0.052)
        knob_x = round(s * 0.61)
        d.ellipse(
            [knob_x - knob_r, track_y - knob_r, knob_x + knob_r, track_y + knob_r],
            fill=white,
        )

    return canvas.resize((size, size), Image.LANCZOS)


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    frames = [draw_icon(s) for s in SIZES]
    ico = OUT_DIR / "app.ico"
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in SIZES])
    frames[-1].save(OUT_DIR / "app_256.png")
    print(f"wrote {ico} ({', '.join(str(s) for s in SIZES)})")


if __name__ == "__main__":
    main()
