"""make the obs swirl mask the share card ground turns: the signed distance to the swirl of resources/icons/share/obs-logo-outline.svg, its view box drawn at 1024 x 1024, edge at half grey and the data-mask-range of ui/_share-motion.html from black to white."""

import argparse
import math
import re
import xml.etree.ElementTree as ElementTree
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
ICONS = HERE.parent.parent / "resources" / "icons" / "share"
MOTION = HERE.parent.parent / "ui" / "_share-motion.html"
SIZE = 1024
# each curve piece becomes this many straight steps, far below a pixel at 1024
CURVE_STEPS = 48

NUMBER = re.compile(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
TOKEN = re.compile(r"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
ARITY = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


def tokens(path):
    for token in TOKEN.findall(path):
        yield token if token.isalpha() else float(token)


def arc_flags(path):
    # the arc flags may be written run together (0 01 or 011), so the arc arguments are read character by character where a flag is due
    out, at, command, count = [], 0, None, 0
    while at < len(path):
        char = path[at]
        if char.isalpha():
            out.append(char)
            command, count = char.upper(), 0
            at += 1
            continue
        if char in " ,\t\n\r":
            at += 1
            continue
        if command == "A" and count % 7 in (3, 4) and char in "01":
            out.append(float(char))
            count += 1
            at += 1
            continue
        match = NUMBER.match(path, at)
        if not match:
            raise ValueError("bad path data near " + path[at:at + 20])
        out.append(float(match.group()))
        count += 1
        at = match.end()
    return out


def cubic(p0, p1, p2, p3):
    for step in range(1, CURVE_STEPS + 1):
        t = step / CURVE_STEPS
        u = 1 - t
        yield (u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0],
               u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1])


def arc(p0, rx, ry, rotation, large, sweep, p1):
    # svg endpoint arc to centre form, as the svg spec appendix on implementation notes gives it
    if p0 == p1:
        return
    if rx == 0 or ry == 0:
        yield p1
        return
    rx, ry = abs(rx), abs(ry)
    phi = math.radians(rotation)
    cos, sin = math.cos(phi), math.sin(phi)
    dx, dy = (p0[0] - p1[0]) / 2, (p0[1] - p1[1]) / 2
    x1, y1 = cos * dx + sin * dy, -sin * dx + cos * dy
    scale = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry)
    if scale > 1:
        rx, ry = rx * math.sqrt(scale), ry * math.sqrt(scale)
    numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1
    factor = math.sqrt(max(0.0, numerator / (rx * rx * y1 * y1 + ry * ry * x1 * x1)))
    if large == sweep:
        factor = -factor
    cx1, cy1 = factor * rx * y1 / ry, -factor * ry * x1 / rx
    cx = cos * cx1 - sin * cy1 + (p0[0] + p1[0]) / 2
    cy = sin * cx1 + cos * cy1 + (p0[1] + p1[1]) / 2
    start = math.atan2((y1 - cy1) / ry, (x1 - cx1) / rx)
    delta = math.atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx) - start
    if sweep and delta < 0:
        delta += 2 * math.pi
    elif not sweep and delta > 0:
        delta -= 2 * math.pi
    for step in range(1, CURVE_STEPS + 1):
        angle = start + delta * step / CURVE_STEPS
        x, y = rx * math.cos(angle), ry * math.sin(angle)
        yield (cos * x - sin * y + cx, sin * x + cos * y + cy)


