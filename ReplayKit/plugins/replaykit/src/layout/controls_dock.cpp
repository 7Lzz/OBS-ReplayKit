// places our controls dock, hides the native one and keeps the dock title renamed
#include "prelude.hpp"
#include "layout/controls_dock.hpp"

#include "core/state.hpp"
#include "core/window_state.hpp"
#include "layout/ui_layout.hpp"

namespace rk {

// our browser dock replaces obss native "Controls" dock (Start Streaming / Recording / ...). place ours in the native docks slot, hide the native one, and force ours to the plain title "Controls". runs on a slow forever-timer because obs re-titles the dock after the browser loads and re-shows the native dock from its stored state. NEVER close() an obs dock -- OBSDock::closeEvent pops the "Closing Dockable Window" box; toggleViewAction / setVisible do not. the persistent config rename is PatchBrowserDockTitleInUserIni().
void ApplyControlsDockTweaks()
{
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	auto *main = qobject_cast<QMainWindow *>(mw);
	QDockWidget *nativeDock = mw->findChild<QDockWidget *>("controlsDock");

	// obs names an extra-browser dock "<title>_extraBrowser" (confirmed in the obs log), not by uuid -- FindSidebarDock skips nativeDock explicitly becuase it carries the exact same windowTitle() "Controls" as ourDock once the rename near the bottom of this function has run once, and findChildren()s enumeration order, not anything meaningful, would otherwise decide which of the two this resolves to.
	QDockWidget *ourDock = main ? FindSidebarDock(main) : nullptr;

	// the clean layout keeps this dock hidden behind the icon rail and re-asserts that on the slow tick, because obs Reset UI restores its own stock layout; the classic first-run placement below only applies to the classic layout
	QSettings dockPlacedState(WindowStateIniPath(), QSettings::IniFormat);
	if (g_shellOn) {
		// also what finishes a pending switch to the clean layout: it holds off until this dock exists, see ApplyLayoutDocks
		if (main)
			ApplyLayoutDocks(UiLayout::Clean);
		LockCleanLayoutMenus();
	} else if (main && ourDock && !dockPlacedState.value("controlsDockPlaced", false).toBool()) {
		// only ever once, on a clean install -- not every obs launch, and not a repeating poll, since some users deliberately move or resize this dock afterward and either would eventually fight that. persisted in window_state.ini instead of an in-memory flag so it survives obs restarts; it only ever resets when a clean reinstall wipes obs-replayKit/ (this file included) and a fresh one gets written.
		if (ourDock->isFloating() || main->dockWidgetArea(ourDock) != Qt::BottomDockWidgetArea) {
			ourDock->setFloating(false);
			QDockWidget *anchor = mw->findChild<QDockWidget *>("mixerDock");
			if (!anchor || !anchor->isVisible())
				anchor = mw->findChild<QDockWidget *>("sourcesDock");
			if (anchor && anchor->isVisible())
				main->splitDockWidget(anchor, ourDock, Qt::Horizontal);
			else
				main->addDockWidget(Qt::BottomDockWidgetArea, ourDock);
			ourDock->show();
			ourDock->raise();
		}
		static const int kMinSaneDockWidth = 150;
		static const int kDefaultDockWidth = 400;
		if (!ourDock->isFloating() && ourDock->width() < kMinSaneDockWidth) {
			int before = ourDock->width();
			int w = (nativeDock && nativeDock->width() >= kMinSaneDockWidth) ? nativeDock->width() : kDefaultDockWidth;
			main->resizeDocks({ ourDock }, { w }, Qt::Horizontal);
			blog(LOG_INFO, "[replaykit] Controls dock width was %d on first launch (below the %d floor), resized to %d", before, kMinSaneDockWidth, w);
		}
		dockPlacedState.setValue("controlsDockPlaced", true);
	}

	// hide the native Controls dock (the bundled DockState also hides it; this covers old installs + any re-show)
	if (nativeDock && nativeDock->isVisible()) {
		if (QAction *a = nativeDock->toggleViewAction(); a && a->isChecked())
			a->setChecked(false); // hides it + unchecks the Docks-menu entry, no close popup
		else
			nativeDock->setVisible(false);
	}

	// force the plain title
	if (ourDock && ourDock->windowTitle() != QLatin1String("Controls")) {
		ourDock->setWindowTitle(QStringLiteral("Controls"));
		if (QWidget *tb = ourDock->titleBarWidget()) {
			for (QLabel *lbl : tb->findChildren<QLabel *>())
				if (lbl->text().contains(QLatin1String("Custom Controls")))
					lbl->setText(QStringLiteral("Controls"));
		}
	}
}

// the visible title bar is handled live by ApplyControlsDockTweaks; this makes the rename stick in obss own config (the "Custom Browser Docks" dialog + the dock name obs creates it with next launch). called from the EXIT event, which fires AFTER OBSBasic::closeEvent has already written user.ini, so this edit lands last and survives.
void PatchBrowserDockTitleInUserIni()
{
	QString path = qEnvironmentVariable("APPDATA") + "/obs-studio/user.ini";
	QFile f(path);
	if (!f.open(QIODevice::ReadOnly | QIODevice::Text))
		return;
	QString text = QString::fromUtf8(f.readAll());
	f.close();
	// only the ExtraBrowserDocks line, only when it is our uuids entry, only the exact stale title token.
	QStringList lines = text.split('\n');
	bool changed = false;
	for (QString &line : lines) {
		if (!line.startsWith(QLatin1String("ExtraBrowserDocks=")))
			continue;
		if (!line.contains(QLatin1String("a59ce0ef5d6f4a4f91d9c7c3c1d4e2b0")))
			break;
		QString before = line;
		line.replace(QLatin1String("\"title\": \"Custom Controls\""), QLatin1String("\"title\": \"Controls\""));
		line.replace(QLatin1String("\"title\":\"Custom Controls\""), QLatin1String("\"title\":\"Controls\""));
		changed = (line != before);
		break;
	}
	if (!changed)
		return;
	QFile w(path);
	if (w.open(QIODevice::WriteOnly | QIODevice::Text)) {
		w.write(lines.join('\n').toUtf8());
		w.close();
		blog(LOG_INFO, "[replaykit] renamed the browser dock to 'Controls' in user.ini");
	}
}
QTimer *g_dockTweakTimer = nullptr;

} // namespace rk
