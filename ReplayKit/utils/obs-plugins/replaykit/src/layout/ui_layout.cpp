// classic and clean layout switching
#include "prelude.hpp"
#include "layout/ui_layout.hpp"

#include <QSaveFile>

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "core/window_state.hpp"

namespace rk {

namespace {

// the ring around the preview in the clean layout: nothing on the left and top where it meets the rail and title bar, a thin chrome-coloured border on the other two so the window edge still has a strip to grab for resizing (the preview and the embedded pages are native child windows and swallow the mouse right up to their own edge)
constexpr int kStageLeft = 0;
constexpr int kStageTop = 0;
constexpr int kStageRight = 6;
constexpr int kStageBottom = 6;
QMargins g_savedCentralMargins;
bool g_centralMarginsSaved = false;
QHash<QString, bool> g_previewBarWasShown;

UiLayout LayoutFromString(const QString &value)
{
	return value == QLatin1String("classic") ? UiLayout::Classic : UiLayout::Clean;
}

// reads interfaceLayout straight out of the helper settings file, so a layout picked while obs was closing is already known at startup instead of waiting on the helper. the file is pretty-printed json, so the value is found by key rather than by the compact-shape helpers above. it sits in the replaykit folder; an install from before it moved there keeps it in scripts until the helper has moved it
QString ReadLayoutFromSettingsFile()
{
	const QString folder = qEnvironmentVariable("APPDATA") + "/obs-studio/obs-replayKit/";
	QFile f(folder + "replaykit_settings.json");
	if (!f.exists())
		f.setFileName(folder + "scripts/replaykit_settings.json");
	if (!f.open(QIODevice::ReadOnly) || f.size() > 4 * 1024 * 1024)
		return QString();
	const QByteArray data = f.readAll();
	qsizetype at = data.indexOf("\"interfaceLayout\"");
	if (at < 0)
		return QString();
	at = data.indexOf(':', at);
	if (at < 0)
		return QString();
	at = data.indexOf('"', at);
	if (at < 0)
		return QString();
	const qsizetype end = data.indexOf('"', at + 1);
	if (end < 0)
		return QString();
	return QString::fromUtf8(data.mid(at + 1, end - at - 1));
}

// the panels the clean layout folds away, by the object names obs gives them: the browser dock is its title plus _extraBrowser, and the title was Custom Controls before it was renamed
bool IsCleanFoldedDock(const QString &name)
{
	static const QStringList names = {"scenesDock", "sourcesDock", "mixerDock", "transitionsDock", "controlsDock", "statsDock", "Controls_extraBrowser", "Custom Controls_extraBrowser"};
	return names.contains(name);
}

// the shown flag of every folded-away dock cleared inside a saved QMainWindow state, so obs opens with those docks hidden while each one keeps its place for when the panels are brought back. a dock entry of the state is the marker 0xfb, the object name as a length-prefixed utf-16 string and one flag byte whose lowest bit is shown; anything that does not read like that is left alone. panelShown says whether scenes, sources or mixer was shown
QByteArray HideFoldedDocksInState(QByteArray state, bool *panelShown, const QStringList &only)
{
	*panelShown = false;
	const qsizetype size = state.size();
	const uchar *bytes = reinterpret_cast<const uchar *>(state.constData());
	QList<qsizetype> shownFlags;
	for (qsizetype i = 0; i + 6 < size; ++i) {
		if (bytes[i] != 0xfb)
			continue;
		const quint32 length = (quint32(bytes[i + 1]) << 24) | (quint32(bytes[i + 2]) << 16) | (quint32(bytes[i + 3]) << 8) | quint32(bytes[i + 4]);
		if (length < 2 || length > 256 || length % 2 != 0 || i + 5 + qsizetype(length) >= size)
			continue;
		QString name;
		bool plain = true;
		for (quint32 j = 0; j < length; j += 2) {
			const ushort ch = ushort((bytes[i + 5 + j] << 8) | bytes[i + 6 + j]);
			if (ch < 0x20 || ch > 0x7e) {
				plain = false;
				break;
			}
			name += QChar(ch);
		}
		if (!plain)
			continue;
		const qsizetype flagAt = i + 5 + qsizetype(length);
		if (IsCleanFoldedDock(name) && (only.isEmpty() || only.contains(name)) && (bytes[flagAt] & 1)) {
			if (name == QLatin1String("scenesDock") || name == QLatin1String("sourcesDock") || name == QLatin1String("mixerDock"))
				*panelShown = true;
			shownFlags.append(flagAt);
		}
		i = flagAt;
	}
	for (const qsizetype at : shownFlags)
		state[at] = char(uchar(state[at]) & 0xfe);
	return state;
}

} // namespace

UiLayout g_wantedLayout = UiLayout::Clean;

// the browser dock that carries the ReplayKit controls page. matched strictly first (its own object name or title), and only then by the generic extra-browser suffix, so another custom browser dock is never mistaken for it. the native controlsDock is skipped on purpose: it carries the exact same windowTitle() "Controls" once the rename has run
QDockWidget *FindSidebarDock(QMainWindow *mw)
{
	QDockWidget *nativeDock = mw->findChild<QDockWidget *>("controlsDock");
	QDockWidget *suffixMatch = nullptr;
	for (QDockWidget *dock : mw->findChildren<QDockWidget *>()) {
		if (dock == nativeDock)
			continue;
		QString id = dock->objectName();
		id.remove('-');
		if (id.toLower() == QLatin1String("a59ce0ef5d6f4a4f91d9c7c3c1d4e2b0") ||
		    dock->objectName() == QLatin1String("Controls_extraBrowser") ||
		    dock->windowTitle() == QLatin1String("Custom Controls") ||
		    dock->windowTitle() == QLatin1String("Controls"))
			return dock;
		if (!suffixMatch && dock->objectName().endsWith(QLatin1String("_extraBrowser")))
			suffixMatch = dock;
	}
	return suffixMatch;
}

// everything the clean layout changes that is not a dock: the menu bar, the strip under the preview, and the margin around the stage. safe to call repeatedly and before the window is first shown
void ApplyLayoutChrome(bool clean)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;

