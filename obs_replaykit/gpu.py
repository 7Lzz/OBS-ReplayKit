"""detect the users gpu(s) and classify them for encoder selection. pulls win32_videocontroller via cim/wmi and parses the adapter name to tag the architecture tier (registry/wmi doesnt expose the arch as a structured field, but marketing names follow stable patterns). prefers discrete over integrated when both are present."""

from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass
from enum import Enum
from typing import List, Optional


class Vendor(str, Enum):
    NVIDIA  = "nvidia"
    AMD     = "amd"
    INTEL   = "intel"
    UNKNOWN = "unknown"


class NvidiaGen(str, Enum):
    """coarse nvenc capability tiers."""
    PRE_MAXWELL2 = "pre_maxwell2"  # kepler and older -- no hevc encode
    MAXWELL2     = "maxwell2"  # gtx 9xx -- hevc, no b-frames, qres only
    PASCAL       = "pascal"  # gtx 10xx -- hevc + lookahead, no hevc b-frames
    TURING       = "turing"  # gtx 16xx / rtx 20xx -- hevc b-frames
    AMPERE       = "ampere"  # rtx 30xx
    ADA          = "ada"  # rtx 40xx
    BLACKWELL    = "blackwell"  # rtx 50xx -- av1 nvenc, nvenc 10th gen


class AmdGen(str, Enum):
    """coarse amf capability tiers."""
    PRE_POLARIS = "pre_polaris"  # no hardware hevc
    POLARIS     = "polaris"  # rx 4/5xx -- first amd hevc (basic)
    VEGA        = "vega"  # vega / radeon vii
    RDNA1       = "rdna1"  # rx 5xxx
    RDNA2       = "rdna2"  # rx 6xxx
    RDNA3       = "rdna3"  # rx 7xxx -- av1 amf
    RDNA4       = "rdna4"  # rx 9xxx (future-proof)


class IntelGen(str, Enum):
    """coarse quick sync capability tiers."""
    PRE_SKYLAKE = "pre_skylake"  # no qsv hevc
    SKYLAKE     = "skylake"  # hd 530 -- first qsv hevc, 8-bit
    KABY_LAKE   = "kaby_lake"  # hd 630 -- + 10-bit hevc
    ICE_LAKE    = "ice_lake"  # big qsv quality jump
    TIGER_LAKE  = "tiger_lake"  # xe-lp igpu
    ALDER_LAKE  = "alder_lake"  # 12th/13th/14th gen igpu
    ARC         = "arc"  # discrete arc + xe-hpg (av1 qsv)


@dataclass(frozen=True)
class Gpu:
    """one physical adapter detected on the system."""
    name:         str  # win32_videocontroller.name
    vendor:       Vendor
    generation:   Optional[str]  # nvidiagen / amdgen / intelgen value (string)
    is_discrete:  bool  # heuristic: discrete dedicated card vs igpu
    vram_bytes:   int  # adapterram (0 if unknown)
    driver:       str  # driverversion

    @property
    def label(self) -> str:
        return self.name

    @property
    def gen_label(self) -> str:
        if self.generation is None:
            return "unknown"
        return self.generation.replace("_", " ").title()


def _query_video_controllers() -> List[dict]:
    """win32_videocontroller rows as plain dicts. powershell get-ciminstance becuase wmic was deprecated in win10 21h1 and isnt installed on win11. returns [] on any failure."""
    cmd = [
        "powershell.exe",
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        # @() forces array semantics even with one controller (otherwise convertto-json serialises a single object as a bare dict). -asarray would also work but its powershell 7+; we still run on stock powershell 5.1.
        "ConvertTo-Json -Depth 3 -Compress -InputObject @("
        "Get-CimInstance Win32_VideoController | "
        "Select-Object Name, AdapterRAM, DriverVersion)",
    ]
    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True,
            timeout=10, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.TimeoutExpired):
        return []
    if result.returncode != 0 or not result.stdout.strip():
        return []
    try:
        import json
        data = json.loads(result.stdout)
    except (json.JSONDecodeError, ValueError):
        return []
    if not isinstance(data, list):
        return [data] if isinstance(data, dict) else []
    return data


