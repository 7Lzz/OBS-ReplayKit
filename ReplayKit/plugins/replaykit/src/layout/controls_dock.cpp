// places our controls dock, hides the native one and keeps the dock title renamed
#include "prelude.hpp"
#include "layout/controls_dock.hpp"

#include <QSaveFile>

#include "core/shared_values.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "layout/ui_layout.hpp"

namespace rk {

// our browser dock replaces obss native "Controls" dock (Start Streaming / Recording / ...). place ours in the native docks slot, hide the native one, and force ours to the plain title kControlsDockTitle. runs on a slow forever-timer because obs re-titles the dock after the browser loads and re-shows the native dock from its stored state. never close() an obs dock -- OBSDock::closeEvent pops the "Closing Dockable Window" box; toggleViewAction / setVisible do not. the persistent config rename is PatchBrowserDockTitleInUserIni().
void ApplyControlsDockTweaks()
{
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	auto *main = qobject_cast<QMainWindow *>(mw);
	QDockWidget *nativeDock = mw->findChild<QDockWidget *>("controlsDock");

	// obs names an extra-browser dock "<title>_extraBrowser" (confirmed in the obs log), not by uuid -- FindSidebarDock skips nativeDock explicitly becuase it carries the exact same windowTitle() as ourDock once the rename near the bottom of this function has run once, and findChildren()s enumeration order, not anything meaningful, would otherwise decide which of the two this resolves to.
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
	const QString title = QString::fromLatin1(kControlsDockTitle);
	if (ourDock && ourDock->windowTitle() != title) {
		ourDock->setWindowTitle(title);
		if (QWidget *tb = ourDock->titleBarWidget()) {
			for (QLabel *lbl : tb->findChildren<QLabel *>())
				if (lbl->text().contains(QLatin1String(kControlsDockLegacyTitle)))
					lbl->setText(title);
		}
	}
}

// the visible title bar is handled live by ApplyControlsDockTweaks; this makes the rename stick in obss own config (the "Custom Browser Docks" dialog + the dock name obs creates it with next launch). called from the EXIT event, which fires AFTER OBSBasic::closeEvent has already written user.ini, so this edit lands last and survives. written through a temp file, since a user.ini cut short is the whole config of obs gone
void PatchBrowserDockTitleInUserIni()
{
	const QString path = ObsUserIniPath();
	QFile f(path);
	if (!f.open(QIODevice::ReadOnly | QIODevice::Text))
		return;
	const QString text = QString::fromUtf8(f.readAll());
	f.close();
	// only an ExtraBrowserDocks line that holds our uuid (the setup writes it with its dashes, the id of the dock has none) and only the exact stale title token; every such line, since obs reads the last of a key that is there twice
	const QString uuid = QString::fromLatin1(kControlsDockUuid);
	const QString bareUuid = QString(uuid).remove(QLatin1Char('-'));
	const QString legacy = QString::fromLatin1(kControlsDockLegacyTitle);
	const QString title = QString::fromLatin1(kControlsDockTitle);
	QStringList lines = text.split('\n');
	bool changed = false;
	for (QString &line : lines) {
		if (!line.startsWith(QLatin1String("ExtraBrowserDocks=")) || !(line.contains(uuid, Qt::CaseInsensitive) || line.contains(bareUuid, Qt::CaseInsensitive)))
			continue;
		const QString before = line;
		line.replace(QStringLiteral("\"title\": \"%1\"").arg(legacy), QStringLiteral("\"title\": \"%1\"").arg(title));
		line.replace(QStringLiteral("\"title\":\"%1\"").arg(legacy), QStringLiteral("\"title\":\"%1\"").arg(title));
		changed = changed || line != before;
	}
	if (!changed)
		return;
	QSaveFile w(path);
	const QByteArray bytes = lines.join('\n').toUtf8();
	if (!w.open(QIODevice::WriteOnly | QIODevice::Text) || w.write(bytes) != bytes.size() || !w.commit()) {
		blog(LOG_WARNING, "[replaykit] the browser dock could not be renamed in user.ini");
		return;
	}
	blog(LOG_INFO, "[replaykit] renamed the browser dock to '%s' in user.ini", kControlsDockTitle);
}

QTimer *g_dockTweakTimer = nullptr;

} // namespace rk
