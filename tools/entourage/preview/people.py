"""X-B3 people (mirrored in EntourageArt.cs)."""
from canvas import Canvas, Rng, hexrgb, mix

SKIN = [hexrgb(0xE0B39A), hexrgb(0xC68E6A), hexrgb(0x8D5B3E), hexrgb(0xF1CDB3)]
SHOES = hexrgb(0x2A2A2A)


def mirror(points):
    """[u, v, ...] down one side (top to bottom) → a closed outline with the other side mirrored."""
    n = len(points) // 2
    out = list(points)
    for i in range(n - 1, -1, -1):
        out += [-points[i * 2], points[i * 2 + 1]]
    return out


def scaled(chain, s, du=0.0):
    """A joint chain [u, v, w, ...] scaled by s (and shifted)."""
    out = []
    for i in range(0, len(chain), 3):
        out += [chain[i] * s + du, chain[i + 1] * s, chain[i + 2] * s]
    return out


def head(c, s, cu, cv, ru, rv, skin, hair, long_hair=False):
    """A head: hair behind, the face, a fringe over the top."""
    if long_hair:
        c.shape([cu - ru * 1.15, cv + rv * 0.2, cu - ru * 1.25, cv - rv * 1.6, cu - ru * 0.7, cv - rv * 1.85,
                 cu + ru * 0.7, cv - rv * 1.85, cu + ru * 1.25, cv - rv * 1.6, cu + ru * 1.15, cv + rv * 0.2,
                 cu, cv + rv * 1.15], 3, hair)
    c.ellipse(cu, cv + rv * 0.08, ru * 1.06, rv * 1.04, hair)
    c.ellipse(cu, cv - rv * 0.06, ru * 0.94, rv * 0.92, skin)
    # Fringe: the hair line across the forehead
    c.shape([cu - ru * 1.0, cv + rv * 0.15, cu - ru * 0.6, cv + rv * 0.75, cu, cv + rv * 1.06, cu + ru * 0.6, cv + rv * 0.75,
             cu + ru * 1.0, cv + rv * 0.15, cu + ru * 0.4, cv + rv * 0.5, cu - ru * 0.2, cv + rv * 0.42], 2, hair)


def shoe(c, u, v, w, s):
    c.shape([u - w, v, u - w * 0.85, v + 0.045 * s, u + w * 0.85, v + 0.045 * s, u + w, v, u, v - 0.004 * s], 2, SHOES)


def person_man(seed):
    """Standing at ease, weight on one leg, a hand in a pocket."""
    c = Canvas(1.0)
    rng = Rng(seed)
    skin = SKIN[min(3, int(rng.next() * 4))]
    top, bottom, hair = hexrgb(0x3E5C7A), hexrgb(0x2E3440), hexrgb(0x2B2018)
    s = 0.985
    # Legs (left leg straight, carrying the weight; the right relaxed, knee eased)
    c.limb(scaled([-0.05, 0.53, 0.052, -0.05, 0.40, 0.046, -0.046, 0.285, 0.032, -0.047, 0.19, 0.034, -0.046, 0.05, 0.021], s), bottom)
    c.limb(scaled([0.05, 0.53, 0.052, 0.058, 0.40, 0.045, 0.068, 0.285, 0.031, 0.075, 0.19, 0.033, 0.083, 0.055, 0.021], s), bottom)
    shoe(c, -0.05 * s, 0.0, 0.04 * s, s)
    shoe(c, 0.088 * s, 0.006 * s, 0.038 * s, s)
    # Hips (trousers)
    c.shape([-0.098 * s, 0.585 * s, 0.098 * s, 0.585 * s, 0.103 * s, 0.5 * s, 0.0, 0.47 * s, -0.103 * s, 0.5 * s], 1, bottom)
    # Arms behind the torso's edge: the right hangs, the left's hand in the pocket
    c.limb(scaled([0.11, 0.79, 0.034, 0.124, 0.70, 0.03, 0.13, 0.615, 0.024, 0.134, 0.55, 0.025, 0.136, 0.485, 0.017], s), top)
    c.limb(scaled([0.136, 0.49, 0.018, 0.139, 0.445, 0.021, 0.137, 0.415, 0.013], s), skin)
    c.limb(scaled([-0.11, 0.79, 0.034, -0.128, 0.70, 0.03, -0.138, 0.625, 0.024, -0.118, 0.585, 0.025, -0.092, 0.555, 0.02], s), top)
    # Torso (a shirt / jacket to the hips)
    c.shape(mirror([0.0, 0.848 * s, 0.032 * s, 0.846 * s, 0.112 * s, 0.82 * s, 0.132 * s, 0.79 * s, 0.116 * s, 0.70 * s,
                    0.097 * s, 0.61 * s, 0.1 * s, 0.555 * s]), 2, top)
    # Neck and head
    c.limb(scaled([0.0, 0.885, 0.021, 0.0, 0.835, 0.024], s), skin)
    head(c, s, 0.0, 0.93 * s, 0.047 * s, 0.063 * s, skin, hair)
    return c