	// zero height instead of hidden -- a hidden menu bar stops qt from firing the shortcuts of every action inside its menus
	if (QWidget *bar = mw->menuWidget()) {
		if (clean) {
			bar->setFixedHeight(0);
		} else {
			bar->setMinimumHeight(0);
			bar->setMaximumHeight(QWIDGETSIZE_MAX);
		}
	}

	// the zoom strip and scroll bars under the preview are what make the stage look busy, and obs never re-shows them by itself
	for (const char *name : {"previewXContainer", "previewYScrollBar"}) {
		QWidget *bar = mw->findChild<QWidget *>(name);
		if (!bar)
			continue;
		if (clean) {
			if (!g_previewBarWasShown.contains(name))
				g_previewBarWasShown.insert(name, !bar->isHidden());
			bar->setVisible(false);
		} else if (g_previewBarWasShown.contains(name)) {
			bar->setVisible(g_previewBarWasShown.take(name));
		}
	}

	if (QWidget *central = mw->centralWidget()) {
		if (QLayout *layout = central->layout()) {
			if (!g_centralMarginsSaved) {
				g_savedCentralMargins = layout->contentsMargins();
				g_centralMarginsSaved = true;
			}
			layout->setContentsMargins(clean ? QMargins(kStageLeft, kStageTop, kStageRight, kStageBottom) : g_savedCentralMargins);
		}
	}

}

