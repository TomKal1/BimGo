"""
BimGo Round X-B3: turns a drawn silhouette into vector rings for BimGo.Core/Scene/EntourageShapes.cs.

    python trace.py drawing.png --key person-man --name "Man standing" --category person --height 1.78
        [--credit "Gavin (Aussie BIM Guru)"] [--tolerance 0.0015] [--smooth 2] [--threshold 0.5] [--preview out.png]

The drawing: a filled silhouette, dark on a light background (a photo or scan of ink on paper, or a tablet PNG); a
transparent PNG works too (its alpha is the shape). Steps: threshold → every contour (holes included, filled even-odd)
→ specks dropped → Douglas–Peucker simplification → normalised (the lowest dark pixel is the ground, the figure's
height is 1 unit, u = 0 its middle) → C# for EntourageShapes.ALL, printed. Chaikin smoothing happens when BimGo draws it
(--smooth passes). --preview draws it the way BimGo will (tools/entourage/preview/canvas.py) beside the source.

Needs numpy, pillow and scikit-image (pip install numpy pillow scikit-image).
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image
from skimage import measure


def load_mask(path, threshold):
    image = Image.open(path)
    if image.mode in ("RGBA", "LA") or (image.mode == "P" and "transparency" in image.info):
        alpha = np.asarray(image.convert("RGBA"))[..., 3] / 255.0
        if alpha.min() < 0.9:
            return alpha >= threshold
    grey = np.asarray(image.convert("L")) / 255.0
    return (1.0 - grey) >= threshold


def ring_area(ring):
    x, y = ring[:, 0], ring[:, 1]
    return 0.5 * abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def trace(mask, tolerance, min_area=0.0004):
    """The mask's outlines as rings in units (v up, ground 0, height 1, u = 0 the middle)."""
    rows = np.nonzero(mask.any(axis=1))[0]
    cols = np.nonzero(mask.any(axis=0))[0]
    if rows.size == 0:
        raise SystemExit("Nothing dark enough to trace (try --threshold)")
    top, ground = rows[0], rows[-1] + 1
    left, right = cols[0], cols[-1] + 1
    height = float(ground - top)
    middle = (left + right) / 2.0
    padded = np.pad(mask.astype(float), 1)
    rings = []
    for contour in measure.find_contours(padded, 0.5):
        # (row, col) → (u, v) in units
        u = (contour[:, 1] - 1 - middle) / height
        v = (ground - (contour[:, 0] - 1)) / height
        ring = np.stack([u, v], axis=1)
        if ring_area(ring) < min_area:
            continue  # a speck (dust on the scan, a stray dot)
        ring = measure.approximate_polygon(ring, tolerance)
        if len(ring) > 3 and np.allclose(ring[0], ring[-1]):
            ring = ring[:-1]
        if len(ring) >= 3:
            rings.append(ring)
    rings.sort(key=ring_area, reverse=True)
    width = (right - left) / height
    return rings, width


def to_csharp(rings, width, key, name, category, height, smooth, credit):
    def number(x):
        text = f"{x:.4f}".rstrip("0").rstrip(".")
        return "0" if text in ("-0", "") else text

    lines = [f'new EntourageShape("{key}", "{name}", EntourageCategories.{category.upper()}, {height}f, {number(width)}, {smooth}, new[]', "{"]
    for i, ring in enumerate(rings):
        flat = ", ".join(f"{number(u)}, {number(v)}" for u, v in ring)
        lines.append(f"    new double[] {{ {flat} }}" + ("," if i < len(rings) - 1 else ""))
    lines.append(f'}}, "{credit}"),')
    return "\n".join(lines)


def preview(source, rings, width, smooth, out):
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "preview"))
    from canvas import Canvas, chaikin  # noqa: E402
    c = Canvas(1.0, max(width, 0.05))
    c.rings([chaikin([x for p in ring for x in p], smooth) for ring in rings], (0.25, 0.25, 0.25))
    drawn = Image.fromarray(c.to_rgba(), "RGBA")
    src = Image.open(source).convert("RGBA")
    src.thumbnail((512, 512))
    sheet = Image.new("RGBA", (1024, 512), (255, 255, 255, 255))
    sheet.alpha_composite(src, ((512 - src.width) // 2, (512 - src.height) // 2))
    sheet.alpha_composite(drawn, (512, 0))
    sheet.convert("RGB").save(out)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("drawing")
    p.add_argument("--key", required=True)
    p.add_argument("--name", required=True)
    p.add_argument("--category", default="person", choices=["tree", "shrub", "person", "car", "other"])
    p.add_argument("--height", type=float, default=1.75)
    p.add_argument("--credit", default="Gavin (Aussie BIM Guru), MIT-0")
    p.add_argument("--tolerance", type=float, default=0.0015, help="simplification, in units of the figure's height")
    p.add_argument("--smooth", type=int, default=2, help="Chaikin passes when drawn (0-3)")
    p.add_argument("--threshold", type=float, default=0.5)
    p.add_argument("--preview")
    a = p.parse_args()
    mask = load_mask(a.drawing, a.threshold)
    rings, width = trace(mask, a.tolerance)
    points = sum(len(r) for r in rings)
    print(f"// {os.path.basename(a.drawing)}: {len(rings)} rings, {points} points", file=sys.stderr)
    print(to_csharp(rings, width, a.key, a.name, a.category, a.height, a.smooth, a.credit))
    if a.preview:
        preview(a.drawing, rings, width, a.smooth, a.preview)


if __name__ == "__main__":
    main()
