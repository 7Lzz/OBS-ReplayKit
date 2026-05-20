"""detect windows displays in the format obss monitor_capture source uses (\\\\?\\DISPLAY#<edid-model>#...#{class-guid}). produced by EnumDisplayDevicesW with EDD_GET_DEVICE_INTERFACE_NAME against the adapter device name from GetMonitorInfoExW."""

import ctypes
from ctypes import wintypes
from dataclasses import dataclass
from typing import List, Optional


_EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001
_MONITORINFOF_PRIMARY          = 0x00000001


class _DISPLAY_DEVICEW(ctypes.Structure):
    _fields_ = [
        ("cb",           wintypes.DWORD),
        ("DeviceName",   wintypes.WCHAR * 32),
        ("DeviceString", wintypes.WCHAR * 128),
        ("StateFlags",   wintypes.DWORD),
        ("DeviceID",     wintypes.WCHAR * 128),
        ("DeviceKey",    wintypes.WCHAR * 128),
    ]


class _MONITORINFOEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize",    wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork",    wintypes.RECT),
        ("dwFlags",   wintypes.DWORD),
        ("szDevice",  wintypes.WCHAR * 32),
    ]


_MONITORENUMPROC = ctypes.WINFUNCTYPE(
    wintypes.BOOL,
    wintypes.HMONITOR, wintypes.HDC,
    ctypes.POINTER(wintypes.RECT), wintypes.LPARAM,
)

_user32 = ctypes.WinDLL("user32", use_last_error=True)
_user32.EnumDisplayMonitors.argtypes = [
    wintypes.HDC, ctypes.POINTER(wintypes.RECT),
    _MONITORENUMPROC, wintypes.LPARAM,
]
_user32.EnumDisplayMonitors.restype  = wintypes.BOOL
_user32.GetMonitorInfoW.argtypes     = [wintypes.HMONITOR, ctypes.POINTER(_MONITORINFOEXW)]
_user32.GetMonitorInfoW.restype      = wintypes.BOOL
_user32.EnumDisplayDevicesW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.POINTER(_DISPLAY_DEVICEW), wintypes.DWORD,
]
_user32.EnumDisplayDevicesW.restype  = wintypes.BOOL


@dataclass(frozen=True)
class Display:
    """one physical display attached to the system."""
    is_primary:    bool
    adapter:       str  # e.g. \\.\display1 -- the gdi adapter device name
    device_id:     str  # e.g. \\\display#msi3cd7#5&...#{guid} -- obss monitor_id
    friendly_name: str  # what windows tells us (often 'generic pnp monitor')
    width:         int
    height:        int

    @property
    def edid_model(self) -> str:
        """edid model code from device_id (e.g. 'MSI3CD7')."""
        parts = self.device_id.split("#")
        return parts[1] if len(parts) > 1 else "?"


def list_displays() -> List[Display]:
    """every active display, primary first."""
    monitors = []

    def _cb(hmon, _hdc, _rect, _lparam):
        info = _MONITORINFOEXW()
        info.cbSize = ctypes.sizeof(info)
        if _user32.GetMonitorInfoW(hmon, ctypes.byref(info)):
            monitors.append(info)
        return True

    _user32.EnumDisplayMonitors(None, None, _MONITORENUMPROC(_cb), 0)

    displays: List[Display] = []
    for info in monitors:
        dd = _DISPLAY_DEVICEW()
        dd.cb = ctypes.sizeof(dd)
        if not _user32.EnumDisplayDevicesW(
            info.szDevice, 0, ctypes.byref(dd), _EDD_GET_DEVICE_INTERFACE_NAME
        ):
            continue
        displays.append(Display(
            is_primary    = bool(info.dwFlags & _MONITORINFOF_PRIMARY),
            adapter       = info.szDevice,
            device_id     = dd.DeviceID,
            friendly_name = dd.DeviceString,
            width         = info.rcMonitor.right  - info.rcMonitor.left,
            height        = info.rcMonitor.bottom - info.rcMonitor.top,
        ))

    displays.sort(key=lambda d: not d.is_primary)
    return displays


def primary_display() -> Optional[Display]:
    """the users primary monitor, or None if no display is reachable."""
    for d in list_displays():
        if d.is_primary:
            return d
    return None
