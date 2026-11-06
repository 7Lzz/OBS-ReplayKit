#!/usr/bin/env python3
"""Draws a Bango Cat mode with other keys on its keys, the way replaykit.dll and the overlay preview do, from manifest.json.

    python render.py "C:\\Program Files\\obs-studio\\bin\\64bit\\Bango Cat" standard F1,,KeyB out.png

The keys are KeyboardEvent codes in the order of the mode's keys, as the bongoKeys setting stores them; an empty one keeps the key.
The picture is the mode's background with every key held drawn on top, turned so the labels read upright. This file is also the
reference for the two runtimes: relabel() is the whole algorithm.
"""
import json
import os
import sys

import cv2
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
KEYS = os.path.normpath(os.path.join(HERE, "..", "..", "ui", "overlay-preview", "bongo-keys"))
SHORT_BELOW = 0.55  # a long label squeezed under this share of its natural size takes its short form
MIN_SMALL_PX = 8.0  # nor is a small line drawn shorter than this many pixels of the picture: the label takes its short form


def load(name):
    return np.array(Image.open(os.path.join(KEYS, name)).convert("RGBA")).astype(np.float32) / 255.0


def over(dst, src, x, y):
    """src drawn over dst (straight alpha, both 0..1) with its top left at x, y"""
    h, w = src.shape[:2]
    d = dst[y:y + h, x:x + w]
    a = src[..., 3:4]
    out_a = a + d[..., 3:4] * (1 - a)
    rgb = (src[..., :3] * a + d[..., :3] * d[..., 3:4] * (1 - a)) / np.maximum(out_a, 1e-6)
    dst[y:y + h, x:x + w] = np.concatenate([rgb, out_a], axis=2)


def choose(entry, slot):
    """which form of the label (None: the long one, "short", "tiny") and how much it is shrunk from its natural size (1: as the key's
    own label was drawn) to fit the key's room"""
    def fit(e):
        return min(1.0, slot["room"][0] / e["w"], slot["room"][1] / e["h"])

    m11, m12, m21, m22 = slot["matrix"]
    down = np.hypot(m21, m22)  # pixels of the picture per sprite pixel, down the label
    f = fit(entry)
    if "short" in entry and (f < SHORT_BELOW or entry.get("smallCap", 1e9) * f * down < MIN_SMALL_PX):
        g = fit(entry["short"])
        if "tiny" in entry and g < SHORT_BELOW:  # still squeezed: the keycap symbol
            return "tiny", fit(entry["tiny"])
        return "short", g
    if "tiny" in entry and f < SHORT_BELOW:
        return "tiny", fit(entry["tiny"])
    return None, f


def form(entry, which):
    return entry[which] if which else entry


def draw_label(dst, sprite, colour, slot, f):
    """the white sprite tinted to colour: sprite pixel (u, v) lands at anchor + f * matrix @ (u - w/2, v - h/2)"""
    m11, m12, m21, m22 = [v * f for v in slot["matrix"]]
    # a label drawn much smaller than its sprite is shrunk smoothly first to about twice its size, or the one-step resample leaves it jagged
    k = min(1.0, 2.0 * np.sqrt(abs(m11 * m22 - m12 * m21)))
    if k < 1.0:
        h0, w0 = sprite.shape[:2]
        sprite = cv2.resize(sprite, (max(1, round(w0 * k)), max(1, round(h0 * k))), interpolation=cv2.INTER_AREA)
        kx, ky = sprite.shape[1] / w0, sprite.shape[0] / h0  # a pixel of the smaller sprite covers 1/kx, 1/ky of the old
        m11, m12, m21, m22 = m11 / kx, m12 / kx, m21 / ky, m22 / ky
    h, w = sprite.shape[:2]
    tint = sprite.copy()
    tint[..., :3] = np.array([int(colour[i:i + 2], 16) for i in (1, 3, 5)], np.float32) / 255.0
    M = np.array([[m11, m21, 0], [m12, m22, 0]])
    M[:, 2] = np.array(slot["anchor"]) - M[:, :2] @ np.array([w / 2, h / 2])
    # premultiplied for the resampling, so the edges do not pick up the colour of the transparent pixels
    pm = tint.copy()
    pm[..., :3] *= pm[..., 3:4]
    warped = cv2.warpAffine(pm, M, (dst.shape[1], dst.shape[0]), flags=cv2.INTER_LINEAR, borderValue=0)
    a = warped[..., 3:4]
    dst[..., :3] = warped[..., :3] + dst[..., :3] * (1 - a)
    dst[..., 3:4] = a + dst[..., 3:4] * (1 - a)


