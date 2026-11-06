#!/usr/bin/env python3
"""Draws the key labels Bongo Cat shows on a key that was given another key in ReplayKit, and the blank faces they go on.

    python make.py "C:\\Program Files\\obs-studio\\bin\\64bit\\Bango Cat"

Writes ../../ui/overlay-preview/bongo-keys/: one white label per key in three styles
(hand and handbold: the Comic Sans letters of the standard and keyboard modes, at each one's weight; plain: the Arial letters of the
feixue keys), the blank face of every key of every mode as a patch of its picture, and manifest.json, which says how to draw a label
on each key: the label the key shows today is fitted onto the font's same label, so a new one lands as big, as stretched, as leaned
and in the same place as the artist drew the old. See README.md for how the pieces are used.

A few glyphs of the font are drawn otherwise than the artist's Comic Sans (Comic Relief's 4 is closed, its 1 has a foot): those are
traced from the pictures instead, their centre line stroked again at the font's weight (TRACE).

Needs: pip install pillow numpy opencv-python-headless scikit-image. The fonts are fetched from google/fonts the first time.
"""
import hashlib
import json
import os
import sys
import tempfile
import urllib.request

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from skimage.morphology import skeletonize

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "ui", "overlay-preview", "bongo-keys"))
FONTS = {
    # the standard and keyboard modes' labels are Comic Sans drawn onto the keys; Comic Relief (SIL Open Font License) is drawn to
    # its shapes and metrics. Their strokes are about 15% of a letter's height, but 18% on the keyboard mode's pressed keys:
    # Regular thickened a little, and Bold
    "hand": ("https://raw.githubusercontent.com/google/fonts/main/ofl/comicrelief/ComicRelief-Regular.ttf", None),
    "handbold": ("https://raw.githubusercontent.com/google/fonts/main/ofl/comicrelief/ComicRelief-Bold.ttf", None),
    # Arimo (SIL Open Font License): metric-compatible with Arial, the letters of the feixue keys, a little heavier than regular as they are
    "plain": ("https://raw.githubusercontent.com/google/fonts/main/ofl/arimo/Arimo%5Bwght%5D.ttf", [550]),
}
CAP = 120  # the cap height every label is drawn at, in sprite pixels
SUPER = 2  # labels are drawn this many times bigger and scaled down, for smooth edges and a stroke of half a pixel
# the stroke added around the font's letters, in sprite pixels, to reach the weight of the pictures' letters
WEIGHT = {"hand": 2.0, "handbold": 0.0, "plain": 0.0}
SIDE_ROOM = {"hand": 1.5, "handbold": 1.5, "plain": 4.0}  # pixels of the picture kept clear at each end of a key

# the modes with labels; mania's keys are coloured blocks without one
MODES = {
    # labels facing the cat read upright with the frame turned half a turn
    "standard": {"backs": ["bg.png", "catbg.png"], "flipped": True, "style": "hand"},
    # the keyboard mode's labels are the bold weight
    "keyboard": {"backs": ["bg.png", "catbg.png"], "flipped": True, "style": "handbold"},
    # feixue's second background is the standard keyboard with the cat in it, which feixue's keys do not sit on
    "feixue": {"backs": ["l2dmousebg.png"], "flipped": False, "style": "plain"},
}
# the labels traced from the pictures instead of taken from the font, where the font's glyph is drawn otherwise; chosen by how much
# better the trace lies over the original (Q, A and W trace worse: their tail, bar and middle blur together at the pictures' size)
TRACE = {"hand": ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "KeyE", "KeyR", "KeyD", "KeyS"], "handbold": ["KeyR"]}
TRACED = {}  # style -> character -> its traced glyph, white on transparent at the sprites' size; filled in by main()

# the label every slot shows today, as the key code of the bongoKeys setting, read off the picture (the arrows by the way they point)
SLOT_LABELS = {
    "standard": ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "KeyQ", "KeyE", "KeyR", "Space", "KeyA", "KeyD", "KeyS", "KeyW"],
    "keyboard": ["ControlLeft", "ShiftLeft", "KeyR", None, None, None, None],  # the four arrows are filled in by looking at them
    "feixue": ["KeyW", "KeyA", "KeyS", "KeyD", "ShiftLeft", "ControlLeft", "Space"],
}
BLANK_SLOTS = {("standard", 10)}  # the space bar of standard is drawn without a label


