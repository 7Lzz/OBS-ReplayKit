// the security every kernel object of the share window gets: the signed in user and the system only, so no other account can read the frames or drive the window
#pragma once

#include <windows.h>

namespace rk::share {

class UserOnlySecurity {
public:
	UserOnlySecurity();
	~UserOnlySecurity();
	UserOnlySecurity(const UserOnlySecurity &) = delete;
	UserOnlySecurity &operator=(const UserOnlySecurity &) = delete;

	bool Ok() const { return descriptor != nullptr; }
	SECURITY_ATTRIBUTES *Attributes() { return Ok() ? &attributes : nullptr; }

private:
	PSECURITY_DESCRIPTOR descriptor = nullptr;
	SECURITY_ATTRIBUTES attributes{};
};

} // namespace rk::share
