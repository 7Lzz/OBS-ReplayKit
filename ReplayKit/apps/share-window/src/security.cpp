#include <windows.h>
#include <sddl.h>

#include <string>
#include <vector>

#include "security.hpp"

namespace rk::share {

UserOnlySecurity::UserOnlySecurity()
{
	HANDLE token = nullptr;
	if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
		return;
	DWORD needed = 0;
	GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
	std::vector<BYTE> buffer(needed);
	bool ok = needed && GetTokenInformation(token, TokenUser, buffer.data(), needed, &needed);
	CloseHandle(token);
	if (!ok)
		return;
	LPWSTR sid = nullptr;
	if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER *>(buffer.data())->User.Sid, &sid))
		return;
	// an admin obs runs as the same user, so the user entry also covers it
	std::wstring sddl = L"D:P(A;;GA;;;" + std::wstring(sid) + L")(A;;GA;;;SY)";
	LocalFree(sid);
	if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) {
		descriptor = nullptr;
		return;
	}
	attributes.nLength = sizeof(attributes);
	attributes.lpSecurityDescriptor = descriptor;
	attributes.bInheritHandle = FALSE;
}

UserOnlySecurity::~UserOnlySecurity()
{
	if (descriptor)
		LocalFree(descriptor);
}

} // namespace rk::share
