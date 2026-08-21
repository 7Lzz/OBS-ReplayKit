"""Voicemeeter Remote API helpers used to preserve hardware inputs."""

from __future__ import annotations

import ctypes
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional

LogFn = Optional[Callable[[str], None]]

_DLL_CANDIDATES = (
    Path(r"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll"),
    Path(r"C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll"),
)

_TYPE_TO_PHYSICAL_STRIPS = {
    1: 2,  # Voicemeeter
    2: 3,  # Voicemeeter Banana / Pro
    3: 5,  # Voicemeeter Potato
}

_INPUT_DEVICE_TYPES = {
    1: "mme",
    3: "wdm",
    4: "ks",
}

_DRIVER_TO_DEVICE_TYPE = {
    "mme": 1,
    "wdm": 3,
    "ks": 4,
}

_RESTORE_DRIVER_ORDER = ("wdm", "ks", "mme")
_DEVICE_SETTLE_TIMEOUT = 10.0
_DEVICE_SETTLE_INTERVAL = 0.35
_PREFIX_MATCH_MIN_CHARS = 12


@dataclass(frozen=True)
class VoicemeeterDeviceChoice:
    name: str
    driver: str


@dataclass(frozen=True)
class VoicemeeterStripDevice:
    index: int
    name: str
    choices: tuple[VoicemeeterDeviceChoice, ...]


@dataclass(frozen=True)
class VoicemeeterSnapshot:
    dll_path: Path
    strips: tuple[VoicemeeterStripDevice, ...]


