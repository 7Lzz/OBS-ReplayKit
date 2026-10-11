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
		DWORD timeout = (DWORD)timeoutMs;
		setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
		setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		if (connect(sock, (sockaddr *)&addr, sizeof(addr)) == 0) {
			std::string body = jsonBody ? jsonBody : "";
			// the helper only accepts same-origin requests on trusted routes -- pretend to be the dock.
			std::string req = std::string(method) + " " + path + " HTTP/1.1\r\n" + "Host: 127.0.0.1\r\n" +
					   "Origin: http://127.0.0.1:" + std::to_string(port) + "\r\n";
			if (!body.empty()) {
				req += "Content-Type: application/json\r\n";
				req += "Content-Length: " + std::to_string(body.size()) + "\r\n";
			}
			req += "Connection: close\r\n\r\n" + body;
			if (send(sock, req.c_str(), (int)req.size(), 0) > 0) {
				char buf[2048];
				int n;
				while ((n = recv(sock, buf, sizeof(buf), 0)) > 0)
					response.append(buf, n);
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return response;
}

} // namespace rk
