"""X-B3 trees and shrubs (mirrored in EntourageArt.cs): branch skeletons with foliage clusters of small leaves."""
import math
from canvas import Canvas, Rng, hexrgb, mix, BLACK

TRUNK = hexrgb(0x5B4636)
BARK_LIGHT = hexrgb(0x7A6450)


class Palette:
    def __init__(self, dark, mid, light):
        self.dark, self.mid, self.light = hexrgb(dark), hexrgb(mid), hexrgb(light)


LEAF = Palette(0x3A5530, 0x5C7C3C, 0x8FAA5C)
GUM = Palette(0x4F5A3A, 0x7A8456, 0xA7AE7C)
NEEDLE = Palette(0x23392A, 0x34523A, 0x557650)


def branch(c, u0, v0, u1, v1, w0, w1, bend, colour):
    """A tapered, slightly bent limb."""
    du, dv = u1 - u0, v1 - v0
    mu, mv = (u0 + u1) * 0.5 - dv * bend, (v0 + v1) * 0.5 + du * bend
    c.limb([u0, v0, w0, mu, mv, (w0 + w1) * 0.5, u1, v1, w1], colour)


def grow(c, rng, u, v, angle, length, width, depth, spread, shrink, up, tips, colour):
    """
    A branch and (depth > 0) its 2–3 children, fanned by `spread` (rad) and pulled towards vertical by `up` (−: towards
    horizontal); every tip (and every fork, smaller) is recorded for foliage as (u, v, size).
    """
    u1 = u + math.cos(angle) * length
    v1 = v + math.sin(angle) * length
    w1 = width * 0.6
    branch(c, u, v, u1, v1, width, w1, rng.range(-0.12, 0.12), colour)
    if depth == 0:
        tips.append((u1, v1, length))
        return
    tips.append((u1, v1, length * 0.55))
    k = 2 if rng.next() < 0.55 else 3
    for i in range(k):
        f = (i / (k - 1)) * 2.0 - 1.0
        a = angle + spread * f + rng.range(-0.3, 0.3) * spread
        a += (math.pi / 2 - a) * up
        grow(c, rng, u1, v1, a, length * rng.range(shrink - 0.08, shrink + 0.06), w1, depth - 1, spread, shrink, up, tips, colour)


def tone(pal, p):
    """The palette from dark (0) through mid (0.5) to light (1)."""
    p = min(max(p, 0.0), 1.0)
    return mix(pal.dark, pal.mid, p * 2.0) if p < 0.5 else mix(pal.mid, pal.light, (p - 0.5) * 2.0)


def foliage(c, rng, clusters, leaf, density, pal, needle=False):
    """
    Leaves around each cluster (u, v, radius) in three layers (back to front, each a little smaller and higher), denser
    in the middle and ragged at the edge; lit across the whole canopy from the upper left, so clusters don't each
    read as a ball.
    """
    u0 = min(u - r for (u, v, r) in clusters)
    u1 = max(u + r for (u, v, r) in clusters)
    v0 = min(v - r for (u, v, r) in clusters)
    v1 = max(v + r for (u, v, r) in clusters)
    for layer in range(3):
        shift, reach = layer * 0.08, 1.0 - layer * 0.12
        for (cu, cv, cr) in clusters:
            n = int(density * (cr * cr) / (leaf * leaf) * reach)
            for _ in range(n):
                a = rng.range(0.0, 2.0 * math.pi)
                d = (rng.next() ** 0.75) * reach
                u = cu + math.cos(a) * cr * d - shift * cr * 0.3
                v = cv + math.sin(a) * cr * d * 0.85 + shift * cr * 0.3
                r = leaf * rng.range(0.7, 1.15)
                g = 0.65 * (v - v0) / max(v1 - v0, 1e-6) + 0.35 * (u1 - u) / max(u1 - u0, 1e-6)
                colour = tone(pal, 0.12 + layer * 0.22 + g * 0.45 + rng.range(-0.14, 0.08))
                if needle:
                    t = rng.range(-1.9, -1.25)
                    if u > cu:
                        t = -math.pi - t
                    c.capsule(u, v, u + math.cos(t) * r * 2.6, v + math.sin(t) * r * 2.6, r * 0.45, r * 0.25, colour)
                else:
                    t = rng.range(0.0, math.pi)
                    c.capsule(u, v, u + math.cos(t) * r * 1.1, v + math.sin(t) * r * 1.1, r, r * 0.55, colour)


def clusters_from(tips, scale, minimum):
    return [(u, v, max(size * scale, minimum)) for (u, v, size) in tips]


