"""enumerate windows audio devices. obs stores endpoint ids in mmdevice format -- {0.0.1.00000000}.<endpoint-guid> for capture, {0.0.0.00000000}.<endpoint-guid> for render. friendly names live under each endpoints Properties subkey."""

import winreg
from dataclasses import dataclass
from typing import List, Optional


# pkey_device_friendlyname in the mmdevices property store.
_PKEY_FRIENDLY_NAME = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"

_CAPTURE_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture"
_RENDER_KEY  = r"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"

# devicestate bits. only the low nibble carries the documented mmdevice states; some virtual-audio drivers (voicemeeter etc.) set extra high bits.
_STATE_ACTIVE = 0x1

# literal string obs uses for 'follow the system defualt device'.
DEFAULT_DEVICE_ID = "default"

DEFAULT_DEVICE_NAME = "Default (system)"


@dataclass(frozen=True)
class AudioDevice:
    """An OBS-format audio endpoint."""
    device_id: str  # obs-format device id ({0.0.1.00000000}.<guid>)
    name: str  # friendly name (e.g. 'microphone (realtek)')


def _to_obs_id(endpoint_guid: str) -> str:
    return f"{{0.0.1.00000000}}.{endpoint_guid}"


def _to_render_obs_id(endpoint_guid: str) -> str:
    return f"{{0.0.0.00000000}}.{endpoint_guid}"


def find_render_endpoint(friendly_name: str) -> Optional[str]:
    """OBS-format render device id matching friendly_name (case-insensitive exact). Used to write the fresh OBS Stream Audio id straight into basic.ini so OBS picks the right monitoring sink on first launch."""
    needle = friendly_name.lower()
    try:
        render = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _RENDER_KEY)
    except OSError:
        return None

    with render:
        i = 0
        while True:
            try:
                endpoint = winreg.EnumKey(render, i)
            except OSError:
                break
            i += 1

            try:
                with winreg.OpenKey(render, endpoint) as ep:
                    state, _ = winreg.QueryValueEx(ep, "DeviceState")
            except OSError:
                continue
            if not (state & _STATE_ACTIVE):
                continue

            try:
                with winreg.OpenKey(render, fr"{endpoint}\Properties") as props:
                    name, _ = winreg.QueryValueEx(props, _PKEY_FRIENDLY_NAME)
            except OSError:
                continue

            if name.lower() == needle:
                return _to_render_obs_id(endpoint)

    return None


def _list_render_endpoints() -> List[AudioDevice]:
    """Active render endpoints known to Windows, in OBS id format."""
    devices: List[AudioDevice] = []
    try:
        render = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _RENDER_KEY)
    except OSError:
        return devices

    with render:
        i = 0
        while True:
            try:
                endpoint = winreg.EnumKey(render, i)
            except OSError:
                break
            i += 1

            try:
                with winreg.OpenKey(render, endpoint) as ep:
                    state, _ = winreg.QueryValueEx(ep, "DeviceState")
            except OSError:
                continue
            if not (state & _STATE_ACTIVE):
                continue

            try:
                with winreg.OpenKey(render, fr"{endpoint}\Properties") as props:
                    name, _ = winreg.QueryValueEx(props, _PKEY_FRIENDLY_NAME)
            except OSError:
                name = endpoint

            devices.append(AudioDevice(_to_render_obs_id(endpoint), name))

    return devices


def _replaykit_monitor_rank(name: str) -> Optional[int]:
    """Rank only the ReplayKit/VB-CABLE render sink; never match normal speakers."""
    lower = name.strip().lower()
    if "surround" in lower or "16ch" in lower:
        return None
    if lower == "obs stream audio":
        return 0
    if lower.startswith("obs stream audio") and "loopback" not in lower:
        return 10
    if lower.startswith("cable input"):
        return 20
    return None


def find_replaykit_monitoring_endpoint() -> Optional[AudioDevice]:
    """Best active render endpoint for OBS monitoring, before or after ReplayKit's VB-CABLE rename."""
    candidates: List[tuple[int, str, AudioDevice]] = []
    for device in _list_render_endpoints():
        rank = _replaykit_monitor_rank(device.name)
        if rank is not None:
            candidates.append((rank, device.name.lower(), device))
    if not candidates:
        return None
    candidates.sort(key=lambda item: (item[0], item[1]))
    return candidates[0][2]


def list_microphones() -> List[AudioDevice]:
    """active capture devices known to windows, alphabetized."""
    devices: List[AudioDevice] = []
    try:
        cap = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _CAPTURE_KEY)
    except OSError:
        return devices

    with cap:
        i = 0
        while True:
            try:
                endpoint = winreg.EnumKey(cap, i)
            except OSError:
                break
            i += 1

            # active devices only (low bit set).
            try:
                with winreg.OpenKey(cap, endpoint) as ep:
                    state, _ = winreg.QueryValueEx(ep, "DeviceState")
            except OSError:
                continue
            if not (state & _STATE_ACTIVE):
                continue

            try:
                with winreg.OpenKey(cap, fr"{endpoint}\Properties") as props:
                    name, _ = winreg.QueryValueEx(props, _PKEY_FRIENDLY_NAME)
            except OSError:
                name = endpoint

            devices.append(AudioDevice(_to_obs_id(endpoint), name))

    devices.sort(key=lambda d: d.name.lower())
    return devices