class _VoicemeeterRemote:
    def __init__(self, dll_path: Path) -> None:
        self.dll_path = dll_path
        self.dll = ctypes.WinDLL(str(dll_path))
        self._configure_signatures()
        self.logged_in = False

    def _configure_signatures(self) -> None:
        dll = self.dll
        dll.VBVMR_Login.restype = ctypes.c_long
        dll.VBVMR_Logout.restype = ctypes.c_long
        dll.VBVMR_IsParametersDirty.restype = ctypes.c_long
        dll.VBVMR_GetVoicemeeterType.argtypes = [ctypes.POINTER(ctypes.c_long)]
        dll.VBVMR_GetVoicemeeterType.restype = ctypes.c_long
        dll.VBVMR_GetParameterStringW.argtypes = [ctypes.c_char_p, ctypes.c_wchar_p]
        dll.VBVMR_GetParameterStringW.restype = ctypes.c_long
        dll.VBVMR_SetParameterStringW.argtypes = [ctypes.c_char_p, ctypes.c_wchar_p]
        dll.VBVMR_SetParameterStringW.restype = ctypes.c_long
        dll.VBVMR_SetParameterFloat.argtypes = [ctypes.c_char_p, ctypes.c_float]
        dll.VBVMR_SetParameterFloat.restype = ctypes.c_long
        dll.VBVMR_Input_GetDeviceNumber.restype = ctypes.c_long
        dll.VBVMR_Input_GetDeviceDescW.argtypes = [
            ctypes.c_long,
            ctypes.POINTER(ctypes.c_long),
            ctypes.c_wchar_p,
            ctypes.c_wchar_p,
        ]
        dll.VBVMR_Input_GetDeviceDescW.restype = ctypes.c_long

    def __enter__(self) -> "_VoicemeeterRemote":
        rc = self.dll.VBVMR_Login()
        if rc == 1:
            raise _VoicemeeterNotRunning()
        if rc != 0:
            raise RuntimeError(f"Voicemeeter is not running or not reachable (login={rc})")
        self.logged_in = True
        try:
            self.dll.VBVMR_IsParametersDirty()
        except Exception:
            pass
        return self

    def __exit__(self, _exc_type, _exc, _tb) -> None:
        if self.logged_in:
            self.dll.VBVMR_Logout()
            self.logged_in = False

    def voicemeeter_type(self) -> int:
        value = ctypes.c_long()
        rc = self.dll.VBVMR_GetVoicemeeterType(ctypes.byref(value))
        if rc != 0:
            raise RuntimeError(f"Voicemeeter type query failed ({rc})")
        return int(value.value)

    def get_strip_device_name(self, index: int) -> str:
        buf = ctypes.create_unicode_buffer(512)
        rc = self.dll.VBVMR_GetParameterStringW(f"Strip[{index}].device.name".encode("ascii"), buf)
        if rc != 0:
            return ""
        return buf.value.strip()

    def get_strip_device_type(self, index: int) -> int:
        value = ctypes.c_float()
        rc = self.dll.VBVMR_GetParameterFloat(f"Strip[{index}].device.type".encode("ascii"), ctypes.byref(value))
        if rc != 0:
            return 0
        return int(value.value)

    def input_devices_by_name(self) -> dict[str, set[str]]:
        devices: dict[str, set[str]] = {}
        count = self.dll.VBVMR_Input_GetDeviceNumber()
        if count <= 0:
            return devices
        for index in range(count):
            device_type = ctypes.c_long()
            name = ctypes.create_unicode_buffer(512)
            hardware_id = ctypes.create_unicode_buffer(512)
            rc = self.dll.VBVMR_Input_GetDeviceDescW(index, ctypes.byref(device_type), name, hardware_id)
            if rc != 0:
                continue
            driver = _INPUT_DEVICE_TYPES.get(int(device_type.value))
            clean_name = name.value.strip()
            if driver and clean_name:
                devices.setdefault(clean_name, set()).add(driver)
        return devices

    def set_strip_device_choice(self, strip: VoicemeeterStripDevice, choice: VoicemeeterDeviceChoice) -> bool:
        param = f"Strip[{strip.index}].Device.{choice.driver}".encode("ascii")
        rc = self.dll.VBVMR_SetParameterStringW(param, choice.name)
        return rc == 0

    def set_strip_device(self, strip: VoicemeeterStripDevice) -> VoicemeeterDeviceChoice | None:
        for choice in strip.choices:
            self.clear_strip_device(strip)
            time.sleep(0.2)
            if not self.set_strip_device_choice(strip, choice):
                continue
            time.sleep(0.5)
            if self.strip_device_matches(strip.index, choice):
                return choice
        return None

    def clear_strip_device(self, strip: VoicemeeterStripDevice) -> bool:
        cleared = False
        for driver in _RESTORE_DRIVER_ORDER:
            param = f"Strip[{strip.index}].Device.{driver}".encode("ascii")
            if self.dll.VBVMR_SetParameterStringW(param, "") == 0:
                cleared = True
        return cleared

    def clear_strip_index(self, index: int) -> bool:
        cleared = False
        for driver in _RESTORE_DRIVER_ORDER:
            param = f"Strip[{index}].Device.{driver}".encode("ascii")
            rc = self.dll.VBVMR_SetParameterStringW(param, "")
            if rc == 0:
                cleared = True
        return cleared

    def wait_for_input_device(self, strip: VoicemeeterStripDevice, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() <= deadline:
            devices = self.input_devices_by_name()
            if any(choice.driver in devices.get(choice.name, set()) for choice in strip.choices):
                return True
            time.sleep(_DEVICE_SETTLE_INTERVAL)
        return False

    def restart_audio_engine(self) -> bool:
        rc = self.dll.VBVMR_SetParameterFloat(b"Command.Restart", ctypes.c_float(1.0))
        return rc == 0

    def strip_device_matches(self, index: int, choice: VoicemeeterDeviceChoice) -> bool:
        expected_type = _DRIVER_TO_DEVICE_TYPE.get(choice.driver)
        return (
            expected_type is not None
            and self.get_strip_device_type(index) == expected_type
            and _device_names_match(self.get_strip_device_name(index), choice.name)
        )


class _VoicemeeterNotRunning(Exception):
    pass


def _voicemeeter_dll() -> Path | None:
    for path in _DLL_CANDIDATES:
        if path.is_file():
            return path
    return None


def _device_names_match(left: str, right: str) -> bool:
    a = left.strip()
    b = right.strip()
    if not a or not b:
        return False
    return a == b or (
        min(len(a), len(b)) >= _PREFIX_MATCH_MIN_CHARS
        and (a.startswith(b) or b.startswith(a))
    )


def _input_device_choices(selected_name: str, devices: dict[str, set[str]]) -> tuple[VoicemeeterDeviceChoice, ...]:
    """return verified restore candidates for Voicemeeters sometimes truncated device name."""
    name = selected_name.strip()
    if not name:
        return ()

    candidates: list[tuple[int, int, int, str, str, str]] = []
    for device_name, drivers in devices.items():
        if device_name == name:
            match_rank = 0
        elif _device_names_match(device_name, name):
            match_rank = 1
        else:
            continue
        for driver_rank, driver in enumerate(_RESTORE_DRIVER_ORDER):
            if driver in drivers:
                candidates.append((driver_rank, match_rank, -len(device_name), device_name.casefold(), device_name, driver))

    candidates.sort()
    choices: list[VoicemeeterDeviceChoice] = []
    seen: set[tuple[str, str]] = set()
    for _driver_rank, _match_rank, _length_rank, _device_key, device_name, driver in candidates:
        key = (device_name, driver)
        if key in seen:
            continue
        seen.add(key)
        choices.append(VoicemeeterDeviceChoice(name=device_name, driver=driver))
    return tuple(choices)


def snapshot_voicemeeter_inputs(log: LogFn = None) -> VoicemeeterSnapshot | None:
    """capture Voicemeeter physical input selections if Voicemeeter is running."""
    dll_path = _voicemeeter_dll()
    if dll_path is None:
        return None

    try:
        with _VoicemeeterRemote(dll_path) as remote:
            vm_type = remote.voicemeeter_type()
            strip_count = _TYPE_TO_PHYSICAL_STRIPS.get(vm_type, 0)
            if strip_count <= 0:
                if log:
                    log(f"warn: unsupported Voicemeeter type {vm_type}; input restore skipped")
                return None
            devices = remote.input_devices_by_name()
            strips: list[VoicemeeterStripDevice] = []
            for index in range(strip_count):
                name = remote.get_strip_device_name(index)
                if not name:
                    continue
                choices = _input_device_choices(name, devices)
                if not choices:
                    if log:
                        log(f"warn: Voicemeeter strip {index + 1} input '{name}' was not in the device list")
                    continue
                if log:
                    first = choices[0]
                    if first.name != name:
                        log(f"resolved Voicemeeter strip {index + 1} input '{name}' -> '{first.name}'")
                strips.append(VoicemeeterStripDevice(index=index, name=name, choices=choices))
    except _VoicemeeterNotRunning:
        return None
    except Exception as exc:
        if log:
            log(f"warn: Voicemeeter input snapshot skipped: {exc}")
        return None

    if not strips:
        return None
    if log:
        log(f"saved Voicemeeter hardware input selection ({len(strips)} strip(s))")
    return VoicemeeterSnapshot(dll_path=dll_path, strips=tuple(strips))


def restore_voicemeeter_inputs(snapshot: VoicemeeterSnapshot | None, log: LogFn = None) -> bool:
    """restore previously captured Voicemeeter hardware inputs and restart its audio engine."""
    if snapshot is None:
        return True
    if not snapshot.dll_path.is_file():
        if log:
            log("warn: Voicemeeter Remote API DLL disappeared; input restore skipped")
        return False

    restored = 0
    try:
        with _VoicemeeterRemote(snapshot.dll_path) as remote:
            for strip in snapshot.strips:
                if not remote.wait_for_input_device(strip, _DEVICE_SETTLE_TIMEOUT):
                    if log:
                        log(f"warn: Voicemeeter input did not reappear in time: {strip.name}")
                    continue
                restored_choice = remote.set_strip_device(strip)
                if restored_choice:
                    restored += 1
                    if log:
                        log(
                            f"restored Voicemeeter strip {strip.index + 1} -> "
                            f"{restored_choice.driver.upper()}: {restored_choice.name}"
                        )
                elif log:
                    log(f"warn: failed to restore Voicemeeter strip {strip.index + 1}: {strip.name}")
            if restored:
                if remote.restart_audio_engine():
                    if log:
                        log("restarted Voicemeeter audio engine")
                elif log:
                    log("warn: Voicemeeter audio engine restart request failed")
    except Exception as exc:
        if log:
            log(f"warn: Voicemeeter input restore failed: {exc}")
        return False

    return restored == len(snapshot.strips)
