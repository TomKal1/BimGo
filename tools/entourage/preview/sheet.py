"""Library sheet: each built-in in its own colours and as the in-game silhouette (alpha test 0.5, luminance shading)."""
import sys
import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, sys.path[0])
import designs  # noqa: E402

CELL = 256
COLOURS = {"tree": 0x7C9A6B, "shrub": 0x93AC7E, "person": 0xE8E4DC, "car": 0xC9CED3, "other": 0xB9B4AA}


def silhouette(rgba, tint):
    a = rgba[..., 3] / 255.0
    rgb = rgba[..., :3] / 255.0
    lum = rgb @ np.array([0.299, 0.587, 0.114])
    mean = (lum * a).sum() / max(a.sum(), 1e-6)
    inv = 1.0 / max(mean, 0.05)
    shade = np.clip(1.0 + (lum * inv - 1.0) * 0.7, 0.45, 1.5)
    t = np.array([(tint >> 16) & 255, (tint >> 8) & 255, tint & 255]) / 255.0
    col = np.clip(t[None, None, :] * shade[..., None] * 0.85, 0, 1)
    out = np.zeros_like(rgba)
    out[..., :3] = (col * 255).astype(np.uint8)
    out[..., 3] = np.where(a >= 0.5, 255, 0)
    return out


def cell(img, bg):
    base = Image.new("RGBA", img.size, bg)
    base.alpha_composite(img)
    return base.convert("RGB").resize((CELL, CELL), Image.LANCZOS)


def main(out, keys=None):
    items = [b for b in designs.BUILT_INS if keys is None or b[0] in keys]
    cols = min(len(items), 7)
    rows = (len(items) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * CELL, rows * (CELL * 2 + 22)), (40, 42, 46))
    d = ImageDraw.Draw(sheet)
    for i, (key, name, cat, h) in enumerate(items):
        rgba = designs.draw(key)
        x, y = (i % cols) * CELL, (i // cols) * (CELL * 2 + 22)
        sheet.paste(cell(Image.fromarray(rgba, "RGBA"), (236, 233, 226, 255)), (x, y))
        sheet.paste(cell(Image.fromarray(silhouette(rgba, COLOURS[cat]), "RGBA"), (120, 140, 160, 255)), (x, y + CELL))
        d.text((x + 6, y + CELL * 2 + 4), f"{name} ({h} m)", fill=(230, 230, 230))
    sheet.save(out)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2].split(",") if len(sys.argv) > 2 else None)
