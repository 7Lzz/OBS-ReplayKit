// frameless cef window that hangs off a title bar button
#pragma once

#include <QColor>
#include <QEasingCurve>
#include <QElapsedTimer>
#include <QList>
#include <QPoint>
#include <QPointer>
#include <QRect>
#include <QSize>
#include <QString>
#include <QVariantAnimation>
#include <QWidget>
#include <functional>

#include "browser-panel.hpp"
#include "core/win32.hpp"

class QJsonArray;

namespace rk {

// a panel that reads as one shape with the button that opened it: the button paints itself as an open tab and the page draws the rest. it is built once and then shown and hidden, since a fresh cef widget per open paints white before the page lands. a Qt::Tool rather than a Qt::Popup, because a popup grabs the mouse and the page inside would never receive a click
class AnchoredPanel : public QObject {
public:
	// which edge of the panel lines up with the same edge of the tab: the bell hangs its panel to the left of it, a badge hangs it to the right
	enum class Edge { Left, Right };

	struct Anchor {
		// identifies the control, so a second press on the same one closes the panel instead of opening it again
		const void *key = nullptr;
		Edge edge = Edge::Right;
		// how many rows of the tab the panel window covers: the bell is a window of its own that stays above the panel and lets the panel start under its last row, the badge is a plain child widget and the panel starts right below it
		int seam = 0;
		// screen rect of the tab, null while the control is not on screen
		std::function<QRect()> tabRect;
		// a press on the control itself is its own toggle, not a click away from the panel; gets the cursor in physical and in qt coordinates
		std::function<bool(const POINT &, const QPoint &)> owns;
		// paints the tab open while the panel shows and closed again when it is gone
		std::function<void(bool)> setOpen;
		// runs as the panel starts to roll up, so the arrow of the control turns back with it the way a page menu turns its chevron, while the tab stays until the panel is gone
		std::function<void()> closing;
		// runs once the panel is hidden
		std::function<void()> onHidden;
		// keeps the control above its panel where it is a window of its own
		std::function<void()> raiseControl;
	};

	AnchoredPanel(QWidget *owner, const QString &title, const QString &url, const QSize &size);
	~AnchoredPanel();
	AnchoredPanel(const AnchoredPanel &) = delete;
	AnchoredPanel &operator=(const AnchoredPanel &) = delete;

	QWidget *Window() const { return m_window; }
	QWidget *Owner() const { return m_owner; }
	const void *AnchorKey() const { return m_anchor.key; }
	// on screen, a roll up in progress included
	bool IsShown() const;
	// on screen and not on its way out
	bool IsOpen() const { return IsShown() && !m_closing; }

	// builds the window and its widget without showing it; the browser inside is only made once the window is shown, which Warm does out of sight
	bool EnsureBuilt();
	// the size the window takes on its next open, for a panel whose content changes with what it shows
	void SetSize(const QSize &size) { m_size = size; }
	// points the page at another address; the pages that take their state from the fragment of it read it again from there, which reaches them even when a script sent to the page is lost
	void SetUrl(const QString &url);
	// opens the panel under the anchor, or closes it when it is already open for that anchor; one open for another anchor gives way at once. the script runs once the panel is up, for the page hook that re-reads its data
	void Toggle(const Anchor &anchor, const QString &script);
	// animated when the user dismisses it, not when the window it hangs off is going away
	void Hide(bool animate);
	// moves the panel back under its tab after the window that holds the tab moved or the tab changed width
	void Follow();
	void RunScript(const QString &script);
	// starts the page of the panel out of sight, so the first open finds one that is already up: the window is shown below every screen until the page says it is up, and hidden again. nothing happens once the page has said so, or while the window the panel hangs off is not on screen
	void Warm();
	bool Warming() const { return m_warming; }
	// the page is up, or the wait for it was given up on
	bool Warmed() const;

	// a window that closes or changes page takes the panels that hang off it along at once
	static void HideOwnedBy(QWidget *owner);
	// how many panels are on screen, for the state the command channel reports
	static int ShownCount();
	// every panel that is showing goes back under its tab; called from the native message loop of the window they hang off, so they keep up while it is dragged or resized
	static void FollowAll();
	// a press on the window frame or caption starts a native move or size loop that no timer runs inside, so the click away watch never sees it; the press itself dismisses the panel here, and a click that is settled by now rolls it up the way any click away does
	static void DismissForPress(bool animate = false);
	static void HideAll(bool animate = false);
	// deletes every panel window and stops the click away watch; runs before cef shuts down
	static void DestroyAll();
	// the colour under the page, so the panel shows its own ground before the first frame instead of white
	static void SetGround(const QColor &color);
	// how the warm-up of every panel stands, for the command channel
	static QJsonArray WarmReport();

private:
	void Finish();
	// puts the panel under its tab from the rectangle windows reports for the obs window; false when the panel does not hang off that window
	bool PlaceFromWindowRect(const QRect &tab);
	void Roll(int toHeight, std::function<void()> done);
	void StopRoll();
	void RollTick();
	// the cut that shows the top of the window while it rolls: the window region, set without a redraw so the page that fills the window is not asked to paint for every step
	void SetReveal(int height);
	void ClearReveal();
	void StartClickAway();
	static void StopClickAway();
	static void DismissTick();
	void OnPageReady();
	void EndWarm();
	void WaitForPage(int run);

	QPointer<QWidget> m_owner;
	QPointer<QWidget> m_window;
	QPointer<QCefWidget> m_browser;
	QString m_title;
	QString m_url;
	QSize m_size;
	Anchor m_anchor;
	int m_tabWidth = -1;
	bool m_closing = false;
	// how much of the window shows while it rolls
	int m_rollHeight = 1;
	QPointer<QTimer> m_rollTimer;
	QElapsedTimer m_rollClock;
	bool m_rollStarted = false;
	int m_rollFrom = 1;
	int m_rollTo = 1;
	int m_rollDuration = 0;
	QEasingCurve m_rollCurve;
	std::function<void()> m_rollDone;
	// how the roll that runs went, for the development log
	int m_rollFrames = 0;
	qint64 m_rollLastMs = 0;
	qint64 m_rollWorstGapMs = 0;
	// the page of the panel has said it is up, the window is up out of sight for it right now, the wait for it was given up on, and its widget could not report its page so the wait is for a fixed time
	bool m_ready = false;
	bool m_warming = false;
	bool m_gaveUp = false;
	bool m_blind = false;
	QElapsedTimer m_warmClock;
	qint64 m_warmMs = -1;
	// counts the warm-ups, so a timer of an earlier one does nothing
	int m_warmRun = 0;
	// an open that came in while the page was still on its way, and the control it came from; it runs when the warm-up ends
	std::function<void()> m_pendingOpen;
	const void *m_pendingKey = nullptr;

	static QList<AnchoredPanel *> s_panels;
	// the panel the click away watch looks after, there is only ever one open
	static AnchoredPanel *s_active;
};

} // namespace rk
