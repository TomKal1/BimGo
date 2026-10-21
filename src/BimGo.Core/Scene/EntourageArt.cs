// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Round X-B: entourage categories (what a billboard is: a tree, a shrub, a person, a car or anything else), their
    /// labels and default silhouette colours.
    /// </summary>
    public static class EntourageCategories
    {
        public const string TREE = "tree", SHRUB = "shrub", PERSON = "person", CAR = "car", OTHER = "other";

        /// <summary>All of them, in panel order.</summary>
        public static readonly string[] ALL = { TREE, SHRUB, PERSON, CAR, OTHER };

        /// <summary>Plural labels in panel order (index as <see cref="ALL"/>).</summary>
        public static readonly string[] LABELS = { "Trees", "Shrubs", "People", "Cars", "Other" };

        /// <summary>Default silhouette colours (0xRRGGBB, index as <see cref="ALL"/>): sage trees, lighter shrubs, off-white people, pale grey cars.</summary>
        public static readonly uint[] DEFAULT_COLOURS = { 0x7C9A6B, 0x93AC7E, 0xE8E4DC, 0xC9CED3, 0xB9B4AA };

        /// <summary>The index of a category (unknown → other).</summary>
        public static int IndexOf(string category)
        {
            int i = Array.IndexOf(ALL, Normalise(category));
            return i < 0 ? ALL.Length - 1 : i;
        }

        /// <summary>A known category, lower case (unknown or empty → other).</summary>
        public static string Normalise(string category)
        {
            string c = (category ?? string.Empty).Trim().ToLowerInvariant();
            return Array.IndexOf(ALL, c) >= 0 ? c : OTHER;
        }
    }

    /// <summary>Round X-B: a built-in billboard type: its key, name, category and the height a new one is planted at.</summary>
    /// <param name="Key">Stable id stored in files ("tree-round").</param>
    /// <param name="Name">Shown in the panel.</param>
    /// <param name="Category">See <see cref="EntourageCategories"/>.</param>
    /// <param name="Height">Default height (m).</param>
    public readonly record struct EntourageBuiltIn(string Key, string Name, string Category, float Height);

    /// <summary>
    /// Round X-B: an RGBA picture of a billboard (top row first, straight alpha) and where its content sits (the
    /// opaque part's box), with its mean luminance (the tinted look shades by luminance against it).
    /// </summary>
    public sealed class EntourageImage
    {
        /// <summary>Width and height (px).</summary>
        public int Width { get; }

        /// <summary>See <see cref="Width"/>.</summary>
        public int Height { get; }

        /// <summary>RGBA bytes, top row first.</summary>
        public byte[] Rgba { get; }

        /// <summary>The content's box (px): min inclusive, max exclusive. Empty pictures have MaxX ≤ MinX.</summary>
        public int MinX { get; private set; }

        /// <summary>See <see cref="MinX"/>.</summary>
        public int MinY { get; private set; }

        /// <summary>See <see cref="MinX"/>.</summary>
        public int MaxX { get; private set; }

        /// <summary>See <see cref="MinX"/>.</summary>
        public int MaxY { get; private set; }

        /// <summary>Mean luminance (0–1) of the opaque pixels.</summary>
        public float MeanLuminance { get; private set; } = 0.5f;

        /// <summary>The content's width / height (1 when empty).</summary>
        public float Aspect => MaxY > MinY && MaxX > MinX ? (MaxX - MinX) / (float)(MaxY - MinY) : 1f;

        /// <summary>True when nothing is opaque.</summary>
        public bool IsEmpty => MaxX <= MinX || MaxY <= MinY;

        /// <summary>Wraps RGBA pixels (top row first) and measures them.</summary>
        public EntourageImage(int width, int height, byte[] rgba)
        {
            Width = width;
            Height = height;
            Rgba = rgba ?? throw new ArgumentNullException(nameof(rgba));
            if (rgba.Length != width * height * 4) { throw new ArgumentException("RGBA size doesn't match the picture."); }
            Measure();
        }

        /// <summary>The alpha (0–255) at a pixel (0 outside).</summary>
        public byte AlphaAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : Rgba[(y * Width + x) * 4 + 3];

        /// <summary>Finds the content box (alpha ≥ 8) and the mean luminance of the opaque part.</summary>
        public void Measure()
        {
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            double lum = 0, weight = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    byte a = Rgba[i + 3];
                    if (a < 8) { continue; }
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                    double w = a / 255.0;
                    lum += w * (0.299 * Rgba[i] + 0.587 * Rgba[i + 1] + 0.114 * Rgba[i + 2]) / 255.0;
                    weight += w;
                }
            }
            if (maxX < 0)
            {
                MinX = MinY = MaxX = MaxY = 0;
                MeanLuminance = 0.5f;
                return;
            }
            MinX = minX;
            MinY = minY;
            MaxX = maxX + 1;
            MaxY = maxY + 1;
            MeanLuminance = (float)Math.Clamp(lum / Math.Max(weight, 1e-6), 0.05, 1.0);
        }

        /// <summary>
        /// Spreads the colour of opaque pixels into the transparent ones around them (alpha unchanged), so filtering and
        /// mipmaps never pull a dark fringe in at the edges.
        /// </summary>
        /// <param name="passes">How far it spreads (px).</param>
        public void BleedEdges(int passes = 8)
        {
            int n = Width * Height;
            var known = new bool[n];
            for (int i = 0; i < n; i++) { known[i] = Rgba[i * 4 + 3] >= 8; }
            var next = new bool[n];
            for (int pass = 0; pass < passes; pass++)
            {
                bool changed = false;
                Array.Copy(known, next, n);
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int i = y * Width + x;
                        if (known[i]) { continue; }
                        int r = 0, g = 0, b = 0, count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= Height) { continue; }
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= Width) { continue; }
                                int j = yy * Width + xx;
                                if (!known[j]) { continue; }
                                r += Rgba[j * 4];
                                g += Rgba[j * 4 + 1];
                                b += Rgba[j * 4 + 2];
                                count++;
                            }
                        }
                        if (count == 0) { continue; }
                        Rgba[i * 4] = (byte)(r / count);
                        Rgba[i * 4 + 1] = (byte)(g / count);
                        Rgba[i * 4 + 2] = (byte)(b / count);
                        next[i] = true;
                        changed = true;
                    }
                }
                Array.Copy(next, known, n);
                if (!changed) { break; }
            }
        }

        /// <summary>
        /// A copy fitted into a square of <paramref name="size"/> px: the content box scaled to fit (never up past 1:1
        /// unless needed), centred horizontally and standing on the bottom edge. Box-filtered when shrinking.
        /// </summary>
        public EntourageImage FitInto(int size)
        {
            var output = new byte[size * size * 4];
            if (IsEmpty) { return new EntourageImage(size, size, output); }
            int cw = MaxX - MinX, ch = MaxY - MinY;
            float scale = Math.Min((size - 2f) / cw, (size - 2f) / ch);
            int ow = Math.Max(1, (int)MathF.Round(cw * scale)), oh = Math.Max(1, (int)MathF.Round(ch * scale));
            int ox = (size - ow) / 2, oy = size - 1 - oh;
            for (int y = 0; y < oh; y++)
            {
                float sy0 = MinY + y / scale, sy1 = MinY + (y + 1) / scale;
                for (int x = 0; x < ow; x++)
                {
                    float sx0 = MinX + x / scale, sx1 = MinX + (x + 1) / scale;
                    double r = 0, g = 0, b = 0, a = 0, count = 0;
                    int ix0 = (int)sx0, ix1 = Math.Max(ix0 + 1, (int)MathF.Ceiling(sx1));
                    int iy0 = (int)sy0, iy1 = Math.Max(iy0 + 1, (int)MathF.Ceiling(sy1));
                    for (int sy = iy0; sy < iy1 && sy < Height; sy++)
                    {
                        for (int sx = ix0; sx < ix1 && sx < Width; sx++)
                        {
                            int i = (sy * Width + sx) * 4;
                            double w = Rgba[i + 3];
                            r += Rgba[i] * w;
                            g += Rgba[i + 1] * w;
                            b += Rgba[i + 2] * w;
                            a += w;
                            count++;
                        }
                    }
                    int o = ((oy + y) * size + ox + x) * 4;
                    if (a > 0)
                    {
                        output[o] = (byte)Math.Clamp(r / a + 0.5, 0, 255);
                        output[o + 1] = (byte)Math.Clamp(g / a + 0.5, 0, 255);
                        output[o + 2] = (byte)Math.Clamp(b / a + 0.5, 0, 255);
                    }
                    output[o + 3] = (byte)Math.Clamp(a / Math.Max(count, 1) + 0.5, 0, 255);
                }
            }
            return new EntourageImage(size, size, output);
        }

        /// <summary>
        /// For a picture with no transparency (a JPEG, a flattened PNG): makes the background see-through, taking it as
        /// the colour of the four corners (when they agree) within a tolerance, softened over a band.
        /// </summary>
        /// <returns>True when a background was removed.</returns>
        public bool KeyOutBackground(int tolerance = 40)
        {
            for (int i = 3; i < Rgba.Length; i += 4) { if (Rgba[i] < 250) { return false; } } // has transparency already
            int[] corners = { 0, Width - 1, (Height - 1) * Width, Height * Width - 1 };
            int r0 = Rgba[corners[0] * 4], g0 = Rgba[corners[0] * 4 + 1], b0 = Rgba[corners[0] * 4 + 2];
            foreach (int c in corners)
            {
                if (Math.Abs(Rgba[c * 4] - r0) + Math.Abs(Rgba[c * 4 + 1] - g0) + Math.Abs(Rgba[c * 4 + 2] - b0) > tolerance) { return false; }
            }
            for (int i = 0; i < Width * Height; i++)
            {
                int d = Math.Abs(Rgba[i * 4] - r0) + Math.Abs(Rgba[i * 4 + 1] - g0) + Math.Abs(Rgba[i * 4 + 2] - b0);
                if (d <= tolerance) { Rgba[i * 4 + 3] = 0; }
                else if (d <= tolerance * 2) { Rgba[i * 4 + 3] = (byte)(255 * (d - tolerance) / tolerance); }
            }
            Measure();
            return true;
        }
    }

    /// <summary>
    /// Round X-B: the built-in billboards, drawn in code (no third-party pictures): six trees (X-B3: the gum tree), three
    /// shrubs, four people and two cars, each a 512 px square standing on its bottom edge. X-B3 redrew the people (adult
    /// proportions, tapered limbs: <see cref="Canvas.Limb"/>) and the trees (branches carrying small-leaf clusters). Deterministic (seeded), so every machine
    /// draws the same pictures. Circles, ellipses and tapered capsules are antialiased analytically; polygons by 4 × 4
    /// supersampling.
    /// </summary>
    public static class EntourageArt
    {
        /// <summary>The pictures' size (px).</summary>
        public const int SIZE = 512;

        private const int MARGIN = 8;

        /// <summary>The built-in types, in panel order.</summary>
        public static readonly EntourageBuiltIn[] BUILT_INS = WithShapes(new EntourageBuiltIn[]
        {
            new("tree-round", "Round tree", EntourageCategories.TREE, 7f),
            new("tree-tall", "Columnar tree", EntourageCategories.TREE, 10f),
            new("tree-spreading", "Spreading tree", EntourageCategories.TREE, 9f),
            new("tree-conifer", "Conifer", EntourageCategories.TREE, 9f),
            new("tree-palm", "Palm", EntourageCategories.TREE, 8f),
            new("tree-gum", "Gum tree", EntourageCategories.TREE, 12f), // X-B3
            new("shrub-round", "Round shrub", EntourageCategories.SHRUB, 1.2f),
            new("shrub-hedge", "Hedge", EntourageCategories.SHRUB, 1.4f),
            new("shrub-grass", "Ornamental grass", EntourageCategories.SHRUB, 0.9f),
            new("person-man", "Man standing", EntourageCategories.PERSON, 1.78f),
            new("person-woman", "Woman walking", EntourageCategories.PERSON, 1.66f),
            new("person-child", "Child", EntourageCategories.PERSON, 1.15f),
            new("person-phone", "Person on the phone", EntourageCategories.PERSON, 1.72f),
            new("car-sedan", "Car", EntourageCategories.CAR, 1.45f),
            new("car-suv", "SUV / van", EntourageCategories.CAR, 1.85f)
        });

        /// <summary>
        /// Round X-B3: traced silhouettes (<see cref="EntourageShapes"/>) take over the built-in with their key (their
        /// name, category and height) or join the list after the procedural ones.
        /// </summary>
        private static EntourageBuiltIn[] WithShapes(EntourageBuiltIn[] drawn)
        {
            var list = new List<EntourageBuiltIn>(drawn.Length + EntourageShapes.ALL.Length);
            foreach (EntourageBuiltIn b in drawn)
            {
                EntourageShape shape = EntourageShapes.Find(b.Key);
                list.Add(shape == null ? b : new EntourageBuiltIn(b.Key, shape.Name, EntourageCategories.Normalise(shape.Category), shape.Height));
            }
            foreach (EntourageShape shape in EntourageShapes.ALL)
            {
                if (list.FindIndex(b => b.Key == shape.Key) < 0)
                {
                    list.Add(new EntourageBuiltIn(shape.Key, shape.Name, EntourageCategories.Normalise(shape.Category), shape.Height));
                }
            }
            return list.ToArray();
        }

        /// <summary>The built-in with a key, or null.</summary>
        public static EntourageBuiltIn? Find(string key)
        {
            foreach (EntourageBuiltIn b in BUILT_INS) { if (b.Key == key) { return b; } }
            return null;
        }

        /// <summary>Draws a built-in (an empty picture for an unknown key).</summary>
        public static EntourageImage Draw(string key)
        {
            EntourageShape shape = EntourageShapes.Find(key);
            Canvas c = shape != null ? DrawShape(shape) : key switch
            {
                "tree-round" => TreeRound(1),
                "tree-tall" => TreeTall(2),
                "tree-spreading" => TreeSpreading(3),
                "tree-conifer" => TreeConifer(4),
                "tree-palm" => TreePalm(5),
                "tree-gum" => TreeGum(9),
                "shrub-round" => ShrubRound(6),
                "shrub-hedge" => ShrubHedge(7),
                "shrub-grass" => ShrubGrass(8),
                "person-man" => People(0, 11),
                "person-woman" => People(1, 12),
                "person-child" => People(2, 13),
                "person-phone" => People(3, 14),
                "car-sedan" => Car(false),
                "car-suv" => Car(true),
                _ => new Canvas(1f)
            };
            var image = new EntourageImage(SIZE, SIZE, c.ToRgba());
            image.BleedEdges();
            return image;
        }

        /// <summary>Round X-B3: a traced silhouette, filled in its category's (darkened) colour.</summary>
        private static Canvas DrawShape(EntourageShape shape)
        {
            var c = new Canvas(1f, (float)Math.Max(shape.Width, 0.05));
            var rings = new double[shape.Rings.Length][];
            int smooth = Math.Clamp(shape.Smooth, 0, 3);
            for (int i = 0; i < rings.Length; i++) { rings[i] = Chaikin(shape.Rings[i], smooth); }
            c.Rings(rings, Hex(EntourageShapes.FillOf(shape.Category)));
            return c;
        }

        #region Drawing

        private readonly record struct Rgb(float R, float G, float B);

        private static Rgb Hex(uint h) => new(((h >> 16) & 255) / 255f, ((h >> 8) & 255) / 255f, (h & 255) / 255f);

        private static Rgb Mix(Rgb a, Rgb b, double t) => new((float)(a.R + (b.R - a.R) * t), (float)(a.G + (b.G - a.G) * t), (float)(a.B + (b.B - a.B) * t));

        private static readonly Rgb BLACK = new(0f, 0f, 0f);

        /// <summary>xorshift32, seeded (the same numbers on every machine).</summary>
        private sealed class Rng
        {
            private uint _x;

            public Rng(int seed)
            {
                _x = unchecked((uint)seed * 2654435761u + 12345u);
                if (_x == 0) { _x = 1; }
            }

            public double Next()
            {
                uint x = _x;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _x = x;
                return x / 4294967296.0;
            }

            public double Range(double a, double b) => a + (b - a) * Next();
        }

        /// <summary>
        /// Round X-B3: Chaikin corner cutting on [u0, v0, u1, v1, …] (closed: every corner rounded; open: the ends kept).
        /// Each pass doubles the points.
        /// </summary>
        private static double[] Chaikin(double[] points, int iterations, bool closed = true)
        {
            double[] pts = points;
            for (int pass = 0; pass < iterations; pass++)
            {
                int n = pts.Length / 2, count = closed ? n : n - 1;
                var output = new List<double>(count * 4 + 4);
                if (!closed)
                {
                    output.Add(pts[0]);
                    output.Add(pts[1]);
                }
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % n;
                    double ax = pts[i * 2], ay = pts[i * 2 + 1], bx = pts[j * 2], by = pts[j * 2 + 1];
                    output.Add(0.75 * ax + 0.25 * bx);
                    output.Add(0.75 * ay + 0.25 * by);
                    output.Add(0.25 * ax + 0.75 * bx);
                    output.Add(0.25 * ay + 0.75 * by);
                }
                if (!closed)
                {
                    output.Add(pts[^2]);
                    output.Add(pts[^1]);
                }
                pts = output.ToArray();
            }
            return pts;
        }

        /// <summary>
        /// Round X-B3: a tapered limb's outline from joints [u, v, half width, …]: down one side, a round-ish cap, back up
        /// the other, a cap.
        /// </summary>
        private static double[] LimbOutline(double[] chain)
        {
            int n = chain.Length / 3;
            var dirs = new double[n * 2];
            var left = new double[n * 2];
            var right = new double[n * 2];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(i - 1, 0), b = Math.Min(i + 1, n - 1);
                double du = chain[b * 3] - chain[a * 3], dv = chain[b * 3 + 1] - chain[a * 3 + 1];
                double l = Math.Sqrt(du * du + dv * dv);
                if (l < 1e-9) { l = 1.0; }
                du /= l;
                dv /= l;
                dirs[i * 2] = du;
                dirs[i * 2 + 1] = dv;
                double u = chain[i * 3], v = chain[i * 3 + 1], w = chain[i * 3 + 2];
                left[i * 2] = u - dv * w;
                left[i * 2 + 1] = v + du * w;
                right[i * 2] = u + dv * w;
                right[i * 2 + 1] = v - du * w;
            }
            var pts = new List<double>(n * 4 + 4);
            pts.Add(chain[0] - dirs[0] * chain[2] * 0.7);
            pts.Add(chain[1] - dirs[1] * chain[2] * 0.7);
            pts.AddRange(left);
            int e = n - 1;
            pts.Add(chain[e * 3] + dirs[e * 2] * chain[e * 3 + 2] * 0.7);
            pts.Add(chain[e * 3 + 1] + dirs[e * 2 + 1] * chain[e * 3 + 2] * 0.7);
            for (int i = n - 1; i >= 0; i--)
            {
                pts.Add(right[i * 2]);
                pts.Add(right[i * 2 + 1]);
            }
            return pts.ToArray();
        }

        /// <summary>
        /// A square canvas in "units": the ground at v = 0, up positive, u = 0 the centre line; the larger of the
        /// content's height and width fills the canvas less a margin.
        /// </summary>
        private sealed class Canvas
        {
            private readonly float[] _r = new float[SIZE * SIZE], _g = new float[SIZE * SIZE], _b = new float[SIZE * SIZE], _a = new float[SIZE * SIZE];
            private readonly float _scale;

            public Canvas(float heightUnits, float widthUnits = 0f) => _scale = (SIZE - 2f * MARGIN) / MathF.Max(heightUnits, widthUnits);

            private float Px(double u) => (float)(SIZE / 2.0 + u * _scale);

            private float Py(double v) => (float)(SIZE - MARGIN - v * _scale);

            private float L(double length) => (float)(length * _scale);

            private void Blend(int x, int y, float coverage, Rgb c)
            {
                if (coverage <= 0f) { return; }
                if (coverage > 1f) { coverage = 1f; }
                int i = y * SIZE + x;
                float k = 1f - coverage;
                _r[i] = _r[i] * k + c.R * coverage;
                _g[i] = _g[i] * k + c.G * coverage;
                _b[i] = _b[i] * k + c.B * coverage;
                _a[i] = _a[i] + coverage * (1f - _a[i]);
            }

            private static void Clip(float x0, float y0, float x1, float y1, out int ix0, out int iy0, out int ix1, out int iy1)
            {
                ix0 = Math.Max(0, (int)MathF.Floor(x0) - 1);
                iy0 = Math.Max(0, (int)MathF.Floor(y0) - 1);
                ix1 = Math.Min(SIZE, (int)MathF.Ceiling(x1) + 2);
                iy1 = Math.Min(SIZE, (int)MathF.Ceiling(y1) + 2);
            }

            public void Circle(double u, double v, double r, Rgb c) => Ellipse(u, v, r, r, c);

            public void Ellipse(double u, double v, double ru, double rv, Rgb c)
            {
                float cx = Px(u), cy = Py(v), a = MathF.Max(L(ru), 0.01f), b = MathF.Max(L(rv), 0.01f);
                Clip(cx - a, cy - b, cx + a, cy + b, out int x0, out int y0, out int x1, out int y1);
                float minor = MathF.Min(a, b);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        float dx = (x + 0.5f - cx) / a, dy = (y + 0.5f - cy) / b;
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        Blend(x, y, (1f - d) * minor + 0.5f, c); // signed distance in px (approximately), half a pixel ramp each way
                    }
                }
            }

            /// <summary>A segment with round ends whose radius goes from <paramref name="r0"/> to <paramref name="r1"/>.</summary>
            public void Capsule(double u0, double v0, double u1, double v1, double r0, double r1, Rgb c)
            {
                float ax = Px(u0), ay = Py(v0), bx = Px(u1), by = Py(v1), ra = L(r0), rb = L(r1);
                float dx = bx - ax, dy = by - ay, l2 = MathF.Max(dx * dx + dy * dy, 1e-9f);
                float m = MathF.Max(ra, rb);
                Clip(MathF.Min(ax, bx) - m, MathF.Min(ay, by) - m, MathF.Max(ax, bx) + m, MathF.Max(ay, by) + m, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0f, 1f);
                        float qx = ax + t * dx - px, qy = ay + t * dy - py;
                        float r = ra + (rb - ra) * t;
                        Blend(x, y, r - MathF.Sqrt(qx * qx + qy * qy) + 0.5f, c);
                    }
                }
            }

            /// <summary>A filled polygon (even-odd), 4 × 4 samples a pixel.</summary>
            public void Poly(double[] points, Rgb c) => Rings(new[] { points }, c);

            /// <summary>
            /// Round X-B3: filled rings [u, v, …] together, even-odd (so a ring inside another is a hole: traced
            /// silhouettes), 4 × 4 samples a pixel.
            /// </summary>
            public void Rings(double[][] rings, Rgb c)
            {
                int total = 0;
                foreach (double[] ring in rings) { total += ring.Length / 2; }
                if (total < 3) { return; }
                // Every edge as (x0, y0, x1, y1) in pixels
                var ex0 = new float[total];
                var ey0 = new float[total];
                var ex1 = new float[total];
                var ey1 = new float[total];
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                int e = 0;
                foreach (double[] ring in rings)
                {
                    int n = ring.Length / 2;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        ex0[e] = Px(ring[i * 2]);
                        ey0[e] = Py(ring[i * 2 + 1]);
                        ex1[e] = Px(ring[j * 2]);
                        ey1[e] = Py(ring[j * 2 + 1]);
                        minX = MathF.Min(minX, ex0[e]);
                        maxX = MathF.Max(maxX, ex0[e]);
                        minY = MathF.Min(minY, ey0[e]);
                        maxY = MathF.Max(maxY, ey0[e]);
                        e++;
                    }
                }
                Clip(minX, minY, maxX, maxY, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int inside = 0;
                        for (int sy = 0; sy < 4; sy++)
                        {
                            float qy = y + (sy + 0.5f) / 4f;
                            for (int sx = 0; sx < 4; sx++)
                            {
                                float qx = x + (sx + 0.5f) / 4f;
                                bool odd = false;
                                for (int k = 0; k < total; k++)
                                {
                                    float ay = ey0[k], by = ey1[k];
                                    if ((ay > qy) != (by > qy))
                                    {
                                        float dyy = by - ay;
                                        if (qx < (ex1[k] - ex0[k]) * (qy - ay) / (dyy == 0f ? 1e-9f : dyy) + ex0[k]) { odd = !odd; }
                                    }
                                }
                                if (odd) { inside++; }
                            }
                        }
                        Blend(x, y, inside / 16f, c);
                    }
                }
            }

            /// <summary>Round X-B3: a closed outline rounded by Chaikin corner cutting, then filled.</summary>
            public void Shape(double[] points, int iterations, Rgb c) => Poly(Chaikin(points, iterations), c);

            /// <summary>Round X-B3: a tapered limb through joints [u, v, half width, …], its outline smoothed.</summary>
            public void Limb(double[] chain, Rgb c) => Poly(Chaikin(LimbOutline(chain), 2), c);

            public byte[] ToRgba()
            {
                var rgba = new byte[SIZE * SIZE * 4];
                for (int i = 0; i < SIZE * SIZE; i++)
                {
                    // The layers build up premultiplied ("over"): back to straight alpha, so edges keep their colour
                    float a = _a[i], k = a > 1e-4f ? 1f / a : 0f;
                    rgba[i * 4] = (byte)Math.Clamp((int)(_r[i] * k * 255f + 0.5f), 0, 255);
                    rgba[i * 4 + 1] = (byte)Math.Clamp((int)(_g[i] * k * 255f + 0.5f), 0, 255);
                    rgba[i * 4 + 2] = (byte)Math.Clamp((int)(_b[i] * k * 255f + 0.5f), 0, 255);
                    rgba[i * 4 + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
                }
                return rgba;
            }
        }

        #endregion

        #region Designs

        // Round X-B3: trees are branch skeletons carrying clusters of small leaves (gaps show the branches, edges are
        // ragged); the Python preview port (ai/<build>/art) draws the same pictures
        private static readonly Rgb TRUNK = Hex(0x5B4636), BARK_LIGHT = Hex(0x7A6450);

        /// <summary>Foliage colours from shadow to sunlit.</summary>
        private readonly record struct Palette(Rgb Dark, Rgb Mid, Rgb Light);

        private static readonly Palette LEAF = new(Hex(0x3A5530), Hex(0x5C7C3C), Hex(0x8FAA5C));
        private static readonly Palette GUM = new(Hex(0x4F5A3A), Hex(0x7A8456), Hex(0xA7AE7C));
        private static readonly Palette NEEDLE = new(Hex(0x23392A), Hex(0x34523A), Hex(0x557650));

        /// <summary>A foliage cluster: its centre and radius (units).</summary>
        private readonly record struct Cluster(double U, double V, double R);

        /// <summary>The palette from dark (0) through mid (0.5) to light (1).</summary>
        private static Rgb Tone(Palette p, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t < 0.5 ? Mix(p.Dark, p.Mid, t * 2.0) : Mix(p.Mid, p.Light, (t - 0.5) * 2.0);
        }

        /// <summary>A tapered, slightly bent limb.</summary>
        private static void Branch(Canvas c, double u0, double v0, double u1, double v1, double w0, double w1, double bend, Rgb colour)
        {
            double du = u1 - u0, dv = v1 - v0;
            double mu = (u0 + u1) * 0.5 - dv * bend, mv = (v0 + v1) * 0.5 + du * bend;
            c.Limb(new[] { u0, v0, w0, mu, mv, (w0 + w1) * 0.5, u1, v1, w1 }, colour);
        }

        /// <summary>
        /// A branch and (depth &gt; 0) its 2–3 children, fanned by <paramref name="spread"/> (rad) and pulled towards
        /// vertical by <paramref name="up"/> (negative: towards horizontal); every tip (and every fork, smaller) is
        /// recorded for foliage.
        /// </summary>
        private static void Grow(Canvas c, Rng rng, double u, double v, double angle, double length, double width, int depth,
            double spread, double shrink, double up, List<Cluster> tips, Rgb colour)
        {
            double u1 = u + Math.Cos(angle) * length, v1 = v + Math.Sin(angle) * length, w1 = width * 0.6;
            Branch(c, u, v, u1, v1, width, w1, rng.Range(-0.12, 0.12), colour);
            if (depth == 0)
            {
                tips.Add(new Cluster(u1, v1, length));
                return;
            }
            tips.Add(new Cluster(u1, v1, length * 0.55));
            int k = rng.Next() < 0.55 ? 2 : 3;
            for (int i = 0; i < k; i++)
            {
                double f = i / (double)(k - 1) * 2.0 - 1.0;
                double a = angle + spread * f + rng.Range(-0.3, 0.3) * spread;
                a += (Math.PI / 2 - a) * up;
                Grow(c, rng, u1, v1, a, length * rng.Range(shrink - 0.08, shrink + 0.06), w1, depth - 1, spread, shrink, up, tips, colour);
            }
        }

        /// <summary>Clusters at the recorded tips, sized from the branch lengths.</summary>
        private static List<Cluster> ClustersFrom(List<Cluster> tips, double scale, double minimum)
        {
            var clusters = new List<Cluster>(tips.Count);
            foreach (Cluster t in tips) { clusters.Add(new Cluster(t.U, t.V, Math.Max(t.R * scale, minimum))); }
            return clusters;
        }

        /// <summary>
        /// Leaves around each cluster in three layers (back to front, each a little smaller and higher), denser in the
        /// middle and ragged at the edge; lit across the whole canopy from the upper left, so clusters don't each read
        /// as a ball. Needles hang down and out.
        /// </summary>
        private static void Foliage(Canvas c, Rng rng, List<Cluster> clusters, double leaf, double density, Palette pal, bool needle = false)
        {
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (Cluster k in clusters)
            {
                u0 = Math.Min(u0, k.U - k.R);
                u1 = Math.Max(u1, k.U + k.R);
                v0 = Math.Min(v0, k.V - k.R);
                v1 = Math.Max(v1, k.V + k.R);
            }
            for (int layer = 0; layer < 3; layer++)
            {
                double shift = layer * 0.08, reach = 1.0 - layer * 0.12;
                foreach (Cluster k in clusters)
                {
                    int n = (int)(density * (k.R * k.R) / (leaf * leaf) * reach);
                    for (int i = 0; i < n; i++)
                    {
                        double a = rng.Range(0.0, 2.0 * Math.PI);
                        double d = Math.Pow(rng.Next(), 0.75) * reach;
                        double u = k.U + Math.Cos(a) * k.R * d - shift * k.R * 0.3;
                        double v = k.V + Math.Sin(a) * k.R * d * 0.85 + shift * k.R * 0.3;
                        double r = leaf * rng.Range(0.7, 1.15);
                        double g = 0.65 * (v - v0) / Math.Max(v1 - v0, 1e-6) + 0.35 * (u1 - u) / Math.Max(u1 - u0, 1e-6);
                        Rgb colour = Tone(pal, 0.12 + layer * 0.22 + g * 0.45 + rng.Range(-0.14, 0.08));
                        if (needle)
                        {
                            double t = rng.Range(-1.9, -1.25);
                            if (u > k.U) { t = -Math.PI - t; }
                            c.Capsule(u, v, u + Math.Cos(t) * r * 2.6, v + Math.Sin(t) * r * 2.6, r * 0.45, r * 0.25, colour);
                        }
                        else
                        {
                            double t = rng.Range(0.0, Math.PI);
                            c.Capsule(u, v, u + Math.Cos(t) * r * 1.1, v + Math.Sin(t) * r * 1.1, r, r * 0.55, colour);
                        }
                    }
                }
            }
        }

        private static Canvas TreeRound(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            var tips = new List<Cluster>();
            Branch(c, 0.0, 0.0, 0.0, 0.36, 0.042, 0.028, 0.02, TRUNK);
            c.Limb(new[] { -0.06, 0.0, 0.012, -0.02, 0.05, 0.03, 0.0, 0.1, 0.02 }, TRUNK); // root flare
            c.Limb(new[] { 0.06, 0.0, 0.012, 0.02, 0.05, 0.03, 0.0, 0.1, 0.02 }, TRUNK);
            foreach (double a in new[] { 2.25, 1.75, 1.35, 0.9 }) { Grow(c, rng, 0.0, 0.34, a, 0.17, 0.022, 3, 0.42, 0.72, 0.1, tips, TRUNK); }
            Foliage(c, rng, ClustersFrom(tips, 0.72, 0.04), 0.015, 2.4, LEAF);
            return c;
        }

        private static Canvas TreeTall(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            var tips = new List<Cluster>();
            Branch(c, 0.0, 0.0, 0.0, 0.3, 0.032, 0.022, 0.0, TRUNK);
            Branch(c, 0.0, 0.3, 0.0, 0.85, 0.022, 0.008, 0.01, TRUNK);
            for (int k = 0; k < 9; k++)
            {
                double v = 0.26 + k * 0.065;
                int side = k % 2 == 0 ? -1 : 1;
                Grow(c, rng, 0.0, v, Math.PI / 2 - side * rng.Range(0.35, 0.6), 0.1 * (1.0 - 0.45 * k / 8), 0.012, 2, 0.3, 0.7, 0.35, tips, TRUNK);
            }
            tips.Add(new Cluster(0.0, 0.9, 0.08));
            Foliage(c, rng, ClustersFrom(tips, 1.05, 0.045), 0.014, 2.3, LEAF);
            return c;
        }

        private static Canvas TreeSpreading(int seed)
        {
            var c = new Canvas(1f, 1.8f);
            var rng = new Rng(seed);
            var tips = new List<Cluster>();
            Branch(c, 0.0, 0.0, 0.0, 0.28, 0.05, 0.034, 0.03, TRUNK);
            c.Limb(new[] { -0.075, 0.0, 0.014, -0.025, 0.05, 0.035, 0.0, 0.1, 0.02 }, TRUNK);
            c.Limb(new[] { 0.075, 0.0, 0.014, 0.025, 0.05, 0.035, 0.0, 0.1, 0.02 }, TRUNK);
            double[,] limbs = { { 2.85, 0.3 }, { 2.3, 0.24 }, { 1.6, 0.2 }, { 0.85, 0.24 }, { 0.3, 0.3 } };
            for (int i = 0; i < limbs.GetLength(0); i++) { Grow(c, rng, 0.0, 0.27, limbs[i, 0], limbs[i, 1], 0.026, 3, 0.4, 0.68, -0.12, tips, TRUNK); }
            Foliage(c, rng, ClustersFrom(tips, 0.68, 0.04), 0.015, 2.3, LEAF);
            return c;
        }

        /// <summary>A eucalypt: a pale, crooked trunk and an open, drooping crown with sky through it.</summary>
        private static Canvas TreeGum(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Rgb bark = Hex(0xD8D0BE);
            var tips = new List<Cluster>();
            c.Limb(new[] { 0.0, 0.0, 0.04, 0.02, 0.22, 0.03, -0.01, 0.42, 0.025, 0.02, 0.55, 0.02 }, bark);
            c.Limb(new[] { -0.06, 0.0, 0.01, -0.02, 0.04, 0.03, 0.0, 0.1, 0.02 }, bark);
            c.Limb(new[] { 0.055, 0.0, 0.01, 0.02, 0.04, 0.03, 0.0, 0.1, 0.02 }, bark);
            for (int i = 0; i < 7; i++)
            {
                // Bark patches
                double v = rng.Range(0.05, 0.5);
                c.Ellipse(rng.Range(-0.012, 0.018), v, 0.01, 0.03, Mix(bark, Hex(0x8C8270), rng.Range(0.3, 0.6)));
            }
            double[,] limbs = { { -0.01, 0.42, 2.4, 0.2, 0.016 }, { 0.02, 0.55, 1.2, 0.2, 0.016 }, { 0.02, 0.55, 1.9, 0.18, 0.014 }, { 0.0, 0.32, 0.55, 0.22, 0.016 } };
            for (int i = 0; i < limbs.GetLength(0); i++)
            {
                Grow(c, rng, limbs[i, 0], limbs[i, 1], limbs[i, 2], limbs[i, 3], limbs[i, 4], 3, 0.55, 0.62, 0.0, tips, bark);
            }
            // Sparse, drooping clusters
            var clusters = new List<Cluster>();
            foreach (Cluster t in tips) { if (rng.Next() < 0.8) { clusters.Add(new Cluster(t.U, t.V - 0.02, Math.Max(t.R * 0.6, 0.035))); } }
            Foliage(c, rng, clusters, 0.015, 1.4, GUM);
            return c;
        }

        private static Canvas TreeConifer(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Branch(c, 0.0, 0.0, 0.0, 0.95, 0.03, 0.006, 0.0, TRUNK);
            var clusters = new List<Cluster>();
            const int tiers = 14;
            for (int k = 0; k < tiers; k++)
            {
                double t = k / (double)(tiers - 1), v = 0.12 + t * 0.8, w = 0.3 * Math.Pow(1 - t, 1.1) + 0.03;
                for (int side = -1; side <= 1; side += 2)
                {
                    Branch(c, 0.0, v + 0.02, side * w * 0.9, v - 0.03, 0.006, 0.002, 0.05 * side, TRUNK);
                    for (int j = 0; j < 3; j++)
                    {
                        double f = (j + 0.7) / 3;
                        clusters.Add(new Cluster(side * w * f * rng.Range(0.85, 1.05), v - 0.02 * f, 0.035 + 0.02 * (1 - t)));
                    }
                }
                clusters.Add(new Cluster(0.0, v, 0.03 + 0.02 * (1 - t)));
            }
            Foliage(c, rng, clusters, 0.012, 2.4, NEEDLE, needle: true);
            return c;
        }

        private static Canvas TreePalm(int seed)
        {
            var c = new Canvas(1f, 1f);
            var rng = new Rng(seed);
            // A gently curving trunk, ringed
            var chain = new double[27];
            for (int i = 0; i < 9; i++)
            {
                double t = i / 8.0;
                chain[i * 3] = 0.07 * Math.Sin(t * 1.7);
                chain[i * 3 + 1] = t * 0.64;
                chain[i * 3 + 2] = 0.026 - 0.008 * t;
            }
            c.Limb(chain, TRUNK);
            for (int i = 1; i < 34; i++)
            {
                double t = i / 34.0, u = 0.07 * Math.Sin(t * 1.7), w = 0.026 - 0.008 * t;
                c.Capsule(u - w * 0.95, t * 0.64, u + w * 0.95, t * 0.64 + 0.004, 0.0028, 0.0028, i % 2 == 1 ? BARK_LIGHT : Hex(0x473628));
            }
            double tu = 0.07 * Math.Sin(1.7), tv = 0.64;
            Rgb dead = Hex(0x8A7650);
            for (int k = 0; k < 4; k++)
            {
                // Dead fronds hanging under the crown
                double a = -Math.PI / 2 + rng.Range(-0.5, 0.5);
                Frond(c, tu, tv - 0.01, a, rng.Range(0.16, 0.22), 0.05, dead, 0.6);
            }
            c.Ellipse(tu, tv + 0.005, 0.03, 0.035, Hex(0x4C5A32)); // the crown
            for (int k = 0; k < 14; k++)
            {
                // Living fronds arching out all round (those towards or away from you look shorter)
                double a = -0.45 + k * (Math.PI + 0.9) / 13 + rng.Range(-0.1, 0.1);
                double fore = rng.Range(0.55, 1.0);
                Rgb colour = Mix(LEAF.Dark, LEAF.Mid, rng.Next() * 0.8);
                Frond(c, tu, tv, a, rng.Range(0.4, 0.48) * fore, 0.12 + 0.18 * Math.Abs(Math.Cos(a)), colour, 1.0);
            }
            return c;
        }

        /// <summary>A pinnate frond: an arching midrib with tapering leaflets either side, hanging towards the tip.</summary>
        private static void Frond(Canvas c, double u0, double v0, double angle, double length, double droop, Rgb colour, double leafletScale)
        {
            double pu = u0, pv = v0;
            const int steps = 16;
            for (int j = 1; j <= steps; j++)
            {
                double s = j / (double)steps;
                double u = u0 + Math.Cos(angle) * length * s, v = v0 + Math.Sin(angle) * length * s * 0.6 - droop * s * s;
                c.Capsule(pu, pv, u, v, 0.006 * (1 - s * 0.6), 0.006 * (1 - s * 0.6), colour);
                double du = u - pu, dv = v - pv, n = Math.Sqrt(du * du + dv * dv);
                if (n == 0) { n = 1; }
                du /= n;
                dv /= n;
                double leaf = 0.1 * Math.Sin(Math.PI * (0.15 + s * 0.8)) * leafletScale;
                for (int side = -1; side <= 1; side += 2)
                {
                    // Out from the midrib, swept towards the tip, sagging
                    double lu = du * 0.55 - dv * side * 0.85, lv = dv * 0.55 + du * side * 0.85 - 0.75;
                    double ln = Math.Sqrt(lu * lu + lv * lv);
                    lu = lu / ln * leaf;
                    lv = lv / ln * leaf;
                    double mu = u + lu * 0.5 + du * leaf * 0.1, mv = v + lv * 0.5;
                    c.Capsule(u, v, mu, mv, 0.0055, 0.004, colour);
                    c.Capsule(mu, mv, u + lu, v + lv, 0.004, 0.0012, colour);
                }
                pu = u;
                pv = v;
            }
        }

        private static Canvas ShrubRound(int seed)
        {
            var c = new Canvas(0.9f, 1.5f);
            var rng = new Rng(seed);
            for (int k = 0; k < 7; k++)
            {
                double a = 0.5 + k * (Math.PI - 1.0) / 6;
                Branch(c, 0.0, 0.0, Math.Cos(a) * 0.45, 0.1 + Math.Sin(a) * 0.3, 0.02, 0.008, 0.1, TRUNK);
            }
            var clusters = new List<Cluster>();
            for (int k = 0; k < 26; k++)
            {
                double a = rng.Range(0.15, Math.PI - 0.15), d = rng.Range(0.35, 1.0);
                clusters.Add(new Cluster(Math.Cos(a) * 0.55 * d, 0.05 + Math.Sin(a) * 0.48 * d, rng.Range(0.1, 0.15)));
            }
            Foliage(c, rng, clusters, 0.018, 2.2, LEAF);
            return c;
        }
        private static readonly Rgb G_DARK = Hex(0x3E5A2E), G_LIGHT = Hex(0x8AA65A);

        private static Canvas ShrubHedge(int seed)
        {
            var c = new Canvas(0.95f, 2.6f);
            var rng = new Rng(seed);
            c.Poly(new[] { -1.25, 0, 1.25, 0, 1.25, 0.75, -1.25, 0.75 }, G_DARK);
            for (int i = 0; i < 160; i++)
            {
                double u = rng.Range(-1.2, 1.2), v = rng.Range(0.1, 0.82), r = rng.Range(0.07, 0.13);
                c.Circle(u, v, r, Mix(G_DARK, G_LIGHT, Math.Clamp(v * 0.9 + rng.Range(-0.25, 0.15), 0, 1)));
            }
            return c;
        }

        private static Canvas ShrubGrass(int seed)
        {
            var c = new Canvas(1f, 1.3f);
            var rng = new Rng(seed);
            for (int k = 0; k < 120; k++)
            {
                double lean = rng.Range(-1, 1), length = rng.Range(0.55, 1.0) * (1 - 0.35 * Math.Abs(lean));
                double pu = rng.Range(-0.08, 0.08), pv = 0;
                Rgb colour = Mix(Hex(0x5F7036), Hex(0xB8AA66), rng.Next() * 0.8);
                for (int j = 1; j <= 8; j++)
                {
                    double s = j / 8.0;
                    double u = pu + lean * 0.55 * s * length / 8 * 2.2, v = pv + (1 - Math.Abs(lean) * s * 0.9) * length / 8;
                    c.Capsule(pu, pv, u, v, 0.013 * (1 - s * 0.6), 0.013 * (1 - (s + 1 / 8.0) * 0.6), colour);
                    pu = u;
                    pv = v;
                }
            }
            return c;
        }

        // Round X-B3: people with adult proportions (the head about 1/7.5 of the height; a child's 1/5.5), shoulders and
        // hips, limbs tapering joint to joint (thigh, knee, calf, ankle), natural stances
        private static readonly Rgb[] SKIN = { Hex(0xE0B39A), Hex(0xC68E6A), Hex(0x8D5B3E), Hex(0xF1CDB3) };
        private static readonly Rgb SHOES = Hex(0x2A2A2A);

        private static Canvas People(int kind, int seed) => kind switch
        {
            0 => PersonMan(seed),
            1 => PersonWoman(seed),
            2 => PersonChild(seed),
            _ => PersonPhone(seed)
        };

        /// <summary>Every value times <paramref name="s"/> (points or joint chains).</summary>
        private static double[] Sc(double s, params double[] values)
        {
            var output = new double[values.Length];
            for (int i = 0; i < values.Length; i++) { output[i] = values[i] * s; }
            return output;
        }

        /// <summary>One side's outline [u, v, …] top to bottom → a closed outline with the other side mirrored.</summary>
        private static double[] Mirror(double[] side)
        {
            int n = side.Length / 2;
            var output = new double[side.Length * 2];
            Array.Copy(side, output, side.Length);
            for (int i = 0; i < n; i++)
            {
                int j = n - 1 - i;
                output[side.Length + i * 2] = -side[j * 2];
                output[side.Length + i * 2 + 1] = side[j * 2 + 1];
            }
            return output;
        }

        /// <summary>A head: (long) hair behind, the hair cap, the face, a fringe across the forehead.</summary>
        private static void Head(Canvas c, double cu, double cv, double ru, double rv, Rgb skin, Rgb hair, bool longHair = false)
        {
            if (longHair)
            {
                c.Shape(new[] { cu - ru * 1.15, cv + rv * 0.2, cu - ru * 1.25, cv - rv * 1.6, cu - ru * 0.7, cv - rv * 1.85,
                    cu + ru * 0.7, cv - rv * 1.85, cu + ru * 1.25, cv - rv * 1.6, cu + ru * 1.15, cv + rv * 0.2, cu, cv + rv * 1.15 }, 3, hair);
            }
            c.Ellipse(cu, cv + rv * 0.08, ru * 1.06, rv * 1.04, hair);
            c.Ellipse(cu, cv - rv * 0.06, ru * 0.94, rv * 0.92, skin);
            c.Shape(new[] { cu - ru * 1.0, cv + rv * 0.15, cu - ru * 0.6, cv + rv * 0.75, cu, cv + rv * 1.06, cu + ru * 0.6, cv + rv * 0.75,
                cu + ru * 1.0, cv + rv * 0.15, cu + ru * 0.4, cv + rv * 0.5, cu - ru * 0.2, cv + rv * 0.42 }, 2, hair);
        }

        private static void Shoe(Canvas c, double u, double v, double w, double s) =>
            c.Shape(new[] { u - w, v, u - w * 0.85, v + 0.045 * s, u + w * 0.85, v + 0.045 * s, u + w, v, u, v - 0.004 * s }, 2, SHOES);

        private static Rgb PickSkin(Rng rng) => SKIN[Math.Min(SKIN.Length - 1, (int)(rng.Next() * SKIN.Length))];

        /// <summary>Standing at ease, the weight on one leg, a hand in a pocket.</summary>
        private static Canvas PersonMan(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Rgb skin = PickSkin(rng), top = Hex(0x3E5C7A), bottom = Hex(0x2E3440), hair = Hex(0x2B2018);
            const double s = 0.985;
            c.Limb(Sc(s, -0.05, 0.53, 0.052, -0.05, 0.40, 0.046, -0.046, 0.285, 0.032, -0.047, 0.19, 0.034, -0.046, 0.05, 0.021), bottom);
            c.Limb(Sc(s, 0.05, 0.53, 0.052, 0.058, 0.40, 0.045, 0.068, 0.285, 0.031, 0.075, 0.19, 0.033, 0.083, 0.055, 0.021), bottom);
            Shoe(c, -0.05 * s, 0.0, 0.04 * s, s);
            Shoe(c, 0.088 * s, 0.006 * s, 0.038 * s, s);
            c.Shape(Sc(s, -0.098, 0.585, 0.098, 0.585, 0.103, 0.5, 0.0, 0.47, -0.103, 0.5), 1, bottom); // hips
            c.Limb(Sc(s, 0.11, 0.79, 0.034, 0.124, 0.70, 0.03, 0.13, 0.615, 0.024, 0.134, 0.55, 0.025, 0.136, 0.485, 0.017), top);
            c.Limb(Sc(s, 0.136, 0.49, 0.018, 0.139, 0.445, 0.021, 0.137, 0.415, 0.013), skin);
            c.Limb(Sc(s, -0.11, 0.79, 0.034, -0.128, 0.70, 0.03, -0.138, 0.625, 0.024, -0.118, 0.585, 0.025, -0.092, 0.555, 0.02), top);
            c.Shape(Mirror(Sc(s, 0.0, 0.848, 0.032, 0.846, 0.112, 0.82, 0.132, 0.79, 0.116, 0.70, 0.097, 0.61, 0.1, 0.555)), 2, top);
            c.Limb(Sc(s, 0.0, 0.885, 0.021, 0.0, 0.835, 0.024), skin);
            Head(c, 0.0, 0.93 * s, 0.047 * s, 0.063 * s, skin, hair);
            return c;
        }

        /// <summary>Walking towards you: the front leg straight, the back heel lifted, the arms swinging; a dress to the knee.</summary>
        private static Canvas PersonWoman(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Rgb skin = PickSkin(rng), top = Hex(0xB5483B), hair = Hex(0x5A3A22);
            const double s = 0.985;
            c.Limb(Sc(s, 0.045, 0.5, 0.046, 0.06, 0.39, 0.04, 0.072, 0.29, 0.028, 0.082, 0.2, 0.029, 0.092, 0.1, 0.017), skin);
            c.Shape(Sc(s, 0.072, 0.088, 0.098, 0.112, 0.112, 0.07, 0.1, 0.05), 1, SHOES);
            c.Limb(Sc(s, -0.04, 0.5, 0.047, -0.042, 0.39, 0.041, -0.04, 0.285, 0.029, -0.042, 0.19, 0.03, -0.04, 0.045, 0.018), skin);
            Shoe(c, -0.04 * s, 0.0, 0.034 * s, s);
            c.Limb(Sc(s, 0.1, 0.79, 0.03, 0.118, 0.70, 0.026, 0.122, 0.625, 0.02, 0.112, 0.56, 0.021, 0.098, 0.5, 0.015), skin);
            c.Limb(Sc(s, 0.098, 0.505, 0.016, 0.094, 0.465, 0.019, 0.09, 0.44, 0.012), skin);
            c.Limb(Sc(s, -0.1, 0.79, 0.03, -0.122, 0.70, 0.026, -0.135, 0.625, 0.02, -0.148, 0.56, 0.021, -0.158, 0.5, 0.015), skin);
            c.Limb(Sc(s, -0.158, 0.505, 0.016, -0.162, 0.465, 0.019, -0.163, 0.44, 0.012), skin);
            c.Shape(Sc(s, 0.0, 0.845, 0.03, 0.843, 0.098, 0.82, 0.116, 0.792, 0.112, 0.75, 0.1, 0.70, 0.078, 0.62, 0.098, 0.53, 0.13, 0.4,
                0.15, 0.32, 0.04, 0.305, -0.12, 0.31, -0.112, 0.4, -0.098, 0.53, -0.078, 0.62, -0.1, 0.70, -0.112, 0.75, -0.116, 0.792,
                -0.098, 0.82, -0.03, 0.843), 2, top);
            c.Limb(Sc(s, 0.0, 0.885, 0.019, 0.0, 0.835, 0.021), skin);
            Head(c, 0.0, 0.93 * s, 0.045 * s, 0.062 * s, skin, hair, longHair: true);
            return c;
        }

        /// <summary>A child of about six: a big head, short legs, shorts and a T-shirt, waving.</summary>
        private static Canvas PersonChild(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Rgb skin = PickSkin(rng), top = Hex(0xE0A93B), bottom = Hex(0x3A5A8A), hair = Hex(0x7A5230);
            const double s = 0.985;
            c.Limb(Sc(s, -0.055, 0.44, 0.055, -0.058, 0.33, 0.046, -0.06, 0.22, 0.036, -0.06, 0.13, 0.038, -0.06, 0.05, 0.026), skin);
            c.Limb(Sc(s, 0.055, 0.44, 0.055, 0.062, 0.33, 0.046, 0.07, 0.22, 0.036, 0.074, 0.13, 0.038, 0.078, 0.05, 0.026), skin);
            Shoe(c, -0.06 * s, 0.0, 0.05 * s, s);
            Shoe(c, 0.08 * s, 0.004 * s, 0.05 * s, s);
            c.Shape(Sc(s, -0.11, 0.53, 0.11, 0.53, 0.125, 0.32, 0.01, 0.32, 0.0, 0.4, -0.01, 0.32, -0.12, 0.32), 1, bottom); // shorts
            c.Limb(Sc(s, 0.11, 0.69, 0.038, 0.13, 0.6, 0.033, 0.138, 0.52, 0.027, 0.14, 0.45, 0.026, 0.141, 0.4, 0.019), skin);
            c.Limb(Sc(s, 0.141, 0.405, 0.02, 0.144, 0.36, 0.024, 0.142, 0.335, 0.015), skin);
            c.Limb(Sc(s, -0.11, 0.69, 0.038, -0.15, 0.62, 0.032, -0.19, 0.58, 0.027, -0.215, 0.64, 0.026, -0.235, 0.7, 0.019), skin);
            c.Limb(Sc(s, -0.235, 0.695, 0.02, -0.245, 0.74, 0.025, -0.25, 0.77, 0.016), skin);
            c.Shape(Mirror(Sc(s, 0.0, 0.76, 0.035, 0.758, 0.11, 0.735, 0.15, 0.68, 0.13, 0.63, 0.112, 0.645, 0.11, 0.56, 0.115, 0.5)), 2, top);
            c.Limb(Sc(s, 0.0, 0.8, 0.025, 0.0, 0.75, 0.028), skin);
            Head(c, 0.0, 0.895 * s, 0.078 * s, 0.098 * s, skin, hair);
            return c;
        }

        /// <summary>On the phone: a hand at the ear, a coat to mid-thigh, a bag over the other shoulder.</summary>
        private static Canvas PersonPhone(int seed)
        {
            var c = new Canvas(1f);
            var rng = new Rng(seed);
            Rgb skin = PickSkin(rng), top = Hex(0x6B7F5A), bottom = Hex(0x4A4038), hair = Hex(0x1E1A16), bag = Hex(0x6B4A33);
            const double s = 0.985;
            c.Limb(Sc(s, -0.048, 0.5, 0.05, -0.05, 0.39, 0.044, -0.05, 0.28, 0.031, -0.052, 0.19, 0.032, -0.054, 0.05, 0.02), bottom);
            c.Limb(Sc(s, 0.048, 0.5, 0.05, 0.05, 0.39, 0.044, 0.05, 0.28, 0.031, 0.052, 0.19, 0.032, 0.054, 0.05, 0.02), bottom);
            Shoe(c, -0.056 * s, 0.0, 0.038 * s, s);
            Shoe(c, 0.056 * s, 0.0, 0.038 * s, s);
            c.Limb(Sc(s, -0.11, 0.79, 0.034, -0.128, 0.70, 0.03, -0.136, 0.62, 0.024, -0.142, 0.55, 0.025, -0.145, 0.49, 0.017), top);
            c.Limb(Sc(s, -0.145, 0.495, 0.018, -0.148, 0.45, 0.021, -0.146, 0.425, 0.013), skin);
            c.Shape(Sc(s, -0.215, 0.53, -0.135, 0.53, -0.128, 0.38, -0.222, 0.38), 1, bag);
            c.Shape(Mirror(Sc(s, 0.0, 0.848, 0.034, 0.846, 0.112, 0.82, 0.132, 0.79, 0.118, 0.70, 0.104, 0.6, 0.112, 0.5, 0.118, 0.4)), 2, top);
            c.Limb(Sc(s, 0.06, 0.84, 0.009, -0.04, 0.7, 0.009, -0.15, 0.53, 0.009), bag); // the strap
            c.Limb(Sc(s, 0.11, 0.79, 0.034, 0.138, 0.71, 0.03, 0.146, 0.665, 0.026, 0.11, 0.76, 0.025, 0.07, 0.86, 0.019), top);
            c.Limb(Sc(s, 0.0, 0.885, 0.021, 0.0, 0.835, 0.024), skin);
            Head(c, 0.0, 0.93 * s, 0.046 * s, 0.063 * s, skin, hair);
            c.Limb(Sc(s, 0.072, 0.855, 0.02, 0.06, 0.9, 0.022, 0.05, 0.925, 0.014), skin);
            c.Shape(Sc(s, 0.038, 0.875, 0.054, 0.875, 0.054, 0.945, 0.038, 0.945), 0, Hex(0x1C1C1E)); // the phone
            return c;
        }
        private static Canvas Car(bool suv)
        {
            var c = new Canvas(1.05f, 3.25f);
            Rgb body = suv ? Hex(0x5C6E7E) : Hex(0x8C96A0), glass = Hex(0x2E3A44), dark = Hex(0x1C1C1E);
            double[] wheels;
            double wr;
            if (!suv)
            {
                c.Poly(new[] { -1.55, 0.18, 1.55, 0.18, 1.6, 0.42, 1.45, 0.55, 0.75, 0.6, 0.35, 0.95, -0.75, 0.95, -1.15, 0.62, -1.58, 0.55 }, body);
                c.Poly(new[] { 0.28, 0.88, 0.62, 0.6, 0.05, 0.6, 0.05, 0.88 }, glass);
                c.Poly(new[] { -0.02, 0.88, -0.02, 0.6, -0.95, 0.6, -0.68, 0.88 }, glass);
                wheels = new[] { -1.0, 0.2, 1.0, 0.2 };
                wr = 0.21;
            }
            else
            {
                c.Poly(new[] { -1.5, 0.2, 1.5, 0.2, 1.55, 0.55, 1.35, 0.68, 0.95, 0.72, 0.62, 1.0, -1.45, 1.0, -1.55, 0.7 }, body);
                c.Poly(new[] { 0.55, 0.93, 0.82, 0.72, 0.25, 0.72, 0.25, 0.93 }, glass);
                c.Poly(new[] { 0.18, 0.93, 0.18, 0.72, -0.6, 0.72, -0.6, 0.93 }, glass);
                c.Poly(new[] { -0.68, 0.93, -0.68, 0.72, -1.35, 0.72, -1.35, 0.93 }, glass);
                wheels = new[] { -0.95, 0.22, 0.95, 0.22 };
                wr = 0.24;
            }
            for (int i = 0; i < wheels.Length; i += 2)
            {
                c.Circle(wheels[i], wheels[i + 1], wr, dark);
                c.Circle(wheels[i], wheels[i + 1], wr * 0.55, Hex(0xA9AFB5));
                c.Circle(wheels[i], wheels[i + 1], wr * 0.18, dark);
            }
            c.Ellipse(1.5, 0.47, 0.06, 0.04, Hex(0xF2E6B0));
            c.Ellipse(-1.52, 0.48, 0.05, 0.04, Hex(0xB03028));
            return c;
        }

        #endregion
    }
}
