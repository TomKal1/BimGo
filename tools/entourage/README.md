# Entourage tools (Round X-B3)

Python helpers for BimGo's billboards. They are not part of the build; nothing here ships with BimGo.

Needs Python 3 with `numpy`, `pillow` and (for tracing) `scikit-image`.

## trace.py: a drawing → `EntourageShapes`

```
python trace.py man.png --key person-man --name "Man standing" --category person --height 1.78 --preview man_check.png
```

- **Input:** a filled silhouette, dark on a light background (ink on paper photographed or scanned flat, or a tablet
  PNG), or a transparent PNG. One figure per file, standing on its lowest dark pixel.
- **Steps:** threshold (`--threshold`, 0.5) → every contour, holes included (the gap under an arm stays open: rings are
  filled even-odd) → specks dropped → Douglas–Peucker (`--tolerance`, 0.0015 of the figure's height) → normalised
  (ground at v = 0, height 1 unit, u = 0 the middle).
- **Output:** a C# `new EntourageShape(…)` entry on stdout: paste it into `EntourageShapes.ALL`
  (`src/BimGo.Core/Scene/EntourageShapes.cs`). The same key as a built-in (`person-man`) replaces that picture; a new
  key adds a type. BimGo smooths the outline with `--smooth` Chaikin passes (2) when it draws it.
- **`--preview`:** the source beside the shape as BimGo will draw it. Check every traced figure before shipping it.
- Only drawings that may be redistributed under MIT-0 go into `EntourageShapes` (Gavin's own, credited with
  `--credit`); never a third-party pack.

## preview/: the built-ins as Python

`preview/canvas.py` mirrors `EntourageArt.Canvas` (circles, ellipses, capsules, `rings` / `poly`, `shape`, `limb`,
Chaikin), `people.py` and `trees.py` mirror the X-B3 designs, `sheet.py` draws a library sheet (own colours, and the
silhouette look as the shader shades it):

```
cd preview
python sheet.py library.png                       # every design ported so far
python sheet.py people.png person-man,person-child
```

Keep a change to the C# designs mirrored here (same numbers, same random-number order) so previews match the app.
The hedge, grass and cars are not ported (unchanged since X-B1).
