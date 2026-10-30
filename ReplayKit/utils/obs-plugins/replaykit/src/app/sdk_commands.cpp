// the commands of the native plugin for the local command channel
#include "prelude.hpp"
#include "app/sdk_commands.hpp"

#include <QApplication>
#include <QDir>
#include <QEnterEvent>
#include <QJsonArray>
#include <QJsonDocument>
#include <QMouseEvent>
#include <QPixmap>
#include <QRegularExpression>
#include <QStandardPaths>
#include <atomic>
#include <map>
#include <mutex>

#include "app/clip_buffer.hpp"
#include "app/capture_context.hpp"
#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/idle_memory.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_title_bar.hpp"
#include "shell/shell_views.hpp"
#include "tray/tray_menu.hpp"
#include "ui/anchored_panel.hpp"
#include "ui/popup_host.hpp"

namespace rk {

namespace {

struct Entry {
	std::string description;
	SdkHandler handler;
};

std::map<std::string, Entry> &Registry()
{
	static std::map<std::string, Entry> registry;
	return registry;
}

std::atomic<bool> g_enabled{false};
std::once_flag g_builtinsOnce;

QJsonObject Fail(const QString &message)
{
	return QJsonObject{{"ok", false}, {"message", message}};
}

const char *ViewName(ShellView view)
{
	switch (view) {
	case ShellView::Home:
		return "home";
	case ShellView::Projector:
		return "projector";
	case ShellView::Clips:
		return "clips";
	case ShellView::Settings:
		return "settings";
	case ShellView::Setup:
		return "setup";
	case ShellView::ObsSettings:
		return "obs-settings";
	}
	return "home";
}

QJsonObject UiState(const QJsonObject &)
{
	QJsonObject out{{"ok", true}, {"shell", g_shellOn}, {"view", QString::fromLatin1(ViewName(g_shellView))}};
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return out;
	const QRect r = mw->geometry();
	out["window"] = QJsonObject{{"x", r.x()}, {"y", r.y()}, {"width", r.width()}, {"height", r.height()}};
	out["visible"] = mw->isVisible();
	out["minimized"] = mw->isMinimized();
	out["maximized"] = mw->isMaximized();
	out["fullscreen"] = mw->isFullScreen();
	out["preview"] = QJsonObject{{"wanted", ShellPreviewWanted()}, {"shown", ShellPreviewIsOn()}, {"studioMode", ShellStudioModeOn()}, {"sceneLocked", ShellSceneLocked()}};
	out["projectorBar"] = g_shellBar && g_shellBar->isVisible();
	out["storageCardOpen"] = StorageModalIsOpen();
	out["panelsOpen"] = AnchoredPanel::ShownCount();
	out["warmUp"] = QJsonObject{{"panels", AnchoredPanel::WarmReport()}, {"storageCard", StorageModalWarmReport()}};
	out["popupHost"] = PopupHost::Probe();
	out["clipBuffer"] = ClipBufferProbe();
	out["hostParked"] = false;
	out["pageHostReady"] = g_hostReady;
	out["setupPending"] = g_setupPending;
	out["setupLocked"] = ShellSetupLocked();
	out["pagePings"] = QJsonObject{{"sent", g_hostPingSent}, {"answered", g_hostPingAcked}, {"proven", g_hostPingProven}, {"misses", g_hostPingMisses}};
	return out;
}

QJsonObject UiView(const QJsonObject &args)
{
	const QString name = args.value("view").toString();
	ShellView view = ShellView::Home;
	if (name == "home")
		view = ShellView::Home;
	else if (name == "projector")
		view = ShellView::Projector;
	else if (name == "clips")
		view = ShellView::Clips;
	else if (name == "settings")
		view = ShellView::Settings;
	else if (name == "setup")
		view = ShellView::Setup;
	else if (name == "obs-settings")
		view = ShellView::ObsSettings;
	else
		return Fail(QStringLiteral("args.view is home, projector, clips, settings, setup or obs-settings."));
	ShellOpenView(view, false);
	return UiState(QJsonObject());
}

QJsonObject UiPopup(const QJsonObject &args)
{
	const QString name = args.value("name").toString();
	if (name == QLatin1String("close")) {
		AnchoredPanel::HideAll();
		StorageModalClose();
		NotifyReaderClose();
		return UiState({});
	}
	QMainWindow *mw = ObsMainWindowQt();
	if (!g_shellTitle || !mw || !mw->isVisible() || mw->isMinimized() || ShellSetupLocked())
		return Fail(QStringLiteral("The shell is not available for a popup."));
	if (name == QLatin1String("storage")) StorageModalOpen();
	else if (name == QLatin1String("reader")) {
		const QString id = args.value("id").toString();
		if (!QRegularExpression(QStringLiteral("\\A[a-zA-Z0-9_.-]{1,80}\\z")).match(id).hasMatch())
			return Fail(QStringLiteral("A valid notification ID is required."));
		NotifyReaderOpen(id.toStdString());
	} else if (!g_shellTitle->TogglePopup(name))
		return Fail(QStringLiteral("args.name is account, notifications, clip, record, storage, reader or close."));
	return UiState({});
}

// shows the window of obs or sends it to the tray the way the tray icon does, so a command that needs it on screen can bring it there and put it back
QJsonObject UiWindow(const QJsonObject &args)
{
	const QString mode = args.value("mode").toString();
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return Fail(QStringLiteral("The window of obs is not there."));
	if (mode == "show") {
		if (!ShellBringMainWindowForward())
			return Fail(QStringLiteral("OBS could not be brought forward."));
	} else if (mode == "hide") {
		if (mw->isVisible())
			ToggleMainWindowShowing();
	} else {
		return Fail(QStringLiteral("args.mode is show or hide."));
	}
	return UiState(QJsonObject());
}

QJsonObject UiBringForward(const QJsonObject &)
{
	const bool broughtForward = ShellBringMainWindowForward();
	QJsonObject result = UiState(QJsonObject());
	result["broughtForward"] = broughtForward;
	return result;
}

// the reload the watchdog does when the page stops answering, run on request so it can be tried without a crash
QJsonObject UiReloadPage(const QJsonObject &)
{
	if (!g_shellHost || !g_shellHost->browser)
		return Fail(QStringLiteral("The shell browser is not there."));
	g_hostReloadTimer.invalidate();
	ShellReloadHost();
	return UiState(QJsonObject());
}

QJsonObject UiPreview(const QJsonObject &args)
{
	if (!args.contains("on"))
		return Fail(QStringLiteral("args.on is required."));
	ShellProjectorSetPreview(args.value("on").toBool());
	return UiState(QJsonObject());
}

QJsonObject UiLock(const QJsonObject &args)
{
	if (!args.contains("on"))
		return Fail(QStringLiteral("args.on is required."));
	ShellProjectorSetLocked(args.value("on").toBool());
	return UiState(QJsonObject());
}

QJsonObject UiStorage(const QJsonObject &args)
{
	if (!args.contains("open"))
		return Fail(QStringLiteral("args.open is required."));
	if (args.value("open").toBool())
		StorageModalOpen();
	else
		StorageModalClose();
	return UiState(QJsonObject());
}

QJsonObject UiStorageProbe(const QJsonObject &)
{
	QJsonObject out = StorageModalProbe();
	out["ok"] = true;
	return out;
}

// the part of the interface a command names: the window itself or one of the pieces of the shell
QWidget *WidgetNamed(const QString &name)
{
	if (name == "window" || name.isEmpty())
		return ObsMainWindowQt();
	if (name == "title")
		return g_shellTitle.data();
	if (name == "rail")
		return g_shellRail.data();
	if (name == "bar")
		return g_shellBar && g_shellBar->isVisible() ? g_shellBar.data() : nullptr;
	if (name == "tip")
		return g_notifyTip && g_notifyTip->isVisible() ? g_notifyTip : nullptr;
	return nullptr;
}

// the tip of the title bar and the rail, shown the way a hover shows it, so it can be looked at beside the tip of a page: the anchor is a rectangle on the screen
QJsonObject UiTip(const QJsonObject &args)
{
	if (args.value("hide").toBool()) {
		if (g_notifyTip) {
			if (args.value("leave").toBool())
				g_notifyTip->Leave();
			else
				g_notifyTip->hide();
		}
		return QJsonObject{{"ok", true}};
	}
	const QString text = args.value("text").toString();
	if (text.isEmpty() || text.size() > 120)
		return Fail(QStringLiteral("args.text is the words of the tip, up to 120 characters."));
	const QString side = args.value("side").toString(QStringLiteral("below"));
	if (side != QLatin1String("below") && side != QLatin1String("right"))
		return Fail(QStringLiteral("args.side is below or right."));
	const QRect anchor(args.value("x").toInt(), args.value("y").toInt(), qMax(1, args.value("width").toInt(1)), qMax(1, args.value("height").toInt(1)));
	if (!g_notifyTip)
		g_notifyTip = new NotifyTip();
	g_notifyTip->ShowFor(text, anchor, side == QLatin1String("right") ? NotifyTip::Side::Right : NotifyTip::Side::Below);
	const QRect r = g_notifyTip->geometry();
	return QJsonObject{{"ok", true}, {"tip", QJsonObject{{"x", r.x()}, {"y", r.y()}, {"width", r.width()}, {"height", r.height()}}}};
}

// where the keyboard is: the window that has the focus of obs, and whether it is the one of the browser of the shell; apply puts it on the browser first, the way a window that comes forward does
QJsonObject UiFocus(const QJsonObject &args)
{
	if (args.value("apply").toBool())
		(void)ShellFocusBrowser();
	QMainWindow *mw = ObsMainWindowQt();
	const HWND focus = GetFocus();
	const HWND foreground = GetForegroundWindow();
	wchar_t name[64] = {};
	if (focus)
		GetClassNameW(focus, name, 64);
	HWND browser = g_shellHost && g_shellHost->browser ? (HWND)g_shellHost->browser->winId() : nullptr;
	return QJsonObject{{"ok", true},
					   {"obsForeground", mw && foreground && GetAncestor(foreground, GA_ROOTOWNER) == (HWND)mw->winId()},
					   {"focusClass", QString::fromWCharArray(name)},
					   {"focusInBrowser", focus && browser && (focus == browser || IsChild(browser, focus))},
					   {"hostOnTop", g_hostOnTop}};
}

// the keyboard focus of obs and the window in front, one line in the obs log for each change, to find out where the keys of a page go. off until the command turns it on
HWINEVENTHOOK g_focusTrace = nullptr;
HWINEVENTHOOK g_foregroundTrace = nullptr;

QString TraceClass(HWND hwnd)
{
	wchar_t name[64] = {};
	GetClassNameW(hwnd, name, 64);
	return QString::fromWCharArray(name);
}

void CALLBACK TraceEvent(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG idObject, LONG, DWORD, DWORD)
{
	if (!hwnd || (idObject != OBJID_CLIENT && idObject != OBJID_WINDOW))
		return;
	if (event == EVENT_SYSTEM_FOREGROUND) {
		DWORD pid = 0;
		GetWindowThreadProcessId(hwnd, &pid);
		blog(LOG_INFO, "[replaykit] focus trace: the window in front is %s of process %lu%s", TraceClass(hwnd).toUtf8().constData(), (unsigned long)pid, pid == GetCurrentProcessId() ? " (obs)" : "");
		return;
	}
	HWND browser = g_shellHost && g_shellHost->browser ? (HWND)g_shellHost->browser->winId() : nullptr;
	QWidget *qt = QApplication::focusWidget();
	blog(LOG_INFO, "[replaykit] focus trace: the keyboard is on %s%s, qt focus is %s", TraceClass(hwnd).toUtf8().constData(),
		 browser && (hwnd == browser || IsChild(browser, hwnd)) ? " (inside the shell browser)" : "", qt ? qt->metaObject()->className() : "none");
}

QJsonObject UiFocusTrace(const QJsonObject &args)
{
	if (args.value("on").toBool(true)) {
		if (!g_focusTrace) {
			g_focusTrace = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, nullptr, TraceEvent, GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT);
			g_foregroundTrace = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, TraceEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
		}
	} else {
		SdkStopFocusTrace();
	}
	return QJsonObject{{"ok", true}, {"on", g_focusTrace != nullptr}};
}

// what a press at a point of the screen would be to the window: the frame, which settles a click and a drag itself, or anything else
QJsonObject UiFrame(const QJsonObject &args)
{
	const POINT screen = {args.value("x").toInt(), args.value("y").toInt()};
	return QJsonObject{{"ok", true}, {"frame", ShellPointIsFrame(screen)}};
}

// puts the pointer on a part of the title bar, the rail or the strip the way a real one would, with the events and without moving the cursor, so the hover states and the tips can be looked at: x and y are inside args.target, and args.on false takes the pointer away again
QJsonObject UiHover(const QJsonObject &args)
{
	const QString target = args.value("target").toString(QStringLiteral("title"));
	QWidget *root = target == QLatin1String("title") || target == QLatin1String("rail") || target == QLatin1String("bar") ? WidgetNamed(target) : nullptr;
	if (!root)
		return Fail(QStringLiteral("args.target is title, rail or bar (while it shows)."));
	const QPoint at(args.value("x").toInt(), args.value("y").toInt());
	if (!root->rect().contains(at))
		return Fail(QStringLiteral("args.x and args.y are outside of the target."));
	QWidget *over = root->childAt(at);
	if (!over)
		over = root;
	const QPoint local = over->mapFrom(root, at);
	const QPoint global = over->mapToGlobal(local);
	if (args.value("on").toBool(true)) {
		QEnterEvent enter(local, over->mapTo(over->window(), local), global);
		QCoreApplication::sendEvent(over, &enter);
		QMouseEvent move(QEvent::MouseMove, local, global, Qt::NoButton, Qt::NoButton, Qt::NoModifier);
		QCoreApplication::sendEvent(over, &move);
	} else {
		QEvent leave(QEvent::Leave);
		QCoreApplication::sendEvent(over, &leave);
	}
	return QJsonObject{{"ok", true}, {"over", QString::fromLatin1(over->metaObject()->className())}};
}

QJsonObject UiSnapshot(const QJsonObject &args)
{
	const QString target = args.value("target").toString(QStringLiteral("window"));
	// the tray menu is drawn without being shown, with the row args.hover in its hover look; the answer lists where each row is
	QJsonArray trayRows;
	QWidget *widget = target == QLatin1String("tray") ? TrayMenuForPicture(args.value("hover").toInt(-1), &trayRows) : WidgetNamed(target);
	if (!widget)
		return Fail(QStringLiteral("args.target is window, title, rail, bar, tip (while it shows) or tray."));
	const QString name = args.value("name").toString(target);
	if (!QRegularExpression(QStringLiteral("^[A-Za-z0-9_-]{1,40}$")).match(name).hasMatch())
		return Fail(QStringLiteral("args.name is letters, digits, - and _, up to 40 of them."));
	const QString dir = QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/sdk");
	QDir().mkpath(dir);
	const QString path = dir + QLatin1Char('/') + name + QStringLiteral(".png");
	const QPixmap pixmap = widget->grab();
	if (!pixmap.save(path, "PNG"))
		return Fail(QStringLiteral("The picture could not be saved."));
	QJsonObject answer{{"ok", true},
			   {"path", path},
			   {"width", pixmap.width()},
			   {"height", pixmap.height()},
			   {"note", QStringLiteral("a grab draws the widgets of the window; the windows of the preview and of the pages are native and stay empty in it")}};
	if (target == QLatin1String("tray"))
		answer["rows"] = trayRows;
	return answer;
}

void DumpTree(QWidget *widget, int depth, int &budget, QJsonObject &node)
{
	node["class"] = QString::fromLatin1(widget->metaObject()->className());
	node["name"] = widget->objectName();
	node["x"] = widget->x();
	node["y"] = widget->y();
	node["width"] = widget->width();
	node["height"] = widget->height();
	node["visible"] = widget->isVisible();
	if (depth <= 0)
		return;
	QJsonArray kids;
	for (QObject *child : widget->children()) {
		QWidget *childWidget = qobject_cast<QWidget *>(child);
		if (!childWidget || childWidget->isWindow())
			continue;
		if (budget <= 0)
			break;
		--budget;
		QJsonObject kid;
		DumpTree(childWidget, depth - 1, budget, kid);
		kids.append(kid);
	}
	if (!kids.isEmpty())
		node["children"] = kids;
}

QJsonObject UiTree(const QJsonObject &args)
{
	QWidget *root = WidgetNamed(args.value("root").toString(QStringLiteral("window")));
	if (!root)
		return Fail(QStringLiteral("args.root is window, title, rail or bar (while it shows)."));
	const int depth = qBound(0, args.value("depth").toInt(2), 6);
	// the window of obs holds hundreds of widgets, so the answer is cut off at a size that stays readable
	int budget = 300;
	QJsonObject tree;
	DumpTree(root, depth, budget, tree);
	return QJsonObject{{"ok", true}, {"tree", tree}, {"truncated", budget <= 0}};
}

QJsonObject UiTrayIcon(const QJsonObject &)
{
	return TrayIconReport();
}

// a way to try an icon without touching the saved setting: args.path puts that image on every surface for now, an empty path or - puts the default back; the helper pushes the saved one again when it connects or the setting changes
QJsonObject UiAppIcon(const QJsonObject &args)
{
	if (args.contains("path"))
		ApplyAppIcon(args.value("path").toString());
	return TrayIconReport();
}

QJsonObject LogMark(const QJsonObject &args)
{
	const QString text = args.value("text").toString().left(200);
	blog(LOG_INFO, "[replaykit] sdk mark: %s", text.toUtf8().constData());
	return QJsonObject{{"ok", true}};
}

QJsonObject Commands(const QJsonObject &)
{
	QJsonArray list;
	for (const auto &pair : Registry())
		list.append(QJsonObject{{"name", QStringLiteral("plugin.") + QString::fromStdString(pair.first)}, {"description", QString::fromStdString(pair.second.description)}});
	return QJsonObject{{"ok", true}, {"commands", list}};
}

void RegisterBuiltins()
{
    SdkRegister("capture.state", "reports capture attribution sampling without exposing process paths", [](const QJsonObject &) { auto out = CaptureContextProbe(); out["ok"] = true; return out; });
	SdkRegister("commands", "the commands of the native plugin", Commands);
	SdkRegister("ui.state", "the view, the window, the preview and its lock, the storage card and how many menus are open", UiState);
	SdkRegister("memory.state", "committed and resident memory before and after the last hidden-window cleanup", [](const QJsonObject &) { return IdleMemory::Probe(); });
	SdkRegister("ui.view", "shows a view: args.view is home, projector, clips, settings, setup or obs-settings", UiView);
	SdkRegister("ui.popup", "opens a shell popup using args.name: account, notifications, clip, record, storage, reader (args.id) or close; reports the shared surface", UiPopup);
	SdkRegister("ui.window", "shows the window of obs or sends it to the tray: args.mode is show or hide", UiWindow);
	SdkRegister("ui.bring-forward", "restores and raises the obs window, and reports whether it became visible", UiBringForward);
	SdkRegister("ui.reload-page", "reloads the page of the shell browser the way the watchdog does when it stops answering", UiReloadPage);
	SdkRegister("ui.preview", "turns the preview on or off: args.on", UiPreview);
	SdkRegister("ui.lock", "locks or unlocks the scene in the preview: args.on", UiLock);
	SdkRegister("ui.storage", "opens or closes the storage card: args.open", UiStorage);
	SdkRegister("ui.storage-probe", "says whether the open storage card shows nothing, and how many times it was built again for that", UiStorageProbe);
	SdkRegister("ui.tip", "shows the tip of the title bar and the rail: args.text, the anchor as args.x, args.y, args.width and args.height on the screen, args.side below or right; args.hide takes it away, and with args.leave it goes the way it does when the pointer leaves", UiTip);
	SdkRegister("ui.focus", "says where the keyboard is: whether obs is in front, which window has the focus and whether it is the browser of the shell; args.apply puts it on the browser first", UiFocus);
	SdkRegister("ui.focus-trace", "writes every change of the keyboard focus inside obs and of the window in front to the obs log: args.on, true when left out, false turns it off", UiFocusTrace);
	SdkRegister("ui.frame", "says whether a press at the screen point args.x and args.y would be on the caption or the sizing frame of the obs window", UiFrame);
	SdkRegister("ui.hover", "puts the pointer on args.target (title, rail or bar) at args.x and args.y, or takes it away with args.on false; only events are sent, the cursor does not move", UiHover);
	SdkRegister("ui.snapshot", "saves a picture of the widgets of args.target (window, title, rail, bar, the tip while it shows, or tray for the tray menu with the row args.hover hovered) to the temp folder of replaykit; args.name names the file", UiSnapshot);
	SdkRegister("ui.tree", "the widgets under args.root (window, title, rail or bar) with their places, args.depth levels deep", UiTree);
	SdkRegister("ui.tray-icon", "the icon on the tray right now: whether it wears the red dot, whether it is one of ours, and what obs put on it for each state", UiTrayIcon);
	SdkRegister("ui.app-icon", "puts the image args.path on the window, the taskbar and the tray for now without saving it (an empty path or - puts the default back), and says what each surface wears: a fingerprint and the red pixels of the window, the taskbar button, the application, the tray and the logo of the title bar", UiAppIcon);
	SdkRegister("log.mark", "writes args.text to the obs log, to find a moment again", LogMark);
}

// the answer goes back to the helper on a thread of its own, since the call blocks until the helper has taken it
void Reply(int id, const QJsonObject &result)
{
	const std::string body = QJsonDocument(result).toJson(QJsonDocument::Compact).toStdString();
	RunAsync([id, body]() {
		const std::string path = "/sdk/reply?id=" + std::to_string(id);
		HttpRequest("POST", path.c_str(), 8767, body.c_str(), 4000);
	});
}

QJsonObject RunCommand(const std::string &name, const std::string &argsText)
{
	if (!g_enabled.load())
		return Fail(QStringLiteral("SDK mode is off."));
	QJsonParseError error;
	const QJsonDocument doc = QJsonDocument::fromJson(QByteArray::fromStdString(argsText), &error);
	if (error.error != QJsonParseError::NoError || !doc.isObject())
		return Fail(QStringLiteral("The arguments are not a json object."));
	const auto found = Registry().find(name);
	if (found == Registry().end())
		return Fail(QStringLiteral("Unknown command: plugin.") + QString::fromStdString(name));
	return found->second.handler(doc.object());
}

} // namespace

