// blocking http calls to the replaykit helper
#include "prelude.hpp"
#include "core/helper_client.hpp"

namespace rk {

// worker-thread only, it can hold for up to timeoutMs; /focus-window answers 200 whether or not it found a window, so the json body decides
bool HttpFocusWindowSucceeded(const char *path, int port, int timeoutMs)
{
	return HttpRequest("GET", path, port, nullptr, timeoutMs).find("\"focused\":true") != std::string::npos;
}

// blocking request to the replaykit helper over plain winsock, to avoid linking qt6network; reads until the helper closes the connection, and callers substring-search the small, known-shape response (JsonBoolField) instead of pulling in a real json parser for two fields
std::string HttpRequest(const char *method, const char *path, int port, const char *jsonBody, int timeoutMs)
{
	std::string response;
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		return response;

	SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock != INVALID_SOCKET) {
		const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds((std::max)(1, timeoutMs));
		auto remaining = [&]() -> DWORD {
			auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
			return ms > 0 ? (DWORD)ms : 0;
		};

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		u_long nonblocking = 1;
		bool connected = false;
		if (ioctlsocket(sock, FIONBIO, &nonblocking) == 0) {
			int result = connect(sock, (sockaddr *)&addr, sizeof(addr));
			if (result == 0) connected = true;
			else if (WSAGetLastError() == WSAEWOULDBLOCK) {
				fd_set writable, failed;
				FD_ZERO(&writable);
				FD_ZERO(&failed);
				FD_SET(sock, &writable);
				FD_SET(sock, &failed);
				DWORD ms = remaining();
				timeval wait = { (long)(ms / 1000), (long)(ms % 1000) * 1000 };
				if (ms && select(0, nullptr, &writable, &failed, &wait) > 0 && FD_ISSET(sock, &writable)) {
					int error = 0, size = sizeof(error);
					connected = getsockopt(sock, SOL_SOCKET, SO_ERROR, (char *)&error, &size) == 0 && error == 0;
				}
			}
		}
		nonblocking = 0;
		if (connected && ioctlsocket(sock, FIONBIO, &nonblocking) == 0) {
			std::string body = jsonBody ? jsonBody : "";
			// the helper only accepts same-origin requests on trusted routes -- pretend to be the dock.
			std::string req = std::string(method) + " " + path + " HTTP/1.1\r\n" + "Host: 127.0.0.1\r\n" +
					   "Origin: http://127.0.0.1:" + std::to_string(port) + "\r\n";
			if (!body.empty()) {
				req += "Content-Type: application/json\r\n";
				req += "Content-Length: " + std::to_string(body.size()) + "\r\n";
			}
			req += "Connection: close\r\n\r\n" + body;
			size_t sent = 0;
			while (sent < req.size()) {
				DWORD ms = remaining();
				if (!ms || setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&ms, sizeof(ms)) != 0) break;
				int n = send(sock, req.data() + sent, (int)std::min<size_t>(req.size() - sent, 64 * 1024), 0);
				if (n <= 0) break;
				sent += n;
			}
			if (sent == req.size()) {
				char buf[2048];
				constexpr size_t responseLimit = 16 * 1024 * 1024;
				while (true) {
					DWORD ms = remaining();
					if (!ms || setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&ms, sizeof(ms)) != 0) {
						response.clear();
						break;
					}
					int n = recv(sock, buf, sizeof(buf), 0);
					if (n == 0) break;
					if (n < 0 || response.size() + (size_t)n > responseLimit) {
						response.clear();
						break;
					}
					response.append(buf, n);
				}
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return response;
}

} // namespace rk