def tree_round(seed):
    c = Canvas(1.0)
    rng = Rng(seed)
    tips = []
    branch(c, 0.0, 0.0, 0.0, 0.36, 0.042, 0.028, 0.02, TRUNK)
    c.limb([-0.06, 0.0, 0.012, -0.02, 0.05, 0.03, 0.0, 0.1, 0.02], TRUNK)  # root flare
    c.limb([0.06, 0.0, 0.012, 0.02, 0.05, 0.03, 0.0, 0.1, 0.02], TRUNK)
    for a in (2.25, 1.75, 1.35, 0.9):
        grow(c, rng, 0.0, 0.34, a, 0.17, 0.022, 3, 0.42, 0.72, 0.1, tips, TRUNK)
    foliage(c, rng, clusters_from(tips, 0.72, 0.04), 0.015, 2.4, LEAF)
    return c


def tree_tall(seed):
    c = Canvas(1.0)
    rng = Rng(seed)
    tips = []
    branch(c, 0.0, 0.0, 0.0, 0.3, 0.032, 0.022, 0.0, TRUNK)
    branch(c, 0.0, 0.3, 0.0, 0.85, 0.022, 0.008, 0.01, TRUNK)
    for k in range(9):
        v = 0.26 + k * 0.065
        side = -1 if k % 2 == 0 else 1
        grow(c, rng, 0.0, v, math.pi / 2 - side * rng.range(0.35, 0.6), 0.1 * (1.0 - 0.45 * k / 8), 0.012, 2, 0.3, 0.7, 0.35, tips, TRUNK)
    tips.append((0.0, 0.9, 0.08))
    foliage(c, rng, clusters_from(tips, 1.05, 0.045), 0.014, 2.3, LEAF)
    return c


def tree_spreading(seed):
    c = Canvas(1.0, 1.8)
    rng = Rng(seed)
    tips = []
    branch(c, 0.0, 0.0, 0.0, 0.28, 0.05, 0.034, 0.03, TRUNK)
    c.limb([-0.075, 0.0, 0.014, -0.025, 0.05, 0.035, 0.0, 0.1, 0.02], TRUNK)
    c.limb([0.075, 0.0, 0.014, 0.025, 0.05, 0.035, 0.0, 0.1, 0.02], TRUNK)
    for a, l in ((2.85, 0.3), (2.3, 0.24), (1.6, 0.2), (0.85, 0.24), (0.3, 0.3)):
        grow(c, rng, 0.0, 0.27, a, l, 0.026, 3, 0.4, 0.68, -0.12, tips, TRUNK)
    foliage(c, rng, clusters_from(tips, 0.68, 0.04), 0.015, 2.3, LEAF)
    return c


def tree_gum(seed):
    """A eucalypt: a pale, crooked trunk and an open, drooping crown with sky through it."""
    c = Canvas(1.0)
    rng = Rng(seed)
    bark = hexrgb(0xD8D0BE)
    tips = []
    c.limb([0.0, 0.0, 0.04, 0.02, 0.22, 0.03, -0.01, 0.42, 0.025, 0.02, 0.55, 0.02], bark)
    c.limb([-0.06, 0.0, 0.01, -0.02, 0.04, 0.03, 0.0, 0.1, 0.02], bark)
    c.limb([0.055, 0.0, 0.01, 0.02, 0.04, 0.03, 0.0, 0.1, 0.02], bark)
    for i in range(7):  # bark patches
        v = rng.range(0.05, 0.5)
        c.ellipse(rng.range(-0.012, 0.018), v, 0.01, 0.03, mix(bark, hexrgb(0x8C8270), rng.range(0.3, 0.6)))
    for (u, v, a, l, w) in ((-0.01, 0.42, 2.4, 0.2, 0.016), (0.02, 0.55, 1.2, 0.2, 0.016), (0.02, 0.55, 1.9, 0.18, 0.014),
                            (0.0, 0.32, 0.55, 0.22, 0.016)):
        grow(c, rng, u, v, a, l, w, 3, 0.55, 0.62, 0.0, tips, bark)
    # Sparse, drooping clusters (fewer leaves, longer and hanging)
    cl = [(u, v - 0.02, max(s * 0.6, 0.035)) for (u, v, s) in tips if rng.next() < 0.8]
    foliage(c, rng, cl, 0.015, 1.4, GUM)
    return c