// the dock half of a layout change. layoutDocksApplied in window_state.ini remembers which layout the docks currently reflect, so switching happens once per real change and never on every launch (obs already persists the dock state itself)
void ApplyLayoutDocks(UiLayout wanted)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	const UiLayout applied = LayoutFromString(ini.value("layoutDocksApplied", "classic").toString());
	QDockWidget *sidebar = FindSidebarDock(mw);

	if (wanted == UiLayout::Clean) {
		if (applied == UiLayout::Classic) {
			// the browser dock is created after cef comes up, and the saved classic arrangement has to include it, so wait for it (a few slow ticks at most, then go on without it)
			static int waitedTicks = 0;
			if (!sidebar && ++waitedTicks < 8)
				return;
			// keep the users own arrangement so Classic can put it back exactly, but only one that has panels in it: a fresh install seeds the clean arrangement, and saving that would make the way back to Classic an empty window, while with nothing saved the switch back lays the panels out in obss default order. isHidden and not isVisible, because the window can still be in the tray
			bool panelsInState = false;
			for (const char *name : {"scenesDock", "sourcesDock", "mixerDock"}) {
				QDockWidget *dock = mw->findChild<QDockWidget *>(name);
				if (dock && !dock->isHidden())
					panelsInState = true;
			}
			if (panelsInState)
				ini.setValue("classicDockState", mw->saveState());
			for (const char *name : {"scenesDock", "sourcesDock", "mixerDock", "transitionsDock", "statsDock"}) {
				if (QDockWidget *dock = mw->findChild<QDockWidget *>(name))
					dock->setVisible(false);
			}
			ini.setValue("layoutDocksApplied", "clean");
			blog(LOG_INFO, "[replaykit] docks switched to the clean layout");
		}
		// the icon rail replaces the controls page, so its dock stays hidden. the browser dock may not exist yet (cef starts late), the slow dock tick calls this again until it does
		if (sidebar && sidebar->isVisible())
			sidebar->setVisible(false);
		return;
	}

	if (applied == UiLayout::Clean) {
		const QByteArray saved = ini.value("classicDockState").toByteArray();
		if (saved.isEmpty() || !mw->restoreState(saved)) {
			// nothing saved to go back to (a fresh install seeded clean), so bring the panels back in obss default order
			for (const char *name : {"scenesDock", "sourcesDock", "mixerDock"}) {
				if (QDockWidget *dock = mw->findChild<QDockWidget *>(name))
					dock->setVisible(true);
			}
			if (sidebar) {
				QDockWidget *mixer = mw->findChild<QDockWidget *>("mixerDock");
				if (mixer)
					mw->splitDockWidget(mixer, sidebar, Qt::Horizontal);
				else
					mw->addDockWidget(Qt::BottomDockWidgetArea, sidebar);
				sidebar->setVisible(true);
			}
		}
		ini.setValue("layoutDocksApplied", "classic");
		blog(LOG_INFO, "[replaykit] docks switched back to the classic layout");
	}
}