def label_texts():
    """code -> (long, short or None) lines per style; a tuple of two strings is two lines (the numpad's small 'num' over the key)"""
    hand, plain = {}, {}
    for c in range(ord("A"), ord("Z") + 1):
        hand["Key" + chr(c)] = plain["Key" + chr(c)] = (chr(c), None)
    for d in range(10):
        hand["Digit%d" % d] = plain["Digit%d" % d] = (str(d), None)
        hand["Numpad%d" % d] = (("num", str(d)), str(d))
        plain["Numpad%d" % d] = (("Num", str(d)), str(d))
    for f in range(1, 13):
        hand["F%d" % f] = plain["F%d" % f] = ("F%d" % f, None)
    words = {
        "Space": ("space", "spc"), "Tab": ("tab", None), "Enter": ("enter", "ent"), "Backspace": ("backspace", "bksp"),
        "CapsLock": ("caps lock", "caps"), "Escape": ("esc", None), "ShiftLeft": ("shift", None), "ShiftRight": ("shift", None),
        "ControlLeft": ("ctrl", None), "ControlRight": ("ctrl", None), "AltLeft": ("alt", None), "AltRight": ("alt", None),
        "Insert": ("insert", "ins"), "Delete": ("delete", "del"), "Home": ("home", None), "End": ("end", None),
        "PageUp": ("page up", "pgup"), "PageDown": ("page down", "pgdn"),
    }
    for code, (a, b) in words.items():
        hand[code] = (a, b)
        plain[code] = (a.title(), b.title() if b else None)
    plain["PageUp"], plain["PageDown"] = ("Page Up", "PgUp"), ("Page Down", "PgDn")
    symbols = {"Semicolon": ";", "Equal": "=", "Comma": ",", "Minus": "-", "Period": ".", "Slash": "/", "Backquote": "`",
               "BracketLeft": "[", "Backslash": "\\", "BracketRight": "]", "Quote": "'"}
    for code, s in symbols.items():
        hand[code] = plain[code] = (s, None)
    for code, s in {"NumpadAdd": "+", "NumpadSubtract": "-", "NumpadMultiply": "*", "NumpadDivide": "/", "NumpadDecimal": "."}.items():
        hand[code] = (("num", s), s)
        plain[code] = (("Num", s), s)
    hand["NumpadEnter"] = (("num", "enter"), "ent")
    plain["NumpadEnter"] = (("Num", "Enter"), "Ent")
    for code, arrow in {"ArrowLeft": "\u2190", "ArrowUp": "\u2191", "ArrowRight": "\u2192", "ArrowDown": "\u2193"}.items():
        hand[code] = (arrow, None)  # replaced by the arrows of the keyboard mode below
        plain[code] = (arrow, None)
    return {"hand": hand, "handbold": dict(hand), "plain": plain}


# ---------------------------------------------------------------- geometry of a slot

def unit(v):
    v = np.asarray(v, float)
    return v / np.linalg.norm(v)


def key_mask(rgba):
    """the key of a pressed picture: the largest opaque cyan piece (feixue's W picture also holds the paw circles)"""
    a = rgba[..., 3] > 128
    n, lab, stats, _ = cv2.connectedComponentsWithStats(a.astype(np.uint8), 8)
    best, score = 0, -1.0
    for i in range(1, n):
        comp = lab == i
        col = rgba[comp][:, :3].astype(int)
        cyan = ((col[:, 2] > 180) & (col[:, 0] < 140)).mean()
        s = stats[i, cv2.CC_STAT_AREA] * (0.1 + cyan)
        if s > score:
            best, score = i, s
    return lab == best


