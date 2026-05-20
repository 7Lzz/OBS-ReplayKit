"""Fast process exit helpers for the console setup app."""

from __future__ import annotations

import os
import sys


def _install_noop_handler() -> None:
    return


def _fallback_exit(rc: int = 0) -> None:
    os._exit(rc)


if os.name != "nt":
    fast_exit = _fallback_exit
    install_console_close_handler = _install_noop_handler
else:
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.windll.kernel32

    PROCESS_TERMINATE = 0x0001
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    TH32CS_SNAPPROCESS = 0x00000002
    MAX_PATH = 260
    INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

    class ProcessEntry32W(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_void_p),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", ctypes.c_long),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", wintypes.WCHAR * MAX_PATH),
        ]

    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.GetCurrentProcessId.argtypes = []
    kernel32.GetCurrentProcessId.restype = wintypes.DWORD
    kernel32.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel32.TerminateProcess.restype = wintypes.BOOL
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry32W)]
    kernel32.Process32FirstW.restype = wintypes.BOOL
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry32W)]
    kernel32.Process32NextW.restype = wintypes.BOOL
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.SetConsoleCtrlHandler.argtypes = [ctypes.c_void_p, wintypes.BOOL]
    kernel32.SetConsoleCtrlHandler.restype = wintypes.BOOL

    def _same_path(left: str, right: str) -> bool:
        try:
            return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))
        except (OSError, ValueError):
            return False

    def _parent_pid(pid: int) -> int | None:
        snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if not snapshot or snapshot == INVALID_HANDLE_VALUE:
            return None
        try:
            entry = ProcessEntry32W()
            entry.dwSize = ctypes.sizeof(ProcessEntry32W)
            ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
            while ok:
                if int(entry.th32ProcessID) == pid:
                    return int(entry.th32ParentProcessID)
                ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
            return None
        finally:
            kernel32.CloseHandle(snapshot)

    def _process_path(pid: int) -> str | None:
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return None
        try:
            size = wintypes.DWORD(32768)
            buffer = ctypes.create_unicode_buffer(size.value)
            if not kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
                return None
            return buffer.value
        finally:
            kernel32.CloseHandle(handle)

    def _terminate_pid(pid: int, rc: int) -> None:
        handle = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
        if not handle:
            return
        try:
            kernel32.TerminateProcess(handle, rc)
        finally:
            kernel32.CloseHandle(handle)

    def _terminate_onefile_parent(rc: int) -> None:
        if not getattr(sys, "frozen", False):
            return
        parent = _parent_pid(int(kernel32.GetCurrentProcessId()))
        if not parent:
            return
        parent_path = _process_path(parent)
        if parent_path and _same_path(parent_path, sys.executable):
            _terminate_pid(parent, rc)

    def fast_exit(rc: int = 0) -> None:
        """Exit now, including the PyInstaller onefile parent when present."""
        try:
            _terminate_onefile_parent(rc)
        except Exception:
            pass
        try:
            kernel32.TerminateProcess(kernel32.GetCurrentProcess(), rc)
        except Exception:
            pass
        os._exit(rc)

    _PHANDLER_ROUTINE = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.DWORD)
    _ctrl_handler_ref = None

    def install_console_close_handler() -> None:
        global _ctrl_handler_ref

        def _ctrl_handler(_ctrl_type):
            fast_exit(0)
            return True

        _ctrl_handler_ref = _PHANDLER_ROUTINE(_ctrl_handler)
        kernel32.SetConsoleCtrlHandler(_ctrl_handler_ref, True)