// obs restores its dock state while it starts, long before the plugin can act, so the panels of the layout being left showed for the seconds until the plugin loaded. the exit event fires after obs saved user.ini, and the state it wrote can still be turned into the arrangement the next launch wants: the panels folded away for the clean layout, the saved classic arrangement for the way back. obs has already destroyed the browser dock by then, so this works on the saved bytes and not on the live docks; layoutDocksApplied only moves once the file really changed, so a failed write leaves the switch to the runtime path
void PrepareDocksForNextLaunch()
{
	QString value = ReadLayoutFromSettingsFile();
	if (value != QLatin1String("clean") && value != QLatin1String("classic"))
		value = g_wantedLayout == UiLayout::Classic ? QStringLiteral("classic") : QStringLiteral("clean");
	const UiLayout wanted = LayoutFromString(value);
	// this session already runs the layout the next one wants, so obs saved exactly that, except that the sources panel is open while the projector view shows and would come up in every view of the next launch
	const bool sameLayout = wanted == (g_shellOn ? UiLayout::Clean : UiLayout::Classic);
	if (sameLayout && !g_shellOn)
		return;

	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	QFile file(qEnvironmentVariable("APPDATA") + "/obs-studio/user.ini");
	if (!file.open(QIODevice::ReadOnly))
		return;
	QList<QByteArray> lines = file.readAll().split('\n');
	file.close();

	qsizetype dockLine = -1;
	bool inSection = false;
	for (qsizetype i = 0; i < lines.size(); ++i) {
		const QByteArray trimmed = lines[i].trimmed();
		if (trimmed.startsWith('['))
			inSection = trimmed == "[BasicWindow]";
		else if (inSection && trimmed.startsWith("DockState=")) {
			dockLine = i;
			break;
		}
	}
	if (dockLine < 0)
		return;

	const QByteArray current = QByteArray::fromBase64(lines[dockLine].trimmed().mid(10));
	QByteArray next;
	if (wanted == UiLayout::Clean) {
		bool panelShown = false;
		next = HideFoldedDocksInState(current, &panelShown, sameLayout ? QStringList{QStringLiteral("sourcesDock")} : QStringList());
		// the way back is the arrangement the user has now, but only one that has panels in it
		if (panelShown && !sameLayout)
			ini.setValue("classicDockState", current);
	} else {
		next = ini.value("classicDockState").toByteArray();
	}
	if (next.isEmpty() || (sameLayout && next == current))
		return;

	const bool crlf = lines[dockLine].endsWith('\r');
	lines[dockLine] = QByteArray("DockState=") + next.toBase64() + (crlf ? "\r" : "");
	QSaveFile out(file.fileName());
	if (!out.open(QIODevice::WriteOnly))
		return;
	out.write(lines.join('\n'));
	if (!out.commit())
		return;
	if (sameLayout) {
		blog(LOG_INFO, "[replaykit] sources panel closed in the saved docks");
		return;
	}
	ini.setValue("layoutDocksApplied", wanted == UiLayout::Clean ? "clean" : "classic");
	blog(LOG_INFO, "[replaykit] docks prepared for the %s layout of the next launch", wanted == UiLayout::Clean ? "clean" : "classic");
}

// the clean layout owns the window arrangement, so the View and Docks entries that rearrange it are fixed there: the toolbars and status bar pinned to what the stage is laid out for, always on top off since nothing in the clean layout could turn it back off, and every one of them disabled so no shortcut or leftover menu can flip it. runs again on the slow dock tick, since qt re-enables a dock entry whenever obs changes that docks features and browser docks add their entries late. classic needs nothing undone, a layout change always restarts obs
void LockCleanLayoutMenus()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn)
		return;
	// -1 leaves the state as it is and only locks it
	const struct {
		const char *name;
		int pinned;
	} entries[] = {
		{"resetUI", -1},         {"actionSceneListMode", -1}, {"actionSceneGridMode", -1}, {"toggleListboxToolbars", 0},
		{"toggleContextBar", 1}, {"toggleSourceIcons", -1},   {"toggleStatusBar", 0},      {"actionAlwaysOnTop", 0},
		{"lockDocks", -1},       {"sideDocks", -1},           {"resetDocks", -1},
	};
	for (const auto &entry : entries) {
		QAction *action = mw->findChild<QAction *>(entry.name);
		if (!action)
			continue;
		// obs follows always on top through triggered and the others through toggled, and trigger() both flips the check and fires triggered
		if (entry.pinned >= 0 && action->isChecked() != (entry.pinned == 1) && action->isEnabled())
			action->trigger();
		if (action->isEnabled())
			action->setEnabled(false);
	}
	if (QMenu *mode = mw->findChild<QMenu *>(QStringLiteral("sceneListModeMenu")))
		mode->menuAction()->setEnabled(false);
	// every panel entry, Custom Browser Docks and whatever a plugin added to the Docks menu
	if (QMenu *docks = mw->findChild<QMenu *>(QStringLiteral("menuDocks"))) {
		for (QAction *action : docks->actions()) {
			if (!action->isSeparator() && action->isEnabled())
				action->setEnabled(false);
		}
	}
}

