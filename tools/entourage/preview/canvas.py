"""Python (numpy) mirror of BimGo.Core EntourageArt.Canvas, for previews. Keep in step with the C#."""
import math
import numpy as np

SIZE = 512
MARGIN = 8


def hexrgb(h):
    return (((h >> 16) & 255) / 255.0, ((h >> 8) & 255) / 255.0, (h & 255) / 255.0)


def mix(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)


BLACK = (0.0, 0.0, 0.0)


class Rng:
    def __init__(self, seed):
        self.x = (seed * 2654435761 + 12345) & 0xFFFFFFFF
        if self.x == 0:
            self.x = 1

    def next(self):
        x = self.x
        x ^= (x << 13) & 0xFFFFFFFF
        x ^= x >> 17
        x ^= (x << 5) & 0xFFFFFFFF
        self.x = x
        return x / 4294967296.0

    def range(self, a, b):
        return a + (b - a) * self.next()


def chaikin(points, iterations, closed=True):
    """Chaikin corner cutting on a flat [u0, v0, u1, v1, ...] list (closed or open, the ends kept when open)."""
    pts = list(points)
    for _ in range(iterations):
        n = len(pts) // 2
        out = []
        if not closed:
            out += [pts[0], pts[1]]
        count = n if closed else n - 1
        for i in range(count):
            j = (i + 1) % n
            ax, ay, bx, by = pts[i * 2], pts[i * 2 + 1], pts[j * 2], pts[j * 2 + 1]
            out += [0.75 * ax + 0.25 * bx, 0.75 * ay + 0.25 * by, 0.25 * ax + 0.75 * bx, 0.25 * ay + 0.75 * by]
        if not closed:
            out += [pts[-2], pts[-1]]
        pts = out
    return pts


def limb_outline(chain):
    """A tapered limb's outline from joints [u, v, half width, ...]: both sides plus a round cap at each end."""
    n = len(chain) // 3
    left, right = [], []
    dirs = []
    for i in range(n):
        a = max(i - 1, 0)
        b = min(i + 1, n - 1)
        du = chain[b * 3] - chain[a * 3]
        dv = chain[b * 3 + 1] - chain[a * 3 + 1]
        l = math.sqrt(du * du + dv * dv)
        if l < 1e-9:
            l = 1.0
        du /= l
        dv /= l
        dirs.append((du, dv))
        u, v, w = chain[i * 3], chain[i * 3 + 1], chain[i * 3 + 2]
        left += [u - dv * w, v + du * w]
        right += [u + dv * w, v - du * w]
    pts = []
    # start cap
    du, dv = dirs[0]
    u, v, w = chain[0], chain[1], chain[2]
    pts += [u - du * w * 0.7, v - dv * w * 0.7]
    pts += left
    du, dv = dirs[-1]
    u, v, w = chain[-3], chain[-2], chain[-1]
    pts += [u + du * w * 0.7, v + dv * w * 0.7]
    for i in range(n - 1, -1, -1):
        pts += [right[i * 2], right[i * 2 + 1]]
    return pts