void SdkRegister(const char *name, const char *description, SdkHandler handler)
{
	Registry()[name] = Entry{description, std::move(handler)};
}

void SdkStopFocusTrace()
{
	if (g_focusTrace)
		UnhookWinEvent(g_focusTrace);
	if (g_foregroundTrace)
		UnhookWinEvent(g_foregroundTrace);
	g_focusTrace = nullptr;
	g_foregroundTrace = nullptr;
}

void SdkSetEnabled(bool on)
{
	g_enabled.store(on);
}

bool SdkEnabled()
{
	return g_enabled.load();
}

void SdkDispatch(const std::string &rest)
{
	std::call_once(g_builtinsOnce, []() { RegisterBuiltins(); });
	// an id of up to nine digits, then the name, then the arguments
	const size_t first = rest.find(' ');
	if (first == std::string::npos || first == 0 || first > 9)
		return;
	for (size_t i = 0; i < first; ++i) {
		if (rest[i] < '0' || rest[i] > '9')
			return;
	}
	const int id = std::atoi(rest.substr(0, first).c_str());
	const size_t second = rest.find(' ', first + 1);
	const std::string name = rest.substr(first + 1, second == std::string::npos ? std::string::npos : second - first - 1);
	const std::string argsText = second == std::string::npos ? std::string("{}") : rest.substr(second + 1);
	if (name.empty() || name.size() > 64 || argsText.size() > 8192) {
		Reply(id, Fail(QStringLiteral("Bad command.")));
		return;
	}
	QMetaObject::invokeMethod(g_callbacks, [id, name, argsText]() { Reply(id, RunCommand(name, argsText)); }, Qt::QueuedConnection);
}

} // namespace rk