void ApplyLayout()
{
	ApplyLayoutChrome(g_shellOn);
	ApplyLayoutDocks(g_shellOn ? UiLayout::Clean : UiLayout::Classic);
	LockCleanLayoutMenus();
}
bool g_shellBootSkipped = false;

// unfinished clean starts in a row that send the next launch to the classic layout
constexpr int kShellBootLimit = 2;

// the settings file wins, the ini copy is only the fallback for a file that cannot be read yet (fresh install, helper never ran)
void InitLayoutAtStartup()
{
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	QString value = ReadLayoutFromSettingsFile();
	if (value != QLatin1String("clean") && value != QLatin1String("classic"))
		value = ini.value("interfaceLayout", "clean").toString();
	g_wantedLayout = LayoutFromString(value);
	ini.setValue("interfaceLayout", g_wantedLayout == UiLayout::Classic ? "classic" : "clean");
	const QColor cachedBg(ini.value("chromeBg").toString());
	const QColor cachedBar(ini.value("chromeBar").toString());
	const QColor cachedInk(ini.value("chromeInk").toString());
	if (cachedBg.isValid())
		g_chromeBg = cachedBg;
	if (cachedBar.isValid())
		g_chromeBar = cachedBar;
	if (cachedInk.isValid())
		g_notifyTipFg = cachedInk;
	// the shell marks its own start and clears the mark a few seconds after obs finishes loading. one start that never got that far proves little, since obs can die early for reasons of its own (a crash inside the windows audio stack was seen five seconds after a launch) and the clean layout would be given up for nothing; so only unfinished starts in a row send this launch to the classic layout instead of crashing the same way again. the saved layout is untouched and the next launch tries the clean one once more
	if (ini.value("shellBootPending", false).toBool()) {
		const int unfinished = ini.value("shellBootUnfinished", 0).toInt() + 1;
		ini.setValue("shellBootPending", false);
		if (unfinished >= kShellBootLimit) {
			ini.setValue("shellBootUnfinished", 0);
			g_shellBootSkipped = true;
			blog(LOG_WARNING, "[replaykit] the last %d clean shell starts never finished, running the classic layout for this launch", unfinished);
		} else {
			ini.setValue("shellBootUnfinished", unfinished);
			blog(LOG_WARNING, "[replaykit] the last clean shell start never finished (%d of %d), trying the clean layout again", unfinished, kShellBootLimit);
		}
	}
}

void ClearShellBootMark()
{
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	ini.setValue("shellBootPending", false);
	ini.setValue("shellBootUnfinished", 0);
}

// the helper settings are the source of truth; a value that is missing or unrecognised (helper down, an older helper) is ignored. nothing is re-laid live: the frame the clean shell needs is fixed for the life of the process, so the new layout waits for the restart the settings page asks for
void ApplyLayoutSetting(const std::string &settingsBody)
{
	const std::string value = ExtractJsonStringField(settingsBody, "interfaceLayout");
	if (value != "clean" && value != "classic")
		return;
	const UiLayout wanted = value == "classic" ? UiLayout::Classic : UiLayout::Clean;
	if (wanted == g_wantedLayout)
		return;
	g_wantedLayout = wanted;
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	ini.setValue("interfaceLayout", value.c_str());
	blog(LOG_INFO, "[replaykit] interface layout will be %s from the next launch", value.c_str());
}

// a layout change swaps the window frame, which qt only does cleanly before the native window exists, so it goes through the same save-and-restart path the theme setting uses
void RestartIntoLayout(const char *layout)
{
	const std::string body = std::string("{\"interfaceLayout\":\"") + layout + "\"}";
	RunAsync([body]() { HttpRequest("POST", "/settings?restart=1", 8767, body.c_str(), 15000); });
}

} // namespace rk