def tree_conifer(seed):
    c = Canvas(1.0)
    rng = Rng(seed)
    branch(c, 0.0, 0.0, 0.0, 0.95, 0.03, 0.006, 0.0, TRUNK)
    clusters = []
    tiers = 14
    for k in range(tiers):
        t = k / (tiers - 1)
        v = 0.12 + t * 0.8
        w = 0.3 * (1 - t) ** 1.1 + 0.03
        for side in (-1, 1):
            branch(c, 0.0, v + 0.02, side * w * 0.9, v - 0.03, 0.006, 0.002, 0.05 * side, TRUNK)
            for j in range(3):
                f = (j + 0.7) / 3
                clusters.append((side * w * f * rng.range(0.85, 1.05), v - 0.02 * f, 0.035 + 0.02 * (1 - t)))
        clusters.append((0.0, v, 0.03 + 0.02 * (1 - t)))
    foliage(c, rng, clusters, 0.012, 2.4, NEEDLE, needle=True)
    return c


def tree_palm(seed):
    c = Canvas(1.0, 1.0)
    rng = Rng(seed)
    # A gently curving trunk, ringed
    chain = []
    for i in range(9):
        t = i / 8
        chain += [0.07 * math.sin(t * 1.7), t * 0.64, 0.026 - 0.008 * t]
    c.limb(chain, TRUNK)
    for i in range(1, 34):
        t = i / 34
        u, w = 0.07 * math.sin(t * 1.7), 0.026 - 0.008 * t
        c.capsule(u - w * 0.95, t * 0.64, u + w * 0.95, t * 0.64 + 0.004, 0.0028, 0.0028, BARK_LIGHT if i % 2 else hexrgb(0x473628))
    tu, tv = 0.07 * math.sin(1.7), 0.64
    dead = hexrgb(0x8A7650)
    # Dead fronds hanging under the crown
    for k in range(4):
        a = -math.pi / 2 + rng.range(-0.5, 0.5)
        frond(c, rng, tu, tv - 0.01, a, rng.range(0.16, 0.22), 0.05, dead, 0.6)
    c.ellipse(tu, tv + 0.005, 0.03, 0.035, hexrgb(0x4C5A32))  # the crown
    # Living fronds: arching out all round (the ones towards or away from you look shorter)
    for k in range(14):
        a = -0.45 + k * (math.pi + 0.9) / 13 + rng.range(-0.1, 0.1)
        fore = rng.range(0.55, 1.0)
        colour = mix(LEAF.dark, LEAF.mid, rng.next() * 0.8)
        frond(c, rng, tu, tv, a, rng.range(0.4, 0.48) * fore, 0.12 + 0.18 * abs(math.cos(a)), colour, 1.0)
    return c


def frond(c, rng, u0, v0, angle, length, droop, colour, leaflet_scale):
    """A pinnate frond: an arching midrib with tapering leaflets either side, hanging towards the tip."""
    pu, pv = u0, v0
    steps = 16
    for j in range(1, steps + 1):
        s = j / steps
        u = u0 + math.cos(angle) * length * s
        v = v0 + math.sin(angle) * length * s * 0.6 - droop * s * s
        c.capsule(pu, pv, u, v, 0.006 * (1 - s * 0.6), 0.006 * (1 - s * 0.6), colour)
        du, dv = u - pu, v - pv
        n = math.sqrt(du * du + dv * dv) or 1.0
        du, dv = du / n, dv / n
        leaf = 0.1 * math.sin(math.pi * (0.15 + s * 0.8)) * leaflet_scale
        for side in (-1, 1):
            # Leaflet direction: out from the midrib, swept towards the tip, sagging
            lu = du * 0.55 + (-dv) * side * 0.85
            lv = dv * 0.55 + du * side * 0.85 - 0.75
            ln = math.sqrt(lu * lu + lv * lv)
            lu, lv = lu / ln * leaf, lv / ln * leaf
            mu, mv = u + lu * 0.5 + du * leaf * 0.1, v + lv * 0.5
            c.capsule(u, v, mu, mv, 0.0055, 0.004, colour)
            c.capsule(mu, mv, u + lu, v + lv, 0.004, 0.0012, colour)
        pu, pv = u, v


def shrub_round(seed):
    c = Canvas(0.9, 1.5)
    rng = Rng(seed)
    for k in range(7):
        a = 0.5 + k * (math.pi - 1.0) / 6
        branch(c, 0.0, 0.0, math.cos(a) * 0.45, 0.1 + math.sin(a) * 0.3, 0.02, 0.008, 0.1, TRUNK)
    cl = []
    for k in range(26):
        a = rng.range(0.15, math.pi - 0.15)
        d = rng.range(0.35, 1.0)
        cl.append((math.cos(a) * 0.55 * d, 0.05 + math.sin(a) * 0.48 * d, rng.range(0.1, 0.15)))
    foliage(c, rng, cl, 0.018, 2.2, LEAF)
    return c