def person_woman(seed):
    """Walking towards you: the front leg straight, the back heel lifted, the arms swinging; a skirt to the knee."""
    c = Canvas(1.0)
    rng = Rng(seed)
    skin = SKIN[min(3, int(rng.next() * 4))]
    top, bottom, hair = hexrgb(0xB5483B), hexrgb(0x2E3440), hexrgb(0x5A3A22)
    s = 0.985
    # Back leg (bent, the heel up) then the front one
    c.limb(scaled([0.045, 0.5, 0.046, 0.06, 0.39, 0.04, 0.072, 0.29, 0.028, 0.082, 0.2, 0.029, 0.092, 0.1, 0.017], s), skin)
    c.shape([0.072 * s, 0.088 * s, 0.098 * s, 0.112 * s, 0.112 * s, 0.07 * s, 0.1 * s, 0.05 * s], 1, SHOES)
    c.limb(scaled([-0.04, 0.5, 0.047, -0.042, 0.39, 0.041, -0.04, 0.285, 0.029, -0.042, 0.19, 0.03, -0.04, 0.045, 0.018], s), skin)
    shoe(c, -0.04 * s, 0.0, 0.034 * s, s)
    # Arms: the right swings forward (foreshortened, towards the body), the left back
    c.limb(scaled([0.1, 0.79, 0.03, 0.118, 0.70, 0.026, 0.122, 0.625, 0.02, 0.112, 0.56, 0.021, 0.098, 0.5, 0.015], s), skin)
    c.limb(scaled([0.098, 0.505, 0.016, 0.094, 0.465, 0.019, 0.09, 0.44, 0.012], s), skin)
    c.limb(scaled([-0.1, 0.79, 0.03, -0.122, 0.70, 0.026, -0.135, 0.625, 0.02, -0.148, 0.56, 0.021, -0.158, 0.5, 0.015], s), skin)
    c.limb(scaled([-0.158, 0.505, 0.016, -0.162, 0.465, 0.019, -0.163, 0.44, 0.012], s), skin)
    # Dress: fitted to the waist, an A-line skirt swinging a little with the stride
    c.shape([0.0, 0.845 * s, 0.03 * s, 0.843 * s, 0.098 * s, 0.82 * s, 0.116 * s, 0.792 * s, 0.112 * s, 0.75 * s,
             0.1 * s, 0.70 * s, 0.078 * s, 0.62 * s, 0.098 * s, 0.53 * s, 0.13 * s, 0.4 * s, 0.15 * s, 0.32 * s,
             0.04 * s, 0.305 * s, -0.12 * s, 0.31 * s, -0.112 * s, 0.4 * s, -0.098 * s, 0.53 * s, -0.078 * s, 0.62 * s,
             -0.1 * s, 0.70 * s, -0.112 * s, 0.75 * s, -0.116 * s, 0.792 * s, -0.098 * s, 0.82 * s, -0.03 * s, 0.843 * s], 2, top)
    # Neck and head (hair to the shoulders)
    c.limb(scaled([0.0, 0.885, 0.019, 0.0, 0.835, 0.021], s), skin)
    head(c, s, 0.0, 0.93 * s, 0.045 * s, 0.062 * s, skin, hair, long_hair=True)
    return c


