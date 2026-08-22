"""recording presets -- resolution + fps + cqp target tier. the actual encoder json is built per-machine in encoder.py from the detected gpu + codec preference, so the same preset works on nvidia/amd/intel/software."""

from dataclasses import dataclass
from typing import Dict, Tuple


@dataclass(frozen=True)
class RecordingPreset:
    """one tier in the recording-quality ladder. basic_ini is section->key->value overrides; cqp_target is on the nvenc scale (0-51, lower = higher quality) and the encoder module translates to whatever scale the chosen encoder uses."""
    name:        str
    label:       str
    description: str
    basic_ini:   Dict[str, Dict[str, str]]
    cqp_target:  int


PRESETS: Tuple[RecordingPreset, ...] = (
    RecordingPreset(
        name        = "performance",
        label       = "Performance",
        description = "720p30 - low GPU/CPU cost, smaller files. Works on any PC.",
        basic_ini={
            "Output": {"Mode": "Advanced"},
            "AdvOut": {
                "RecType":   "Standard",
                "RecTracks": "1",
            },
            "Video": {
                "BaseCX":    "1920",  "BaseCY":   "1080",
                "OutputCX":  "1280",  "OutputCY": "720",
                "FPSCommon": "30",
                "ScaleType": "lanczos",
            },
        },
        cqp_target     = 26,
    ),

    RecordingPreset(
        name        = "balanced",
        label       = "Balanced (recommended)",
        description = "Native canvas -> 1080p60, high-quality target - good on most modern PCs.",
        basic_ini={
            "Output": {"Mode": "Advanced"},
            "AdvOut": {
                "RecType":   "Standard",
                "RecTracks": "1",
            },
            "Video": {
                "BaseCX":    "1920",  "BaseCY":   "1080",
                "OutputCX":  "1920",  "OutputCY": "1080",
                "FPSCommon": "60",
                "ScaleType": "lanczos",
            },
        },
        cqp_target     = 22,
    ),

    RecordingPreset(
        name        = "quality",
        label       = "Quality (high-end PCs)",
        description = "Native canvas -> 1080p60, very high quality target. Best for replay clips.",
        basic_ini={
            "Output": {"Mode": "Advanced"},
            "AdvOut": {
                "RecType":   "Standard",
                "RecTracks": "1",
            },
            "Video": {
                "BaseCX":    "2560",  "BaseCY":   "1440",
                "OutputCX":  "1920",  "OutputCY": "1080",
                "FPSCommon": "60",
                "ScaleType": "lanczos",
            },
        },
        cqp_target     = 20,
    ),
)


def get_preset(name: str) -> RecordingPreset:
    for p in PRESETS:
        if p.name == name:
            return p
    raise ValueError(f"Unknown recording preset: {name!r}")