def outlines(path):
    """the closed outlines of an svg path as lists of points."""
    items = arc_flags(path)
    shapes, points = [], []
    at, current, start, last_control, command = 0, (0.0, 0.0), (0.0, 0.0), None, None
    while at < len(items):
        if isinstance(items[at], str):
            command = items[at]
            at += 1
            if command.upper() == "Z":
                if points:
                    shapes.append(points)
                points, current, last_control = [], start, None
                continue
        upper, relative = command.upper(), command.islower()
        args = items[at:at + ARITY[upper]]
        at += ARITY[upper]
        base = current if relative else (0.0, 0.0)
        if upper == "M":
            if points:
                shapes.append(points)
            current = (base[0] + args[0], base[1] + args[1])
            start, points, last_control = current, [current], None
            command = "l" if relative else "L"
        elif upper == "L":
            current = (base[0] + args[0], base[1] + args[1])
            points.append(current)
            last_control = None
        elif upper == "H":
            current = ((current[0] if relative else 0.0) + args[0], current[1])
            points.append(current)
            last_control = None
        elif upper == "V":
            current = (current[0], (current[1] if relative else 0.0) + args[0])
            points.append(current)
            last_control = None
        elif upper in ("C", "S"):
            if upper == "C":
                c1 = (base[0] + args[0], base[1] + args[1])
                rest = args[2:]
            else:
                c1 = (2 * current[0] - last_control[0], 2 * current[1] - last_control[1]) if last_control else current
                rest = args
            c2 = (base[0] + rest[0], base[1] + rest[1])
            end = (base[0] + rest[2], base[1] + rest[3])
            points.extend(cubic(current, c1, c2, end))
            current, last_control = end, c2
        elif upper in ("Q", "T"):
            if upper == "Q":
                control = (base[0] + args[0], base[1] + args[1])
                end = (base[0] + args[2], base[1] + args[3])
            else:
                control = (2 * current[0] - last_control[0], 2 * current[1] - last_control[1]) if last_control else current
                end = (base[0] + args[0], base[1] + args[1])
            c1 = (current[0] + 2 / 3 * (control[0] - current[0]), current[1] + 2 / 3 * (control[1] - current[1]))
            c2 = (end[0] + 2 / 3 * (control[0] - end[0]), end[1] + 2 / 3 * (control[1] - end[1]))
            points.extend(cubic(current, c1, c2, end))
            current, last_control = end, control
        elif upper == "A":
            end = (base[0] + args[5], base[1] + args[6])
            points.extend(arc(current, args[0], args[1], args[2], bool(args[3]), bool(args[4]), end))
            current, last_control = end, None
    if points:
        shapes.append(points)
    return shapes


def signed_distance(shapes, size):
    """distance in pixels from each pixel centre to the outline, positive inside by the non-zero rule svg fills with."""
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float64) + 0.5
    nearest = np.full((size, size), np.inf)
    winding = np.zeros((size, size), dtype=np.int32)
    for points in shapes:
        ring = np.asarray(points + [points[0]], dtype=np.float64)
        for (ax, ay), (bx, by) in zip(ring[:-1], ring[1:]):
            ex, ey = bx - ax, by - ay
            length = ex * ex + ey * ey
            if length == 0:
                continue
            t = np.clip(((xs - ax) * ex + (ys - ay) * ey) / length, 0.0, 1.0)
            nearest = np.minimum(nearest, np.hypot(xs - (ax + t * ex), ys - (ay + t * ey)))
            if ay == by:
                continue
            upward = ay <= by
            low, high = (ay, by) if upward else (by, ay)
            rows = (ys >= low) & (ys < high)
            crossing = ax + (ys - ay) * ex / ey
            winding += np.where(rows & (crossing > xs), 1 if upward else -1, 0).astype(np.int32)
    return np.where(winding != 0, nearest, -nearest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ICONS / "obs-swirl-mask.png")
    args = parser.parse_args()
    svg = ElementTree.parse(ICONS / "obs-logo-outline.svg").getroot()
    names = {"svg": "http://www.w3.org/2000/svg"}
    view = [float(v) for v in svg.get("viewBox").split()]
    swirl = svg.find("svg:path", names)
    move = re.fullmatch(r"\s*translate\(\s*([-\d.]+)[ ,]+([-\d.]+)\s*\)\s*", swirl.get("transform", "translate(0 0)"))
    shift = (float(move.group(1)), float(move.group(2)))
    scale = SIZE / view[2]
    shapes = [[((x + shift[0] - view[0]) * scale, (y + shift[1] - view[1]) * scale) for x, y in points] for points in outlines(swirl.get("d"))]
    # the shader reads (value - 0.5) * range as the distance in mask units, so the mask is made with the range it reads
    match = re.search(r'data-mask-range="(0\.\d+)"', MOTION.read_text(encoding="utf-8"))
    if not match:
        raise SystemExit(f"{MOTION} names no data-mask-range")
    distance = signed_distance(shapes, SIZE)
    value = np.clip(0.5 + distance / (float(match.group(1)) * SIZE), 0.0, 1.0)
    Image.fromarray(np.round(value * 255).astype(np.uint8), mode="L").save(args.out, optimize=True)
    print("wrote", args.out)


if __name__ == "__main__":
    main()