def quad(mask):
    cnt, _ = cv2.findContours(mask.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    hull = cv2.convexHull(max(cnt, key=cv2.contourArea))
    peri = cv2.arcLength(hull, True)
    for eps in np.linspace(0.01, 0.2, 60):
        ap = cv2.approxPolyDP(hull, eps * peri, True)
        if len(ap) == 4:
            q = ap[:, 0, :].astype(float)
            return q if cv2.contourArea(q.astype(np.float32)) > 0.8 * mask.sum() else None
    return None


def edge_axes(mask, flipped):
    """the two directions of the key's sides, as the text runs across it (ex) and down it (ey)"""
    q = quad(mask)
    if q is None:
        return None
    e = [q[(i + 1) % 4] - q[i] for i in range(4)]
    u, v = (e[0] - e[2]) / 2, (e[1] - e[3]) / 2
    want_x = np.array([-1.0, 0.0]) if flipped else np.array([1.0, 0.0])
    want_y = np.array([0.0, -1.0]) if flipped else np.array([0.0, 1.0])
    cands = [u, -u, v, -v]
    ex = max(cands, key=lambda d: np.dot(unit(d), want_x))
    rest = [d for d in cands if abs(np.dot(unit(d), unit(ex))) < 0.9]
    ey = max(rest, key=lambda d: np.dot(unit(d), want_y))
    return unit(ex), unit(ey)


def local_box(mask, ex, ey):
    """the tightest box along the key's own axes: a point is centre + s * ex + t * ey"""
    ys, xs = np.nonzero(mask)
    pts = np.column_stack([xs + 0.5, ys + 0.5])
    st = pts @ np.linalg.inv(np.column_stack([ex, ey])).T
    s0, s1 = np.percentile(st[:, 0], [0.5, 99.5])
    t0, t1 = np.percentile(st[:, 1], [0.5, 99.5])
    centre = np.column_stack([ex, ey]) @ np.array([(s0 + s1) / 2, (t0 + t1) / 2])
    return centre, (s1 - s0, t1 - t0)


def angle(a, b):
    return np.degrees(np.arccos(np.clip(np.dot(unit(a), unit(b)), -1, 1)))


# ---------------------------------------------------------------- blank faces

def dist_smooth(alpha):
    """distance in from the key's edge, measured on its anti-aliased outline at 4x so it does not step with the pixels"""
    h, w = alpha.shape
    up = cv2.resize(alpha.astype(np.float32), (w * 4, h * 4), interpolation=cv2.INTER_LINEAR) > 0.5
    d = cv2.distanceTransform(up.astype(np.uint8), cv2.DIST_L2, 5) / 4.0
    return cv2.resize(d, (w, h), interpolation=cv2.INTER_AREA)


def border(img, mask, dark):
    """how many pixels in from the edge the key's outline reaches, the median around the key"""
    d = cv2.distanceTransform(mask.astype(np.uint8), cv2.DIST_L2, 5)
    ys, xs = np.nonzero((d > 0) & (d <= 1.5))
    cy, cx = np.nonzero(mask)[0].mean(), np.nonzero(mask)[1].mean()
    runs = []
    for y, x in zip(ys[::3], xs[::3]):
        v = np.array([cx - x, cy - y])
        v /= np.linalg.norm(v) + 1e-9
        n = 0
        for k in range(1, 12):
            px, py = int(round(x + v[0] * k)), int(round(y + v[1] * k))
            if not mask[py, px] or not dark(img[py, px]):
                break
            n = k
        runs.append(n)
    return float(np.median(runs))


def luma(px):
    return 0.3 * px[..., 0] + 0.59 * px[..., 1] + 0.11 * px[..., 2]


def blank_back(back, mask, alpha, flat):
    """the background with this key's label taken out: a flat face fills in from just inside the outline, a shaded one is in-painted"""
    out = back.copy()
    d = dist_smooth(alpha)
    dm = cv2.distanceTransform(mask.astype(np.uint8), cv2.DIST_L2, 5)
    if flat:
        inner = border(back, mask, lambda p: luma(p.astype(float)) < 110) + 0.6
        face = np.median(back[mask & (dm > inner + 1) & (back[..., 0] > 200)][:, :3], axis=0)
        w = np.clip(d - inner + 0.5, 0, 1)[..., None]
        out[..., :3] = np.clip(back[..., :3] * (1 - w) + face * w + 0.5, 0, 255).astype(np.uint8)
        return out, face
    # at least 3.5 pixels in: the thin grey outline of some keys reads as no outline, and in-painting it would bend it
    inner = max(border(back, mask, lambda p: luma(p.astype(float)) < 200) + 0.5, 3.5)
    face = np.median(back[mask & (dm > inner + 2)][:, :3], axis=0)
    # the label is the pixels clearly darker or more coloured than the face; the bevel's shading is only a little darker
    lum = luma(back[..., :3].astype(float))
    far = np.abs(back[..., :3].astype(int) - face.astype(int)).sum(axis=2) > 60
    text = (dm > inner) & far & ((lum < luma(face.astype(float)) - 70) | (np.ptp(back[..., :3].astype(int), axis=2) > 60))
    text = (cv2.dilate(text.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0) & (dm > inner - 0.5)
    out[..., :3] = cv2.inpaint(np.ascontiguousarray(back[..., :3]), text.astype(np.uint8) * 255, 4, cv2.INPAINT_TELEA)
    return out, face


def blank_pressed(pressed, mask, flat):
    """the pressed key without its label: the cyan keys of standard and keyboard are one flat colour, feixue's has an outline and is in-painted"""
    out = pressed.copy()
    opaque = mask & (pressed[..., 3] >= 200)
    out[opaque, 3] = 255
    if flat:
        face = np.median(pressed[opaque & (pressed[..., 0] < 150)][:, :3], axis=0)
        out[opaque, :3] = face.astype(np.uint8)
        return out, face
    dm = cv2.distanceTransform(mask.astype(np.uint8), cv2.DIST_L2, 5)
    inner = max(border(pressed, mask, lambda p: luma(p.astype(float)) < 170) + 0.5, 3.5)
    face = np.median(pressed[opaque & (dm > inner + 2) & (pressed[..., 2] > 200) & (pressed[..., 0] < 120)][:, :3], axis=0)
    lum = luma(pressed[..., :3].astype(float))
    text = (dm > inner) & opaque & (np.abs(pressed[..., :3].astype(int) - face.astype(int)).sum(axis=2) > 60) & (lum < luma(face.astype(float)) - 40)
    text = (cv2.dilate(text.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0) & (dm > inner - 0.5) & opaque
    out[..., :3] = cv2.inpaint(np.ascontiguousarray(out[..., :3]), text.astype(np.uint8) * 255, 4, cv2.INPAINT_TELEA)
    return out, face


def patch(original, blank):
    """the pixels the blank face changed, cut out with their place; every one of them is opaque, so drawing the patch over the picture replaces them"""
    changed = np.any(original != blank, axis=2)
    changed &= blank[..., 3] == 255
    ys, xs = np.nonzero(changed)
    if not len(xs):
        return None, None  # the key has no label to take out
    x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    p = blank[y0:y1, x0:x1].copy()
    p[..., 3] = np.where(changed[y0:y1, x0:x1], 255, 0)
    p[p[..., 3] == 0] = 0
    return Image.fromarray(p, "RGBA"), [int(x0), int(y0)]


# ---------------------------------------------------------------- labels

# the keycap symbols (ISO/IEC 9995-7) a key too small for its name shows instead, as strokes on a cap height of 1:
# lists of points, each list one stroke; "closed" strokes return to their start
SYMBOLS = {
    "ShiftLeft": [("closed", [(0.5, 0.0), (1.0, 0.52), (0.74, 0.52), (0.74, 1.0), (0.26, 1.0), (0.26, 0.52), (0.0, 0.52)])],
    "CapsLock": [("closed", [(0.5, 0.0), (1.0, 0.4), (0.74, 0.4), (0.74, 0.66), (0.26, 0.66), (0.26, 0.4), (0.0, 0.4)]),
                 ("open", [(0.26, 0.97), (0.74, 0.97)])],
    "Backspace": [("closed", [(0.0, 0.5), (0.38, 0.08), (1.35, 0.08), (1.35, 0.92), (0.38, 0.92)]),
                  ("open", [(0.66, 0.32), (1.04, 0.68)]), ("open", [(1.04, 0.32), (0.66, 0.68)])],
    "Enter": [("open", [(1.0, 0.0), (1.0, 0.66), (0.08, 0.66)]), ("open", [(0.36, 0.38), (0.08, 0.66), (0.36, 0.94)])],
    "Tab": [("open", [(0.0, 0.5), (0.95, 0.5)]), ("open", [(0.68, 0.24), (0.95, 0.5), (0.68, 0.76)]), ("open", [(1.12, 0.1), (1.12, 0.9)])],
    "ControlLeft": [("open", [(0.05, 0.62), (0.5, 0.12), (0.95, 0.62)])],
    "Home": [("open", [(0.0, 0.0), (1.0, 0.0)]), ("open", [(0.98, 1.0), (0.12, 0.14)]), ("open", [(0.12, 0.56), (0.12, 0.14), (0.54, 0.14)])],
    "End": [("open", [(0.0, 1.0), (1.0, 1.0)]), ("open", [(0.02, 0.0), (0.88, 0.86)]), ("open", [(0.88, 0.44), (0.88, 0.86), (0.46, 0.86)])],
    "PageUp": [("open", [(0.05, 0.5), (0.5, 0.05), (0.95, 0.5)]), ("open", [(0.05, 0.95), (0.5, 0.5), (0.95, 0.95)])],
    "PageDown": [("open", [(0.05, 0.05), (0.5, 0.5), (0.95, 0.05)]), ("open", [(0.05, 0.5), (0.5, 0.95), (0.95, 0.5)])],
    "Space": [("open", [(0.0, 0.55), (0.0, 0.95), (1.3, 0.95), (1.3, 0.55)])],
}
for _left, _right in (("ShiftLeft", "ShiftRight"), ("ControlLeft", "ControlRight")):
    SYMBOLS[_right] = SYMBOLS[_left]
STROKE = {"hand": 0.158, "handbold": 0.178, "plain": 0.095}  # the stroke of a symbol against its height, as the style's letters


def draw_symbol(style, name):
    """a keycap symbol drawn as round-ended strokes at CAP height, SUPER times bigger and scaled down"""
    k = CAP * SUPER
    w = STROKE[style] * k
    pad = w + 8
    pts_all = [p for _, pts in SYMBOLS[name] for p in pts]
    width = max(x for x, _ in pts_all) * k + 2 * pad
    img = Image.new("L", (int(width) + 1, int(k + 2 * pad) + 1), 0)
    d = ImageDraw.Draw(img)

    def at(p):
        return (pad + p[0] * k, pad + p[1] * k)

    for kind, pts in SYMBOLS[name]:
        xy = [at(p) for p in pts] + ([at(pts[0])] if kind == "closed" else [])
        d.line(xy, fill=255, width=int(round(w)), joint="curve")
        for p in xy:  # round ends and corners
            d.ellipse([p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2], fill=255)
    a = np.array(img)
    ys, xs = np.nonzero(a > 8)
    a = a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    return cv2.resize(a, (max(1, round(a.shape[1] / SUPER)), max(1, round(a.shape[0] / SUPER))), interpolation=cv2.INTER_AREA)


def font(style, size):
    url, axes = FONTS[style]
    path = os.path.join(tempfile.gettempdir(), "replaykit-bongo-keys-" + os.path.basename(url).replace("%5B", "[").replace("%5D", "]"))
    if not os.path.exists(path):
        urllib.request.urlretrieve(url, path)
    f = ImageFont.truetype(path, size)
    if axes:
        f.set_variation_by_axes(axes)
    return f


def font_size_for_cap(style):
    lo, hi = 20, 600
    while hi - lo > 1:
        mid = (lo + hi) // 2
        bb = font(style, mid).getbbox("H")
        lo, hi = (mid, hi) if bb[3] - bb[1] < CAP else (lo, mid)
    return lo


def draw_text(style, text, size, weight):
    """one or two lines of white text on transparent, cropped to the ink, drawn SUPER times bigger and scaled down"""
    big = draw_text_at(style, text, size * SUPER, int(round(weight * SUPER)))
    h, w = big.shape
    return cv2.resize(big, (max(1, round(w / SUPER)), max(1, round(h / SUPER))), interpolation=cv2.INTER_AREA)


def draw_line(style, f, line, weight, size):
    """one line of text cropped to its ink; a character with a traced glyph takes that glyph instead, as tall as the font's and in its place"""
    traced = TRACED.get(style, {})
    pad = 40
    bb = f.getbbox(line, stroke_width=weight)
    W, H = bb[2] - bb[0] + 2 * pad + size, bb[3] - bb[1] + 2 * pad + size
    img = Image.new("L", (W, H), 0)
    if not any(ch in traced for ch in line):
        ImageDraw.Draw(img).text((pad - bb[0], pad - bb[1]), line, font=f, fill=255, stroke_width=weight, stroke_fill=255)
    else:
        d = ImageDraw.Draw(img)
        x0, y0 = pad - bb[0], pad - bb[1]
        for j, ch in enumerate(line):
            x = x0 + f.getlength(line[:j])
            if ch not in traced:
                d.text((x, y0), ch, font=f, fill=255, stroke_width=weight, stroke_fill=255)
                continue
            cb = f.getbbox(ch, stroke_width=weight)
            g = traced[ch]
            k = (cb[3] - cb[1]) / g.shape[0]
            g = cv2.resize(g, (max(1, round(g.shape[1] * k)), max(1, round(g.shape[0] * k))), interpolation=cv2.INTER_CUBIC)
            cx, cy = x + (cb[0] + cb[2]) / 2, y0 + (cb[1] + cb[3]) / 2
            gx, gy = int(round(cx - g.shape[1] / 2)), int(round(cy - g.shape[0] / 2))
            a = np.array(img)
            region = a[gy:gy + g.shape[0], gx:gx + g.shape[1]]
            a[gy:gy + g.shape[0], gx:gx + g.shape[1]] = np.maximum(region, np.clip(g[:region.shape[0], :region.shape[1]] * 255, 0, 255).astype(np.uint8))
            img = Image.fromarray(a)
            d = ImageDraw.Draw(img)
    a = np.array(img)
    ys, xs = np.nonzero(a > 8)
    return a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def draw_text_at(style, text, size, weight):
    lines = text if isinstance(text, tuple) else (text,)
    if len(lines) == 1:
        return draw_line(style, font(style, size), lines[0], weight, size)
    # two lines: the small one over the big one, a fifth of the big one's size between their ink
    small = draw_line(style, font(style, int(size * 0.5)), lines[0], weight, size)
    big = draw_line(style, font(style, size), lines[1], weight, size)
    gap = int(size * 0.2)
    W = max(small.shape[1], big.shape[1])
    a = np.zeros((small.shape[0] + gap + big.shape[0], W), np.uint8)
    a[:small.shape[0], (W - small.shape[1]) // 2:(W - small.shape[1]) // 2 + small.shape[1]] = small
    a[small.shape[0] + gap:, (W - big.shape[1]) // 2:(W - big.shape[1]) // 2 + big.shape[1]] = big
    return a


# ---------------------------------------------------------------- tracing a label from the pictures

def stroke_width(a):
    """the width of a glyph's strokes, as twice the median distance from their middle line to their edge"""
    b = (a > 0.5).astype(np.uint8)
    dt = cv2.distanceTransform(b, cv2.DIST_L2, 5)
    ridge = (dt >= cv2.dilate(dt, np.ones((3, 3), np.uint8)) - 1e-3) & (dt > 1)
    return 2 * float(np.median(dt[ridge]))


def skeleton_paths(skel):
    """a one pixel wide skeleton as lines between its ends and junctions (and around its loops)"""
    pts = set(zip(*[v.tolist() for v in np.nonzero(skel)]))

    def near(p):
        y, x = p
        return [(y + dy, x + dx) for dy in (-1, 0, 1) for dx in (-1, 0, 1) if (dy or dx) and (y + dy, x + dx) in pts]

    deg = {p: len(near(p)) for p in pts}
    used, out = set(), []
    for p in sorted(pts, key=lambda p: (deg[p] != 1, p)):
        for q in near(p):
            if frozenset((p, q)) in used:
                continue
            line = [p, q]
            used.add(frozenset((p, q)))
            while deg[line[-1]] == 2:
                nxt = [r for r in near(line[-1]) if frozenset((line[-1], r)) not in used]
                if not nxt:
                    break
                used.add(frozenset((line[-1], nxt[0])))
                line.append(nxt[0])
            out.append(line)
    return out, deg


def trace(ref, width):
    """the original label's centre line, without the short side branches a blurred stroke grows, smoothed and stroked again with a
    round brush of the font's width"""
    skel = skeletonize(cv2.GaussianBlur(ref, (0, 0), 2.5) > 0.5)
    for _ in range(3):
        lines, deg = skeleton_paths(skel)
        spurs = [ln for ln in lines if (deg[ln[0]] == 1) != (deg[ln[-1]] == 1) and len(ln) < width * 1.2]
        if not spurs:
            break
        for ln in spurs:
            for p in (ln[:-1] if deg[ln[0]] == 1 else ln[1:]):
                skel[p] = False
    lines, _ = skeleton_paths(skel)
    S = 4
    canvas = np.zeros((ref.shape[0] * S, ref.shape[1] * S), np.uint8)
    r = int(round(width * S / 2))
    k = max(2, int(width / 3))
    for line in lines:
        if len(line) < 2:
            continue
        a = np.array(line, float)
        if len(a) >= 2 * k + 1:
            padded = np.concatenate([np.repeat(a[:1], k, 0), a, np.repeat(a[-1:], k, 0)])
            ker = np.ones(2 * k + 1) / (2 * k + 1)
            a = np.column_stack([np.convolve(padded[:, 0], ker, "valid"), np.convolve(padded[:, 1], ker, "valid")])
        pts = a[:, ::-1] * S + S / 2
        t = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(pts, axis=0), axis=1))])
        # the brush stamped every quarter of a pixel along the line: even strokes with round ends and joins
        for dist in np.arange(0, t[-1] + 1, S / 4):
            x, y = np.interp(dist, t, pts[:, 0]), np.interp(dist, t, pts[:, 1])
            cv2.circle(canvas, (int(round(x * 4)), int(round(y * 4))), r * 4, 255, -1, lineType=cv2.LINE_AA, shift=2)
    return cv2.resize(canvas.astype(np.float32) / 255, (ref.shape[1], ref.shape[0]), interpolation=cv2.INTER_AREA)


def de_skew(alpha, centre, ex, ey, size, scale, crisp=True):
    """the label of a slot turned upright: sprite pixel (u, v) samples the picture at centre + ((u - w/2) * ex + (v - h/2) * ey) / scale"""
    w, h = int(size[0] * scale), int(size[1] * scale)
    M = np.array([[ex[0] / scale, ey[0] / scale, 0], [ex[1] / scale, ey[1] / scale, 0]], float)
    M[:, 2] = np.array(centre) - M[:, :2] @ np.array([w / 2, h / 2])
    up = cv2.warpAffine(alpha.astype(np.float32), M, (w, h), flags=cv2.INTER_CUBIC | cv2.WARP_INVERSE_MAP, borderValue=0)
    if not crisp:
        return np.clip(up, 0, 1)
    # crisp edges at the bigger size: a smooth step around the half-covered line instead of the blur of the upscale
    up = np.clip((up - 0.3) / 0.4, 0, 1)
    up = up * up * (3 - 2 * up)
    return up


def crop_ink(a, pad=2):
    ys, xs = np.nonzero(a > 0.03)
    return a[max(ys.min() - pad, 0):ys.max() + pad + 1, max(xs.min() - pad, 0):xs.max() + pad + 1]


def save_sprite(name, a):
    a8 = np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8) if a.dtype != np.uint8 else a
    img = Image.merge("LA", (Image.new("L", (a8.shape[1], a8.shape[0]), 255), Image.fromarray(a8)))
    img.save(os.path.join(OUT, name), optimize=True)
    return {"file": name, "w": int(a8.shape[1]), "h": int(a8.shape[0])}