class Canvas:
    def __init__(self, height_units, width_units=0.0):
        self.r = np.zeros((SIZE, SIZE), np.float32)
        self.g = np.zeros((SIZE, SIZE), np.float32)
        self.b = np.zeros((SIZE, SIZE), np.float32)
        self.a = np.zeros((SIZE, SIZE), np.float32)
        self.scale = (SIZE - 2.0 * MARGIN) / max(height_units, width_units)

    def px(self, u):
        return SIZE / 2.0 + u * self.scale

    def py(self, v):
        return SIZE - MARGIN - v * self.scale

    def L(self, l):
        return l * self.scale

    @staticmethod
    def clip(x0, y0, x1, y1):
        return (max(0, int(math.floor(x0)) - 1), max(0, int(math.floor(y0)) - 1),
                min(SIZE, int(math.ceil(x1)) + 2), min(SIZE, int(math.ceil(y1)) + 2))

    def blend(self, ix0, iy0, cov, c):
        cov = np.clip(cov, 0.0, 1.0).astype(np.float32)
        h, w = cov.shape
        sl = (slice(iy0, iy0 + h), slice(ix0, ix0 + w))
        k = 1.0 - cov
        self.r[sl] = self.r[sl] * k + c[0] * cov
        self.g[sl] = self.g[sl] * k + c[1] * cov
        self.b[sl] = self.b[sl] * k + c[2] * cov
        self.a[sl] = self.a[sl] + cov * (1.0 - self.a[sl])

    def circle(self, u, v, r, c):
        self.ellipse(u, v, r, r, c)

    def ellipse(self, u, v, ru, rv, c):
        cx, cy = self.px(u), self.py(v)
        a, b = max(self.L(ru), 0.01), max(self.L(rv), 0.01)
        x0, y0, x1, y1 = self.clip(cx - a, cy - b, cx + a, cy + b)
        if x1 <= x0 or y1 <= y0:
            return
        X, Y = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        dx, dy = (X - cx) / a, (Y - cy) / b
        d = np.sqrt(dx * dx + dy * dy)
        self.blend(x0, y0, (1.0 - d) * min(a, b) + 0.5, c)

    def capsule(self, u0, v0, u1, v1, r0, r1, c):
        ax, ay, bx, by = self.px(u0), self.py(v0), self.px(u1), self.py(v1)
        ra, rb = self.L(r0), self.L(r1)
        dx, dy = bx - ax, by - ay
        l2 = max(dx * dx + dy * dy, 1e-9)
        m = max(ra, rb)
        x0, y0, x1, y1 = self.clip(min(ax, bx) - m, min(ay, by) - m, max(ax, bx) + m, max(ay, by) + m)
        if x1 <= x0 or y1 <= y0:
            return
        X, Y = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        t = np.clip(((X - ax) * dx + (Y - ay) * dy) / l2, 0.0, 1.0)
        qx, qy = ax + t * dx - X, ay + t * dy - Y
        r = ra + (rb - ra) * t
        self.blend(x0, y0, r - np.sqrt(qx * qx + qy * qy) + 0.5, c)

    def poly(self, points, c):
        self.rings([points], c)

    def rings(self, rings, c):
        """Filled rings together, even-odd (an inner ring is a hole), 4 x 4 samples a pixel."""
        edges = []
        for ring in rings:
            n = len(ring) // 2
            j = n - 1
            for i in range(n):
                edges.append((self.px(ring[i * 2]), self.py(ring[i * 2 + 1]), self.px(ring[j * 2]), self.py(ring[j * 2 + 1])))
                j = i
        if len(edges) < 3:
            return
        e = np.array(edges)
        x0, y0, x1, y1 = self.clip(e[:, 0].min(), e[:, 1].min(), e[:, 0].max(), e[:, 1].max())
        if x1 <= x0 or y1 <= y0:
            return
        sub = (np.arange(4) + 0.5) / 4.0
        xs = (np.arange(x0, x1)[:, None] + sub[None, :]).reshape(-1)
        ys = (np.arange(y0, y1)[:, None] + sub[None, :]).reshape(-1)
        QX, QY = np.meshgrid(xs, ys)
        odd = np.zeros(QX.shape, bool)
        for (xi, yi, xj, yj) in edges:
            dyy = yj - yi
            if dyy == 0:
                dyy = 1e-9
            cross = (yi > QY) != (yj > QY)
            odd ^= cross & (QX < (xj - xi) * (QY - yi) / dyy + xi)
        h, w = y1 - y0, x1 - x0
        cov = odd.reshape(h, 4, w, 4).sum(axis=(1, 3)) / 16.0
        self.blend(x0, y0, cov, c)

    # X-B3 helpers (mirrored in C#)
    def shape(self, points, iterations, c):
        """A closed outline rounded by Chaikin corner cutting, then filled."""
        self.poly(chaikin(points, iterations), c)

    def limb(self, chain, c):
        """A tapered limb through joints [u, v, half width, ...], smoothed."""
        self.poly(chaikin(limb_outline(chain), 2), c)

    def to_rgba(self):
        a = self.a
        k = np.where(a > 1e-4, 1.0 / np.maximum(a, 1e-4), 0.0)
        out = np.zeros((SIZE, SIZE, 4), np.uint8)
        out[..., 0] = np.clip(self.r * k * 255 + 0.5, 0, 255)
        out[..., 1] = np.clip(self.g * k * 255 + 0.5, 0, 255)
        out[..., 2] = np.clip(self.b * k * 255 + 0.5, 0, 255)
        out[..., 3] = np.clip(a * 255 + 0.5, 0, 255)
        return out
