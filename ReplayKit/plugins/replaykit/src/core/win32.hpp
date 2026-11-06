// windows headers in one place so winsock2.h always lands before windows.h, whichever file needs a win32 type first
#pragma once

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <dwmapi.h>
#include <shlobj.h>   // SHGetPropertyStoreForWindow
#include <shobjidl.h> // ITaskbarList