def relabel(manifest, mode, codes, backgrounds, pressed, md5s=None, force=False):
    """backgrounds: {file: rgba 0..1}, pressed: [rgba 0..1 per key]; changed in place. A key keeps its picture when its code is empty,
    when it is the key's own label, or when its pressed picture is not the one the faces were cut from (md5s: {file or key index: md5});
    a background that is not the one they were cut from is left as it is."""
    m = manifest["modes"][mode]
    style = manifest["styles"][m["style"]]
    md5s = md5s or {}
    backgrounds = {b: img for b, img in backgrounds.items() if md5s.get(b, m["backgrounds"].get(b)) == m["backgrounds"].get(b)}
    changed = []
    for i, slot in enumerate(m["slots"]):
        code = codes[i] if i < len(codes) else ""
        if not code or (code == slot["label"] and not force) or code not in style or md5s.get(i, slot["pressed"]["md5"]) != slot["pressed"]["md5"]:
            continue
        changed.append((i, slot, code))
    # every blank face first, then the labels, so no face is laid over a label already drawn
    for i, slot, code in changed:
        for img in backgrounds.values():
            if slot["rest"]["patch"]:
                over(img, load(slot["rest"]["patch"]), *slot["rest"]["at"])
        if slot["pressed"]["patch"]:
            over(pressed[i], load(slot["pressed"]["patch"]), *slot["pressed"]["at"])
    for i, slot, code in changed:
        which, f = choose(style[code], slot)
        sprite = load(form(style[code], which)["file"])
        for img in backgrounds.values():
            draw_label(img, sprite, slot["rest"]["color"], slot, f)
        draw_label(pressed[i], sprite, slot["pressed"]["color"], slot, f)


def md5(path):
    import hashlib
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def main(root, mode, codes, out):
    manifest = json.load(open(os.path.join(KEYS, "manifest.json"), encoding="utf-8"))
    folder = os.path.join(root, "mode", mode)
    config = json.load(open(os.path.join(folder, "config.json"), encoding="utf-8"))
    backs = list(manifest["modes"][mode]["backgrounds"])
    backgrounds = {b: np.array(Image.open(os.path.join(folder, b)).convert("RGBA")).astype(np.float32) / 255.0 for b in backs[:1]}
    pressed = [np.array(Image.open(os.path.join(folder, config["KeysImagePath"], f)).convert("RGBA")).astype(np.float32) / 255.0 for f in config["KeysImageName"]]
    md5s = {b: md5(os.path.join(folder, b)) for b in backgrounds}
    md5s.update({i: md5(os.path.join(folder, config["KeysImagePath"], f)) for i, f in enumerate(config["KeysImageName"])})
    relabel(manifest, mode, codes, backgrounds, pressed, md5s)
    frame = backgrounds[backs[0]].copy()
    rest = frame.copy()
    for p in pressed:
        over(frame, p, 0, 0)
    pics = [rest, frame]
    if manifest["modes"][mode]["slots"] and mode != "feixue":
        pics = [np.rot90(p, 2) for p in pics]
    img = np.concatenate(pics, axis=0)
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA").save(out)


if __name__ == "__main__":
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3].split(","), sys.argv[4])