def md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def ink_color(img, blank, mask):
    """the solid colour of a label: of the pixels the blank face changed, the quarter whose brightness is furthest from the face's
    (the anti-aliased edge of a blue letter on cyan differs as much in hue, but not in brightness)"""
    diff = np.abs(img[..., :3].astype(int) - blank[..., :3].astype(int)).sum(axis=2) * mask
    text = diff > 60
    if not text.any():
        return None
    cols = img[text][:, :3].astype(float)
    away = np.abs(luma(cols) - luma(blank[text][:, :3].astype(float)))
    return np.median(cols[away >= np.percentile(away, 75)], axis=0)


def hexcolor(c):
    c = [int(round(v)) for v in c]
    return "#%02x%02x%02x" % tuple(c[:3])


# ---------------------------------------------------------------- fitting a key's own label

FIT_SCALE = 8.0  # the key is looked at this many times bigger while fitting


def place(sprite, p, canvas):
    """the sprite on the key's upright canvas: sprite pixel q lands at key pixel [[sx, k], [0, sy]] @ (q - middle) + (tx, ty)"""
    sx, sy, k, tx, ty = p
    h, w = sprite.shape
    A = FIT_SCALE * np.array([[sx, k], [0, sy]])
    off = FIT_SCALE * np.array([tx, ty]) + np.array(canvas[::-1]) / 2 - A @ np.array([w / 2, h / 2])
    return cv2.warpAffine(sprite, np.hstack([A, off[:, None]]).astype(np.float32), (canvas[1], canvas[0]), flags=cv2.INTER_LINEAR)