_VENDOR_PATTERNS = (
    (re.compile(r"\bnvidia\b|\bgeforce\b|\bquadro\b|\brtx\b|\bgtx\b", re.IGNORECASE), Vendor.NVIDIA),
    (re.compile(r"\bamd\b|\bradeon\b|\brx\s*\d", re.IGNORECASE),                      Vendor.AMD),
    (re.compile(r"\bintel\b|\biris\b|\barc\b|\buhd\b|\bhd\s*graphics\b", re.IGNORECASE), Vendor.INTEL),
)

# basic-display-adapter / indirect-display / remote-display drivers we want to ignore (no real encoder support, never the right answer for obs).
_IGNORE_NAME_PATTERNS = (
    re.compile(r"\bbasic\s+display\s+adapter\b", re.IGNORECASE),
    re.compile(r"\bidd\b|indirect\s+display", re.IGNORECASE),
    re.compile(r"\bremote\s+display\b", re.IGNORECASE),
    re.compile(r"\bvirtual\s+display\b", re.IGNORECASE),
    re.compile(r"\bdisplay\s*link\b", re.IGNORECASE),
)


def _detect_vendor(name: str) -> Vendor:
    for pattern, vendor in _VENDOR_PATTERNS:
        if pattern.search(name):
            return vendor
    return Vendor.UNKNOWN


# nvidia model -> arch. ordered most-specific first; first match wins.
_NVIDIA_PATTERNS = (
    # blackwell -- rtx 50-series (2025+). 50xx covers desktop + mobile.
    (re.compile(r"\brtx\s*50\d{2}\b", re.IGNORECASE),                                   NvidiaGen.BLACKWELL),
    # ada lovelace -- rtx 40-series (2022-2024).
    (re.compile(r"\brtx\s*40\d{2}\b", re.IGNORECASE),                                   NvidiaGen.ADA),
    # ampere -- rtx 30-series.
    (re.compile(r"\brtx\s*30\d{2}\b", re.IGNORECASE),                                   NvidiaGen.AMPERE),
    # turing -- rtx 20-series + gtx 16-series.
    (re.compile(r"\brtx\s*20\d{2}\b|\bgtx\s*16\d{2}\b|\btitan\s*rtx\b", re.IGNORECASE), NvidiaGen.TURING),
    # pascal -- gtx 10-series + titan xp; pascal quadro variants match via the 'p' prefix.
    (re.compile(r"\bgtx\s*10\d{2}\b|\btitan\s*xp\b|\bquadro\s*p\d", re.IGNORECASE),     NvidiaGen.PASCAL),
    # maxwell 2nd gen -- gtx 9-series (gm204/206). gtx 750/750ti was maxwell 1 which didnt have hevc, so we deliberately dont claim it.
    (re.compile(r"\bgtx\s*9\d{2}\b|\btitan\s*x\b", re.IGNORECASE),                      NvidiaGen.MAXWELL2),
)


def _detect_nvidia_gen(name: str) -> NvidiaGen:
    for pattern, gen in _NVIDIA_PATTERNS:
        if pattern.search(name):
            return gen
    return NvidiaGen.PRE_MAXWELL2


_AMD_PATTERNS = (
    # rdna 4 -- rx 9000-series (forward-looking).
    (re.compile(r"\brx\s*9\d{3}\b", re.IGNORECASE),                                                  AmdGen.RDNA4),
    # rdna 3 -- rx 7000-series.
    (re.compile(r"\brx\s*7\d{3}\b", re.IGNORECASE),                                                  AmdGen.RDNA3),
    # rdna 2 -- rx 6000-series + rdna-2 integrated (rembrandt, ryzen 6000u/h).
    (re.compile(r"\brx\s*6\d{3}\b|\bradeon\s+graphics\s*\(rembrandt\)", re.IGNORECASE),              AmdGen.RDNA2),
    # rdna 1 -- rx 5000-series.
    (re.compile(r"\brx\s*5\d{3}\b", re.IGNORECASE),                                                  AmdGen.RDNA1),
    # vega / radeon vii.
    (re.compile(r"\brx\s+vega\b|\bvega\s*\d|\bradeon\s+vii\b", re.IGNORECASE),                       AmdGen.VEGA),
    # polaris -- rx 4xx / 5xx (570/580/590 etc.). 5xx range conflicts with rdna1s 5000-series, so we match 3-digit '5xx' only (not 4-digit '5xxx').
    (re.compile(r"\brx\s*4\d{2}\b|\brx\s*5\d{2}(?!\d)", re.IGNORECASE),                              AmdGen.POLARIS),
)