def person_child(seed):
    """A child of about six: a big head (a fifth of the height), short legs, shorts and a T-shirt."""
    c = Canvas(1.0)
    rng = Rng(seed)
    skin = SKIN[min(3, int(rng.next() * 4))]
    top, bottom, hair = hexrgb(0xE0A93B), hexrgb(0x3A5A8A), hexrgb(0x7A5230)
    s = 0.985
    c.limb(scaled([-0.055, 0.44, 0.055, -0.058, 0.33, 0.046, -0.06, 0.22, 0.036, -0.06, 0.13, 0.038, -0.06, 0.05, 0.026], s), skin)
    c.limb(scaled([0.055, 0.44, 0.055, 0.062, 0.33, 0.046, 0.07, 0.22, 0.036, 0.074, 0.13, 0.038, 0.078, 0.05, 0.026], s), skin)
    shoe(c, -0.06 * s, 0.0, 0.05 * s, s)
    shoe(c, 0.08 * s, 0.004 * s, 0.05 * s, s)
    # Shorts
    c.shape([-0.11 * s, 0.53 * s, 0.11 * s, 0.53 * s, 0.125 * s, 0.32 * s, 0.01 * s, 0.32 * s, 0.0, 0.4 * s,
             -0.01 * s, 0.32 * s, -0.12 * s, 0.32 * s], 1, bottom)
    # Arms (one raised a little, waving)
    c.limb(scaled([0.11, 0.69, 0.038, 0.13, 0.6, 0.033, 0.138, 0.52, 0.027, 0.14, 0.45, 0.026, 0.141, 0.4, 0.019], s), skin)
    c.limb(scaled([0.141, 0.405, 0.02, 0.144, 0.36, 0.024, 0.142, 0.335, 0.015], s), skin)
    c.limb(scaled([-0.11, 0.69, 0.038, -0.15, 0.62, 0.032, -0.19, 0.58, 0.027, -0.215, 0.64, 0.026, -0.235, 0.7, 0.019], s), skin)
    c.limb(scaled([-0.235, 0.695, 0.02, -0.245, 0.74, 0.025, -0.25, 0.77, 0.016], s), skin)
    # T-shirt with short sleeves
    c.shape(mirror([0.0, 0.76 * s, 0.035 * s, 0.758 * s, 0.11 * s, 0.735 * s, 0.15 * s, 0.68 * s, 0.13 * s, 0.63 * s,
                    0.112 * s, 0.645 * s, 0.11 * s, 0.56 * s, 0.115 * s, 0.5 * s]), 2, top)
    c.limb(scaled([0.0, 0.8, 0.025, 0.0, 0.75, 0.028], s), skin)
    head(c, s, 0.0, 0.895 * s, 0.078 * s, 0.098 * s, skin, hair)
    return c


def person_phone(seed):
    """On the phone: one hand at the ear, a coat to mid-thigh, a bag over the other shoulder."""
    c = Canvas(1.0)
    rng = Rng(seed)
    skin = SKIN[min(3, int(rng.next() * 4))]
    top, bottom, hair = hexrgb(0x6B7F5A), hexrgb(0x4A4038), hexrgb(0x1E1A16)
    bag = hexrgb(0x6B4A33)
    s = 0.985
    c.limb(scaled([-0.048, 0.5, 0.05, -0.05, 0.39, 0.044, -0.05, 0.28, 0.031, -0.052, 0.19, 0.032, -0.054, 0.05, 0.02], s), bottom)
    c.limb(scaled([0.048, 0.5, 0.05, 0.05, 0.39, 0.044, 0.05, 0.28, 0.031, 0.052, 0.19, 0.032, 0.054, 0.05, 0.02], s), bottom)
    shoe(c, -0.056 * s, 0.0, 0.038 * s, s)
    shoe(c, 0.056 * s, 0.0, 0.038 * s, s)
    # The arm holding the bag's strap, then the bag
    c.limb(scaled([-0.11, 0.79, 0.034, -0.128, 0.70, 0.03, -0.136, 0.62, 0.024, -0.142, 0.55, 0.025, -0.145, 0.49, 0.017], s), top)
    c.limb(scaled([-0.145, 0.495, 0.018, -0.148, 0.45, 0.021, -0.146, 0.425, 0.013], s), skin)
    c.shape([-0.215 * s, 0.53 * s, -0.135 * s, 0.53 * s, -0.128 * s, 0.38 * s, -0.222 * s, 0.38 * s], 1, bag)
    # Coat to mid-thigh
    c.shape(mirror([0.0, 0.848 * s, 0.034 * s, 0.846 * s, 0.112 * s, 0.82 * s, 0.132 * s, 0.79 * s, 0.118 * s, 0.70 * s,
                    0.104 * s, 0.6 * s, 0.112 * s, 0.5 * s, 0.118 * s, 0.4 * s]), 2, top)
    # The strap over the shoulder
    c.limb([0.06 * s, 0.84 * s, 0.009 * s, -0.04 * s, 0.7 * s, 0.009 * s, -0.15 * s, 0.53 * s, 0.009 * s], bag)
    # The phone arm: up and in, the hand at the ear
    c.limb(scaled([0.11, 0.79, 0.034, 0.138, 0.71, 0.03, 0.146, 0.665, 0.026, 0.11, 0.76, 0.025, 0.07, 0.86, 0.019], s), top)
    c.limb(scaled([0.0, 0.885, 0.021, 0.0, 0.835, 0.024], s), skin)
    head(c, s, 0.0, 0.93 * s, 0.046 * s, 0.063 * s, skin, hair)
    c.limb(scaled([0.072, 0.855, 0.02, 0.06, 0.9, 0.022, 0.05, 0.925, 0.014], s), skin)
    c.shape([0.038 * s, 0.875 * s, 0.054 * s, 0.875 * s, 0.054 * s, 0.945 * s, 0.038 * s, 0.945 * s], 0, hexrgb(0x1C1C1E))
    return c