def overlap(a, b):
    return float(np.minimum(a, b).sum() / max(1e-6, np.maximum(a, b).sum()))


def fit_label(ink, sprite):
    """how the artist drew the key's label, as the scale across and down, lean and place that lay the font's same label over it best:
    starting from the height and middle of the ink, each is nudged while the overlap grows, in smaller and smaller steps"""
    ys, xs = np.nonzero(ink > 0.5)
    s0 = (ys.max() - ys.min() + 1) / FIT_SCALE / sprite.shape[0]
    p = [s0, s0, 0.0, ((xs.min() + xs.max()) / 2 - ink.shape[1] / 2) / FIT_SCALE, ((ys.min() + ys.max()) / 2 - ink.shape[0] / 2) / FIT_SCALE]
    best = overlap(ink, place(sprite, p, ink.shape))
    steps = [0.08 * s0, 0.08 * s0, 0.08 * s0, 1.0, 1.0]
    for _ in range(7):
        moved = True
        while moved:
            moved = False
            for i in range(5):
                for d in (-steps[i], steps[i]):
                    q = list(p)
                    q[i] += d
                    o = overlap(ink, place(sprite, q, ink.shape))
                    if o > best + 1e-5:
                        p, best, moved = q, o, True
        steps = [x / 2 for x in steps]
    return p, best