def _detect_amd_gen(name: str) -> AmdGen:
    for pattern, gen in _AMD_PATTERNS:
        if pattern.search(name):
            return gen
    return AmdGen.PRE_POLARIS


_INTEL_PATTERNS = (
    # arc discrete + xe-hpg. arc a310/a380/a580/a750/a770 all share the alchemist hevc encode block.
    (re.compile(r"\barc\s*a?\d{3}\b|\bxe-hpg\b", re.IGNORECASE),                IntelGen.ARC),
    # alder/raptor/meteor lake iris xe in 12th-14th gen.
    (re.compile(r"\b(12|13|14)\d{2}[a-z]?\b.*(uhd|iris)", re.IGNORECASE),       IntelGen.ALDER_LAKE),
    # tiger lake (11th gen) -- xe-lp.
    (re.compile(r"\b11\d{2}[a-z]?\b.*(iris|xe)|\bxe\s*graphics\b", re.IGNORECASE), IntelGen.TIGER_LAKE),
    # ice lake (10th gen) -- first xe igpu.
    (re.compile(r"\b10\d{2}[a-z]?\b.*iris", re.IGNORECASE),                     IntelGen.ICE_LAKE),
    # kaby lake (7th gen) hd 6xx. coffee lake (8th/9th) + comet lake (10th non-ice-lake) reuse the same igpu silicon, so they share this rule.
    (re.compile(r"\b(uhd|hd)\s*graphics\s*6\d{2}\b", re.IGNORECASE),            IntelGen.KABY_LAKE),
    # skylake (6th gen) hd 5xx -- first with qsv hevc.
    (re.compile(r"\bhd\s*graphics\s*5\d{2}\b", re.IGNORECASE),                  IntelGen.SKYLAKE),
)


def _detect_intel_gen(name: str) -> IntelGen:
    for pattern, gen in _INTEL_PATTERNS:
        if pattern.search(name):
            return gen
    return IntelGen.PRE_SKYLAKE


def _classify(raw: dict) -> Optional[Gpu]:
    """map a win32_videocontroller row to a Gpu, or None if it looks like a virtual/basic/fallback adapter we should ignore."""
    name = (raw.get("Name") or "").strip()
    if not name:
        return None
    for skip in _IGNORE_NAME_PATTERNS:
        if skip.search(name):
            return None

    vendor = _detect_vendor(name)
    if vendor is Vendor.NVIDIA:
        gen = _detect_nvidia_gen(name).value
    elif vendor is Vendor.AMD:
        gen = _detect_amd_gen(name).value
    elif vendor is Vendor.INTEL:
        gen = _detect_intel_gen(name).value
    else:
        gen = None

    vram = int(raw.get("AdapterRAM") or 0)
    # heuristic: igpus typically expose <2 gb adapterram (rest is shared/dynamic). discrete cards report full vram. 2 gb threshold catches apus/igpus that reserve up to 2 gb dedicated (ryzen) without losing genuinely small discrete cards from ~2014.
    is_discrete = vram >= 2 * 1024**3 and vendor is not Vendor.INTEL

    return Gpu(
        name        = name,
        vendor      = vendor,
        generation  = gen,
        is_discrete = is_discrete,
        vram_bytes  = vram,
        driver      = (raw.get("DriverVersion") or "").strip(),
    )


def list_gpus() -> List[Gpu]:
    """every adapter we can usefully classify, discrete cards first."""
    rows = _query_video_controllers()
    out  = [g for g in (_classify(r) for r in rows) if g is not None]
    # sort: discrete first; within tier, nvidia > amd > intel > unknown.
    vendor_rank = {Vendor.NVIDIA: 0, Vendor.AMD: 1, Vendor.INTEL: 2, Vendor.UNKNOWN: 3}
    out.sort(key=lambda g: (not g.is_discrete, vendor_rank[g.vendor]))
    return out


def primary_gpu() -> Optional[Gpu]:
    """best guess at which adapter obs will use for hardware encoding. None means callers should fall back to software encode."""
    gpus = list_gpus()
    return gpus[0] if gpus else None
