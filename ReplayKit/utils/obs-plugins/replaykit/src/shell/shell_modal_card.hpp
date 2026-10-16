// a card drawn over a dimmed obs window, the window the storage card and the notification reader live in
#pragma once

#include <QElapsedTimer>
#include <QJsonObject>
#include <QPointer>
#include <QString>
#include <QWidget>
#include <functional>
#include <string>

class QCefWidget;

namespace rk {

struct ModalCardSpec {
	// how the card is named in the obs log
	const char *name;
	QString windowTitle;
	QString url;
	int width;
	int height;
	// run in the page each time the card opens, unless an open names its own
	std::string showScript;
	std::string hideScript;
	// after the card closed, for what the view under it shows of what was done in it
	std::function<void()> onClosed;
};

class ModalCard {
public:
	explicit ModalCard(ModalCardSpec spec);

	// builds the windows; the page of the card starts with the warm-up of the shell, or with the first open
	void Prepare();
	// starts the page of the card out of sight, so the first open finds it drawn; true while that is still to come or on its way
	bool WarmNext();
	QJsonObject WarmReport() const;
	bool IsOpen() const;
	// the obs window moved or changed size: the dim and the card go with it, placed from the window rectangle windows reports
	void Follow();
	// showScript replaces the one of the spec for this open
	void Open(const std::string &showScript = std::string());
	void Close();
	// whether the card is open and shows nothing, and how many times it was built again for that
	QJsonObject Probe();
	// deletes the windows; runs before cef shuts down
	void Destroy();

	void OnPagePressedOutside() { Close(); }
	void OnHostMoved();
	void OnHostGone() { Close(); }

private:
	bool EnsureBuilt();
	void Sync();
	void FollowWindowRect();
	void RunScript(const std::string &script);
	void StyleCard();
	void EndCardWarm();
	void OnCardPageReady();
	void StartCardWarm();
	bool CardLooksEmpty();
	void CheckCardPainted(int run);
	void OpenFresh();

	ModalCardSpec m_spec;
	QPointer<QWidget> m_curtain;
	QPointer<QWidget> m_card;
	QPointer<QCefWidget> m_browser;
	QObject *m_hostFilter = nullptr;
	// the page of the card gets the warm-up the menus of the title bar get: the browser inside a cef widget is only made when the widget is first shown, so the card is shown out of sight until its page says it is up. an open that comes before that waits for it
	bool m_ready = false;
	bool m_warming = false;
	bool m_gaveUp = false;
	bool m_blind = false;
	bool m_openWaits = false;
	std::string m_waitingScript;
	// what the last open showed, so a card built again after opening empty shows it again
	std::string m_lastScript;
	int m_run = 0;
	qint64 m_warmMs = -1;
	int m_heals = 0;
	QElapsedTimer m_clock;
};

} // namespace rk
