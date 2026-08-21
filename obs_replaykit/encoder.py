"""pick the right obs recording encoder + settings for the users gpu. inputs: detected gpu (vendor+gen), codec preference (auto/h264/h265), preset cqp target, compression_mode (lower_gpu/balanced/smaller_files). outputs an EncoderChoice with the obs encoder id, encoder json, and human-readable label. fallback chain: user-preferred codec -> next-best hardware codec on this gpu -> software x264 (always available). av1 is deliberately not offered -- most iphones and plenty of android devices have no av1 decoder, and unlike a container-tag issue theres no fix for missing silicon."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, Optional

from .gpu import AmdGen, Gpu, IntelGen, NvidiaGen, Vendor


class CodecPreference(str, Enum):
    """user-facing codec preference. AUTO picks the best for the gpu."""
    AUTO  = "auto"
    H264  = "h264"
    H265  = "h265"

    @classmethod
    def parse(cls, raw: str) -> "CodecPreference":
        try:
            return cls(raw.strip().lower())
        except (AttributeError, ValueError):
            return cls.AUTO


@dataclass(frozen=True)
class EncoderChoice:
    """resolved encoder selection for this machine."""
    obs_encoder_id: str
    settings:       Dict[str, Any]
    codec:          str  # "h264" / "h265" -- for the menu
    backend:        str  # "nvenc" / "amf" / "qsv" / "x264"
    label:          str  # short label, e.g. "nvenc hevc (blackwell)"
    description:    str  # one-liner explaining the tradeoff

    @property
    def is_hardware(self) -> bool:
        return self.backend != "x264"


# capability tables -- what each generation can actualy do. the encoder json we emit only references features the silicon supports; obs otherwise silently ignores unknown keys but the encoder may misbehave.

_NVIDIA_HEVC_OK    = frozenset({NvidiaGen.MAXWELL2.value, NvidiaGen.PASCAL.value,
                                NvidiaGen.TURING.value, NvidiaGen.AMPERE.value,
                                NvidiaGen.ADA.value,     NvidiaGen.BLACKWELL.value})
# nvenc hevc b-frames: turing+. pascal hevc cant use b-frames.
_NVIDIA_BF_HEVC_OK = frozenset({NvidiaGen.TURING.value, NvidiaGen.AMPERE.value,
                                NvidiaGen.ADA.value,    NvidiaGen.BLACKWELL.value})
# lookahead: pascal+ (maxwell 2 doesnt implement it).
_NVIDIA_LOOKAHEAD_OK = frozenset({NvidiaGen.PASCAL.value,  NvidiaGen.TURING.value,
                                  NvidiaGen.AMPERE.value,  NvidiaGen.ADA.value,
                                  NvidiaGen.BLACKWELL.value})

_AMD_HEVC_OK = frozenset({AmdGen.POLARIS.value, AmdGen.VEGA.value, AmdGen.RDNA1.value,
                          AmdGen.RDNA2.value,  AmdGen.RDNA3.value, AmdGen.RDNA4.value})
# amf hevc b-frames: vega+ (polaris hevc has no b-frame support).
_AMD_BF_OK   = frozenset({AmdGen.VEGA.value, AmdGen.RDNA1.value,
                          AmdGen.RDNA2.value, AmdGen.RDNA3.value, AmdGen.RDNA4.value})

_INTEL_HEVC_OK = frozenset({IntelGen.SKYLAKE.value,   IntelGen.KABY_LAKE.value,
                            IntelGen.ICE_LAKE.value,  IntelGen.TIGER_LAKE.value,
                            IntelGen.ALDER_LAKE.value,IntelGen.ARC.value})


# compression-mode effort tables. balanced is the validated default (~10-11% gpu on a 3060 ti at 1080p60 hevc, vs the previous heavyweight config which sat near 30%). lower_gpu trades file size for less encoder work; smaller_files does the opposite. cqp stays the same across modes -- only encoder effort changes, so visual quality is held constant while gpu/filesize move opposite directions.

_NVENC_EFFORT = {
    "lower_gpu":     {"preset": "p1", "multipass": "disabled", "lookahead": False, "bf": 0},
    "balanced":      {"preset": "p2", "multipass": "disabled", "lookahead": False, "bf": 2},
    "smaller_files": {"preset": "p5", "multipass": "qres",     "lookahead": True,  "bf": 3},
}

_AMF_EFFORT = {
    "lower_gpu":     {"preset": "speed",    "bf": 0},
    "balanced":      {"preset": "balanced", "bf": 2},
    "smaller_files": {"preset": "quality",  "bf": 3},
}

_QSV_EFFORT = {
    "lower_gpu":     "veryfast",
    "balanced":      "balanced",
    "smaller_files": "slower",
}

_X264_EFFORT = {
    "lower_gpu":     "superfast",
    "balanced":      "veryfast",
    "smaller_files": "medium",
}


def _effort(table: dict, mode: str):
    """look up the requested effort tier; fall back to balanced for unknown modes (forwards-compat with future values)."""
    return table.get(mode, table["balanced"])


# encoder json builders. cqp is the nvenc-scale quality knob (lower = higher quality); qsv translates to icq, x264 translates to crf. mapping isnt perfectly linear but close enough that one slider drives all encoders consistently.

def _to_qsv_icq(cqp: int) -> int:
    """nvenc/amf cqp -> intel qsv icq (1-51, lower = higher quality). +1 offset becuase qsv hevc is a bit less efficient than nvenc hevc."""
    return max(1, min(51, cqp + 1))


def _to_x264_crf(cqp: int) -> int:
    """nvenc cqp -> libx264 crf. software x264 is more bit-efficient than nvenc at the same numeric value, so shift down ~2 to keep visual quality comparable without blowing up file size."""
    return max(0, min(51, cqp - 2))


def _build_nvenc_h264(cqp: int, gen: str, mode: str = "balanced") -> Dict[str, Any]:
    """nvenc h.264. preset/multipass/lookahead/bf come from the user-selected compression_mode."""
    effort = _effort(_NVENC_EFFORT, mode)
    s: Dict[str, Any] = {
        "rate_control": "CQP",
        "cqp":          cqp,
        "keyint_sec":   2,
        "preset":       effort["preset"],
        "multipass":    effort["multipass"],
        "tune":         "hq",
        "profile":      "high",
    }
    # b-frames: requested count, capped by generation support (pascal+ for nvenc h264).
    bf_ok = gen in {NvidiaGen.PASCAL.value, NvidiaGen.TURING.value, NvidiaGen.AMPERE.value,
                    NvidiaGen.ADA.value, NvidiaGen.BLACKWELL.value}
    s["bf"] = effort["bf"] if bf_ok else 0
    s["lookahead"] = effort["lookahead"] and (gen in _NVIDIA_LOOKAHEAD_OK)
    return s


def _build_nvenc_hevc(cqp: int, gen: str, mode: str = "balanced") -> Dict[str, Any]:
    """nvenc hevc. profile=main keeps it 8-bit -- 10-bit hevc doubles encode cost and the dock cef preview doesnt decode hdr hevc reliably."""
    effort = _effort(_NVENC_EFFORT, mode)
    s: Dict[str, Any] = {
        "rate_control": "CQP",
        # hevc is ~30-50% more efficient than h.264 at the same perceived quality, so we use a slightly higher cqp for the same look.
        "cqp":          cqp + 2,
        "keyint_sec":   2,
        "preset":       effort["preset"],
        "multipass":    effort["multipass"],
        "tune":         "hq",
        "profile":      "main",
    }
    s["bf"]        = effort["bf"] if gen in _NVIDIA_BF_HEVC_OK else 0
    s["lookahead"] = effort["lookahead"] and (gen in _NVIDIA_LOOKAHEAD_OK)
    return s


def _build_amf_h264(cqp: int, _gen: str, mode: str = "balanced") -> Dict[str, Any]:
    """amd amf h.264. filler_data off becuase for recording it just wastes disk."""
    effort = _effort(_AMF_EFFORT, mode)
    return {
        "rate_control":  "CQP",
        "cqp":           cqp,
        "keyint_sec":    2,
        "preset":        effort["preset"],
        "profile":       "high",
        "filler_data":   False,
        "bf":            effort["bf"],
    }


def _build_amf_hevc(cqp: int, gen: str, mode: str = "balanced") -> Dict[str, Any]:
    """amf hevc (polaris+). rdna2+ adds b-frame support."""
    effort = _effort(_AMF_EFFORT, mode)
    s: Dict[str, Any] = {
        "rate_control":  "CQP",
        "cqp":           cqp + 2,  # match nvenc hevc offset
        "keyint_sec":    2,
        "preset":        effort["preset"],
        "profile":       "main",
        "filler_data":   False,
    }
    s["bf"] = effort["bf"] if gen in _AMD_BF_OK else 0
    return s


def _build_qsv_h264(cqp: int, _gen: str, mode: str = "balanced") -> Dict[str, Any]:
    """quick sync h.264. icq ('intelligent constant quality') is qsvs closest equivalent to nvenc cqp -- bits track perceptual quality, not a hard bitrate."""
    return {
        "rate_control":  "ICQ",
        "icq_quality":   _to_qsv_icq(cqp),
        "keyint_sec":    2,
        "target_usage":  _effort(_QSV_EFFORT, mode),
        "profile":       "high",
        "async_depth":   4,
        "low_power":     False,
    }


def _build_qsv_hevc(cqp: int, _gen: str, mode: str = "balanced") -> Dict[str, Any]:
    return {
        "rate_control":  "ICQ",
        "icq_quality":   _to_qsv_icq(cqp + 2),
        "keyint_sec":    2,
        "target_usage":  _effort(_QSV_EFFORT, mode),
        "profile":       "main",
        "async_depth":   4,
        "low_power":     False,
    }


def _build_x264(cqp: int, mode: str = "balanced") -> Dict[str, Any]:
    """libx264 software encoder. last-resort fallback."""
    return {
        "rate_control": "CRF",
        "crf":          _to_x264_crf(cqp),
        "keyint_sec":   2,
        "preset":       _effort(_X264_EFFORT, mode),
        "profile":      "high",
        "tune":         "",
        "x264opts":     "",
    }


@dataclass(frozen=True)
class _Candidate:
    """one concrete encoder we could pick. resolved into EncoderChoice by pick_encoder()."""
    obs_encoder_id: str
    codec:          str
    backend:        str
    label:          str
    description:    str
    builder:        Any  # callable (cqp, mode) -> dict


def _nvenc_candidates(gen: str) -> Dict[str, _Candidate]:
    av: Dict[str, _Candidate] = {}
    av["h264"] = _Candidate(
        obs_encoder_id = "obs_nvenc_h264_tex",
        codec          = "h264",
        backend        = "nvenc",
        label          = "NVENC H.264",
        description    = "NVIDIA hardware H.264 - plays anywhere, biggest files of these options.",
        builder        = lambda cqp, mode, g=gen: _build_nvenc_h264(cqp, g, mode),  # noqa: E731
    )
    if gen in _NVIDIA_HEVC_OK:
        av["h265"] = _Candidate(
            obs_encoder_id = "obs_nvenc_hevc_tex",
            codec          = "h265",
            backend        = "nvenc",
            label          = f"NVENC HEVC ({gen.title()})",
            description    = "NVIDIA hardware HEVC - ~half the file size of H.264 at the same quality.",
            builder        = lambda cqp, mode, g=gen: _build_nvenc_hevc(cqp, g, mode),
        )
    return av


def _amf_candidates(gen: str) -> Dict[str, _Candidate]:
    av: Dict[str, _Candidate] = {}
    av["h264"] = _Candidate(
        obs_encoder_id = "h264_amf",
        codec          = "h264",
        backend        = "amf",
        label          = "AMF H.264",
        description    = "AMD hardware H.264 - plays anywhere, modest compression.",
        builder        = lambda cqp, mode, g=gen: _build_amf_h264(cqp, g, mode),
    )
    if gen in _AMD_HEVC_OK:
        av["h265"] = _Candidate(
            obs_encoder_id = "h265_amf",
            codec          = "h265",
            backend        = "amf",
            label          = f"AMF HEVC ({gen.upper()})",
            description    = "AMD hardware HEVC - significantly smaller files than H.264.",
            builder        = lambda cqp, mode, g=gen: _build_amf_hevc(cqp, g, mode),
        )
    return av


def _qsv_candidates(gen: str) -> Dict[str, _Candidate]:
    av: Dict[str, _Candidate] = {}
    av["h264"] = _Candidate(
        obs_encoder_id = "obs_qsv11_h264",
        codec          = "h264",
        backend        = "qsv",
        label          = "Quick Sync H.264",
        description    = "Intel iGPU H.264 - low CPU/GPU cost, larger files.",
        builder        = lambda cqp, mode, g=gen: _build_qsv_h264(cqp, g, mode),
    )
    if gen in _INTEL_HEVC_OK:
        av["h265"] = _Candidate(
            obs_encoder_id = "obs_qsv11_hevc",
            codec          = "h265",
            backend        = "qsv",
            label          = f"Quick Sync HEVC ({gen.replace('_', ' ').title()})",
            description    = "Intel iGPU HEVC - smaller files, low overhead.",
            builder        = lambda cqp, mode, g=gen: _build_qsv_hevc(cqp, g, mode),
        )
    return av


def _software_candidate() -> _Candidate:
    """libx264 cpu encode fallback. no gpu needed."""
    return _Candidate(
        obs_encoder_id = "obs_x264",
        codec          = "h264",
        backend        = "x264",
        label          = "x264 (software)",
        description    = "CPU-only H.264 - works on any machine, uses CPU not GPU.",
        builder        = lambda cqp, mode, _g="": _build_x264(cqp, mode),
    )


def _available_candidates(gpu: Optional[Gpu]) -> Dict[str, _Candidate]:
    """codec slug -> usable encoder for this gpu. software x264 is always appended under 'software' so callers can fall back."""
    if gpu is None:
        return {"software": _software_candidate()}

    candidates: Dict[str, _Candidate] = {}
    gen = gpu.generation or ""
    if gpu.vendor is Vendor.NVIDIA:
        candidates = _nvenc_candidates(gen)
    elif gpu.vendor is Vendor.AMD:
        candidates = _amf_candidates(gen)
    elif gpu.vendor is Vendor.INTEL:
        candidates = _qsv_candidates(gen)

    candidates["software"] = _software_candidate()
    return candidates


# preferred-codec fallback order.
_FALLBACK_ORDER = {
    CodecPreference.H265: ("h265", "h264", "software"),
    CodecPreference.H264: ("h264", "software"),
    CodecPreference.AUTO: ("h265", "h264", "software"),
}


def pick_encoder(gpu: Optional[Gpu],
                 codec_preference: str,
                 cqp: int,
                 compression_mode: str = "balanced") -> EncoderChoice:
    """resolve codec preference + gpu + compression_mode into a concrete encoder choice. cqp is on the nvenc scale (0-51, lower = better); the chosen builder translates as needed (crf for x264, icq for qsv, etc.). compression_mode controls encoder effort (lower_gpu/balanced/smaller_files)."""
    pref       = CodecPreference.parse(codec_preference)
    candidates = _available_candidates(gpu)

    for codec in _FALLBACK_ORDER[pref]:
        cand = candidates.get(codec)
        if cand is None:
            continue
        return EncoderChoice(
            obs_encoder_id = cand.obs_encoder_id,
            settings       = cand.builder(cqp, compression_mode),
            codec          = cand.codec,
            backend        = cand.backend,
            label          = cand.label,
            description    = cand.description,
        )

    # unreachable: fallback_order always ends in 'software' and _available_candidates always includes it. defensive last-ditch so callers never get None.
    sw = _software_candidate()
    return EncoderChoice(
        obs_encoder_id = sw.obs_encoder_id,
        settings       = sw.builder(cqp, compression_mode),
        codec          = sw.codec,
        backend        = sw.backend,
        label          = sw.label,
        description    = sw.description,
    )


def available_codecs(gpu: Optional[Gpu]) -> Dict[str, str]:
    """codec slug -> short label for the codecs this gpu can actualy run. 'auto' is always included."""
    cand = _available_candidates(gpu)
    labels = {"auto": "Auto (pick best for this GPU)"}
    if "h264" in cand:
        labels["h264"] = cand["h264"].label
    if "h265" in cand:
        labels["h265"] = cand["h265"].label
    return labels