# ---------------------------------------------------------------- main

def main(root):
    mode_root = os.path.join(root, "mode")
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        if f.endswith((".png", ".json")):
            os.remove(os.path.join(OUT, f))
    texts = label_texts()
    sizes = {style: font_size_for_cap(style) for style in FONTS}

    def sprite_of(style, text):
        return draw_text(style, text, sizes[style], WEIGHT[style]).astype(np.float32) / 255.0

    manifest = {"version": 2, "styles": {}, "modes": {}}
    arrows = {}  # code -> the keyboard mode's own arrow, cut from its picture: the arrows are drawings, not letters of a font

    for mode, info in MODES.items():
        folder = os.path.join(mode_root, mode)
        style = info["style"]
        config = json.load(open(os.path.join(folder, "config.json"), encoding="utf-8"))
        backs = {b: np.array(Image.open(os.path.join(folder, b)).convert("RGBA")) for b in info["backs"]}
        first = backs[info["backs"][0]]
        flat = style != "plain"  # the Comic Sans keyboards have flat faces; feixue's are shaded and get in-painted
        slots = []
        for i, (file, use) in enumerate(zip(config["KeysImageName"], config["KeyUse"])):
            path = os.path.join(folder, config["KeysImagePath"], file)
            pressed = np.array(Image.open(path).convert("RGBA"))
            mask = key_mask(pressed)
            slots.append({"i": i, "use": use, "path": path, "pressed": pressed, "mask": mask, "axes": edge_axes(mask, info["flipped"])})
        # a key whose sides do not give its slant (the round R, a key fitted crooked) takes the slant of the mode's other keys
        exm = unit(np.median([s["axes"][0] for s in slots if s["axes"]], axis=0))
        eym = unit(np.median([s["axes"][1] for s in slots if s["axes"]], axis=0))
        for s in slots:
            ex, ey = s["axes"] if s["axes"] and angle(s["axes"][0], exm) < 8 and angle(s["axes"][1], eym) < 18 else (exm, eym)
            centre, size = local_box(s["mask"], ex, ey)
            s.update(ex=ex, ey=ey, centre=centre, size=size)
            alpha = np.where(cv2.dilate(s["mask"].astype(np.uint8), np.ones((3, 3), np.uint8)) > 0, s["pressed"][..., 3] / 255.0, 0)
            # the blank faces, and the patches that turn a key blank
            rest_blank, _ = blank_back(first, s["mask"], alpha, flat)
            s["rest_patch"], s["rest_at"] = patch(first, rest_blank)
            for b, img in backs.items():
                if b != info["backs"][0]:
                    other, _ = blank_back(img, s["mask"], alpha, flat)
                    p2, at2 = patch(img, other)
                    same = (p2 is None) == (s["rest_patch"] is None) and (p2 is None or np.array_equal(np.array(p2), np.array(s["rest_patch"])))
                    assert at2 == s["rest_at"] and same, (mode, s["i"], b)
            pressed_blank, pressed_face = blank_pressed(s["pressed"], s["mask"], flat)
            s["pressed_patch"], s["pressed_at"] = patch(s["pressed"], pressed_blank)
            # the label's ink: what the blank face took out of the pressed key, as coverage
            removed = np.abs(s["pressed"][..., :3].astype(float) - pressed_blank[..., :3].astype(float)).max(axis=2)
            full = np.abs(255.0 - pressed_face).max() if flat else 140.0  # how far a solid white (or dark) stroke sits from the face
            s["ink"] = np.clip(removed / max(1.0, full), 0, 1) * s["mask"]
            # the colours a label is drawn in: the solid middle of the original label's strokes
            if flat:  # the Comic Sans keyboards draw in black on the keys and white on the pressed ones
                s["rest_color"], s["pressed_color"] = (0, 0, 0), (255, 255, 255)
            else:
                s["rest_color"] = ink_color(first, rest_blank, s["mask"])
                s["pressed_color"] = ink_color(s["pressed"], pressed_blank, s["mask"] & (s["pressed"][..., 3] == 255))
            # the room inside the key's outline, with a little more at the ends so a long word does not touch it (feixue's keys are bevelled inside)
            edge = border(first, s["mask"], (lambda p: luma(p.astype(float)) < 110) if flat else (lambda p: luma(p.astype(float)) < 200))
            margin = edge + 0.5
            side = min(SIDE_ROOM[style], 0.05 * size[0])
            s["box"] = (max(4.0, size[0] - 2 * (margin + side)), max(4.0, size[1] - 2 * margin))
            s["label"] = SLOT_LABELS[mode][s["i"]]
            s["blank"] = (mode, s["i"]) in BLANK_SLOTS
            # the label upright, on the key's own canvas
            s["canvas"] = (s["box"][0] + 2 * margin + 8, s["box"][1] + 2 * margin + 8)
            s["upright"] = de_skew(s["ink"], centre, ex, ey, s["canvas"], FIT_SCALE, crisp=False)
            if s["label"] is None and not s["blank"]:
                s["label"] = arrow_direction(crop_ink(de_skew(s["ink"], centre, ex, ey, s["canvas"], FIT_SCALE)))
        # how the artist drew each key's own label, from the font's same label laid over the pressed key's (the cleaner of the two)
        # (the fit is made against the font's glyph, before any traced glyph replaces it)
        for s in slots:
            s["fit"] = None
            if s["blank"] or s["label"].startswith("Arrow") or (style == "plain" and s["label"] == "Space"):
                continue  # no label, a drawing, or feixue's Life box, whose words are not the key's name
            p, score = fit_label(s["upright"], sprite_of(style, texts[style][s["label"]][0]))
            if score < 0.45:  # not the font's label after all: placed as the mode's other keys are
                print("warning: %s key %d fits its label poorly (%.2f), placed as the other keys" % (mode, s["i"], score))
                continue
            s["fit"], s["score"] = p, score
        fitted = [s for s in slots if s["fit"]]
        # the labels traced from the pictures: the key's label on the background and on the pressed key, carried into the font's
        # pixels through the key's fit, averaged and traced
        for s in fitted:
            if s["label"] not in TRACE.get(style, []):
                continue
            text = texts[style][s["label"]][0]
            ref_sprite = sprite_of(style, text)
            h, w = ref_sprite.shape
            sx, sy, k, tx, ty = s["fit"]
            E = np.column_stack([s["ex"], s["ey"]])
            M = E @ np.array([[sx, k], [0, sy]])
            off = s["centre"] + E @ np.array([tx, ty]) - M @ np.array([w / 2, h / 2])
            dm = cv2.distanceTransform(s["mask"].astype(np.uint8), cv2.DIST_L2, 5)
            edge = border(first, s["mask"], lambda p: luma(p.astype(float)) < 110)
            rest_ink = (1 - luma(first[..., :3].astype(float)) / 255.0) * (dm > edge + 1.2)
            back = [np.clip(cv2.warpAffine(ink.astype(np.float32), np.hstack([M, off[:, None]]).astype(np.float32), (w, h),
                                           flags=cv2.INTER_CUBIC | cv2.WARP_INVERSE_MAP), 0, 1) for ink in (s["ink"], rest_ink)]
            traced = trace((back[0] + back[1]) / 2, stroke_width(ref_sprite))
            ys, xs = np.nonzero(traced > 0.03)
            TRACED.setdefault(style, {})[text] = traced[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
        # a key without a label of its own draws one as the mode's fitted keys do, as tall against its room as they draw theirs
        tall = np.median([s["fit"][1] * CAP / s["box"][1] for s in fitted])
        wide = np.median([s["fit"][0] / s["fit"][1] for s in fitted])
        lean = np.median([s["fit"][2] / s["fit"][1] for s in fitted])
        out_slots = []
        for s in slots:
            if s["fit"]:
                sx, sy, k, tx, ty = s["fit"]
            else:
                ink_rows = np.nonzero(s["upright"].max(axis=1) > 0.5)[0]
                own = (ink_rows.max() - ink_rows.min() + 1) / FIT_SCALE / s["box"][1] if (len(ink_rows) and s["label"] == "Space" and not s["blank"]) else None
                sy = (own or tall) * s["box"][1] / CAP  # feixue's Life box: as tall as its own words
                sx, k, tx, ty = sy * wide, sy * lean, 0.0, 0.0
            E = np.column_stack([s["ex"], s["ey"]])
            M = E @ np.array([[sx, k], [0, sy]])
            anchor = s["centre"] + E @ np.array([tx, ty])
            # the room, in pixels of a label at its natural size: the key's, or the original label's where that reaches further
            room_w, room_h = s["box"]
            if s["fit"]:
                ink_cols = np.nonzero(s["upright"].max(axis=0) > 0.5)[0]
                ink_rows = np.nonzero(s["upright"].max(axis=1) > 0.5)[0]
                room_w = max(room_w, (ink_cols.max() - ink_cols.min() + 1) / FIT_SCALE)
                room_h = max(room_h, (ink_rows.max() - ink_rows.min() + 1) / FIT_SCALE)
            out_slots.append({
                "key": s["use"], "label": s["label"], "blank": s["blank"],
                # sprite pixel (u, v) of a label at its natural size lands at anchor + matrix @ (u - w/2, v - h/2): x = m11 u + m21 v, y = m12 u + m22 v
                "matrix": [round(float(M[0, 0]), 6), round(float(M[1, 0]), 6), round(float(M[0, 1]), 6), round(float(M[1, 1]), 6)],
                "anchor": [round(float(anchor[0]), 3), round(float(anchor[1]), 3)],
                "room": [round(float(room_w / sx), 1), round(float(room_h / sy), 1)],
                "fit": round(s["score"], 3) if s["fit"] else None,
                "rest": {"patch": None, "at": s["rest_at"], "color": hexcolor(s["rest_color"])},
                "pressed": {"patch": None, "at": s["pressed_at"], "color": hexcolor(s["pressed_color"]), "md5": md5(s["path"])},
            })
            if s["rest_patch"]:
                out_slots[-1]["rest"]["patch"] = "face-%s-rest-%d.png" % (mode, s["i"])
                s["rest_patch"].save(os.path.join(OUT, out_slots[-1]["rest"]["patch"]), optimize=True)
            if s["pressed_patch"]:
                out_slots[-1]["pressed"]["patch"] = "face-%s-pressed-%d.png" % (mode, s["i"])
                s["pressed_patch"].save(os.path.join(OUT, out_slots[-1]["pressed"]["patch"]), optimize=True)
            if s["label"].startswith("Arrow") and mode == "keyboard":
                arrows[s["label"]] = crop_ink(de_skew(s["ink"], s["centre"], s["ex"], s["ey"], s["canvas"], CAP / 20.0))
        manifest["modes"][mode] = {
            "style": style,
            "backgrounds": {b: md5(os.path.join(folder, b)) for b in info["backs"]},
            "slots": out_slots,
        }

    # every key in every style
    for style in FONTS:
        entries = {}
        for code, (long_text, short_text) in texts[style].items():
            entry = {}
            if code in arrows and style != "plain":
                # the keyboard mode's arrow, as tall as a letter
                a = arrows[code]
                k = CAP / float(a.shape[0])
                a = np.clip(cv2.resize(a, (max(1, int(a.shape[1] * k)), CAP), interpolation=cv2.INTER_CUBIC), 0, 1)
                entry.update(save_sprite("%s-%s.png" % (style, code.lower()), a))
                entry["source"] = "keyboard"
            else:
                entry.update(save_sprite("%s-%s.png" % (style, code.lower()), draw_text(style, long_text, sizes[style], WEIGHT[style])))
            if isinstance(long_text, tuple):
                entry["smallCap"] = CAP * 0.5  # the cap height of the small first line
            if short_text:
                entry["short"] = save_sprite("%s-%s-short.png" % (style, code.lower()), draw_text(style, short_text, sizes[style], WEIGHT[style]))
            if code in SYMBOLS:
                entry["tiny"] = save_sprite("%s-%s-tiny.png" % (style, code.lower()), draw_symbol(style, code))
            entries[code] = entry
        manifest["styles"][style] = entries

    with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")
    for mode, m in manifest["modes"].items():
        print(mode, " ".join("%s:%s" % (s["key"], s["fit"]) for s in m["slots"]))
    print("wrote", OUT, "with", len(os.listdir(OUT)), "files")


def arrow_direction(a):
    """which way an upright arrow points: a fat arrow's head holds more ink than its tail, so its ink's centre sits toward the head"""
    ys, xs = np.nonzero(a > 0.5)
    h, w = a.shape
    cx, cy = xs.mean() / w - 0.5, ys.mean() / h - 0.5
    if abs(cx) > abs(cy):
        return "ArrowRight" if cx > 0 else "ArrowLeft"
    return "ArrowDown" if cy > 0 else "ArrowUp"


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
