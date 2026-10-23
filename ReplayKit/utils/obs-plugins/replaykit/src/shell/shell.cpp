// start and stop of the clean shell
#include "prelude.hpp"
#include "shell/shell.hpp"

#include "chrome/title_keybind_badge.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "layout/ui_layout.hpp"
#include "shell/shell_curtain.hpp"
#include "shell/shell_account.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_grip.hpp"
#include "shell/shell_history.hpp"
#include "shell/shell_layout.hpp"
#include "shell/shell_obs_settings.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_rail.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_title_bar.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "tray/tray_menu.hpp"

namespace rk {

namespace {

void PollSharePreview()
{
	RunAsync([]() {
		const std::string body = HttpRequest("GET", "/share-preview", 8767, nullptr, 800);
		if (body.empty())
			return;
		const bool available = JsonBoolField(body, "available", false);
		const bool enabled = JsonBoolField(body, "enabled", false);
		QMetaObject::invokeMethod(
			g_callbacks,
			[available, enabled]() {
				g_sharePreviewAvailable = available;
				if (g_shellRail)
					g_shellRail->SetShare(available, enabled);
			},
			Qt::QueuedConnection);
	});
}

bool g_warmDone = false;
QElapsedTimer g_warmSince;

// the menus of the title bar and the card of the storage start their browsers out of sight, one at a time, once the page of the shell is up and the window is on screen, so the first open of each finds a page that is already drawn; a click that comes sooner waits for it
void ShellWarmStep()
{
	if (g_warmDone || !g_hostReady || !g_shellTitle)
		return;
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !mw->isVisible() || mw->isMinimized())
		return;
	if (!g_warmSince.isValid()) {
		g_warmSince.start();
		return;
	}
	// the page of the shell has the browser to itself for a moment first
	if (g_warmSince.elapsed() < 600)
		return;
	if (g_shellTitle->WarmNext() || StorageModalWarmNext() || NotifyReaderWarmNext())
		return;
	g_warmDone = true;
	blog(LOG_INFO, "[replaykit] the menus and the storage card have their pages up");
}

void ShellTick()
{
	if (g_shellTitle)
		g_shellTitle->RefreshStates();
	const int tick = ++g_shellTickCount;
	ShellWarmStep();
	if (tick % 12 == 0) {
		PollSharePreview();
		ShellProbeHost(false);
	}
	if (tick % 40 == 0 || (tick % 4 == 0 && g_shellRail && g_shellRail->StorageCompressing()))
		ShellStoragePoll();
	// the account: soon after the helper is up, then every half minute, and every second while its menu is open
	if (tick == 8 || tick == 40 || tick % 120 == 0 || (tick % 4 == 0 && g_shellTitle && g_shellTitle->AccountPanelOpen()))
		ShellAccountPoll();
}

} // namespace

// the popup browser was parked and loads its pages again, so the menus are warmed the way they are after a launch
void ShellWarmAgain()
{
	g_warmDone = false;
	g_warmSince.invalidate();
}

// keybind chips, clip length and theme colours arrive on the 1s settings poll
void ShellApplySettings(const std::string &settingsBody)
{
	if (!g_shellOn || !g_shellTitle)
		return;
	g_shellTitle->SetLabels(g_titleRecordLabel, g_titleClipLabel, JsonIntField(settingsBody, "replaySeconds", 0));
	// only the short read of the poll carries it, so a full read of the settings leaves the badge as it is
	if (settingsBody.find("\"updateReady\":") != std::string::npos)
		g_shellTitle->SetUpdateReady(QString::fromStdString(ExtractJsonStringField(settingsBody, "updateReady")));
}

// switches the process into the clean shell. must run before the main window has a native window: a frameless flag set later makes qt recreate it while obs is using it
bool ShellEnableFrame(QMainWindow *mw)
{
	if (!mw)
		return false;
	if (!(mw->windowFlags() & Qt::FramelessWindowHint)) {
		const bool created = mw->testAttribute(Qt::WA_WState_Created);
		const bool wasVisible = mw->isVisible();
		blog(LOG_INFO, "[replaykit] clean shell: making the main window frameless (native window already created: %d, visible: %d)", created, wasVisible);
		mw->setWindowFlag(Qt::FramelessWindowHint, true);
		if (wasVisible)
			mw->show();
	}
	g_shellOn = true;
	return true;
}

// builds the title bar and rail and starts watching the window. called once the frame is enabled, before obs shows the window
void ShellBuild()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn || g_shellTitle)
		return;
	g_shellTitle = new ShellTitleBar(mw);
	g_shellRail = new ShellRail(mw);
	ShellUpdateTitle();
	// the stage starts covered: home is the landing view and its page is still on its way
	QWidget *curtain = new ShellCurtain(mw);
	g_shellCurtain = curtain;
	// made before the grip and the corner, which have to sit above it
	g_shellBar = new ShellProjectorBar(mw);
	g_shellBar->hide();
	g_shellGrip = new ShellGrip(mw);
	g_shellCorner = new ShellCorner(mw);
	ShellProjectorSetup();
	ShellHostFilter *filter = new ShellHostFilter(mw);
	mw->installEventFilter(filter);
	if (QWidget *central = mw->centralWidget())
		central->installEventFilter(filter);
	g_shellFrameFilter = new ShellFrameFilter();
	qApp->installNativeEventFilter(g_shellFrameFilter);
	g_shellTitle->show();
	g_shellRail->show();
	curtain->show();
	ShellBindHwnd();
	ShellLayout();
	blog(LOG_INFO, "[replaykit] clean shell built (%dx%d, title %d, rail %d)", mw->width(), mw->height(), kShellTitleH, kShellRailW);
}

// the timers and app-wide filter that only matter once obs has finished loading
void ShellStart()
{
	if (!g_shellOn || g_shellTimer)
		return;
	g_obsEmbedFilter = new ObsSettingsEmbedFilter();
	qApp->installEventFilter(g_obsEmbedFilter);
	// the arrows of the title bar start from the view the shell opened on
	ShellHistoryStart();
	// the shell has been up long enough to count the start as good, see InitLayoutAtStartup
	QTimer::singleShot(4000, g_callbacks, []() { ClearShellBootMark(); });
	g_shellTimer = new QTimer(g_callbacks);
	QObject::connect(g_shellTimer, &QTimer::timeout, g_callbacks, []() { ShellTick(); });
	g_shellTimer->start(250);
	PollSharePreview();
	ShellStoragePoll();
	// obs restores its docks after the plugin has built the shell, so the sources panel is put right once loading has finished
	ShellProjectorPrepare();
	ShellProjectorApply();
	ShellLayout();
	// after the throwaway browser of PrewarmCefBrowser has taken the first cef surface of the process, which is the one that used to come up half painted
	QTimer::singleShot(800, g_callbacks, []() { ShellEnsureHost(); });
	if (g_shellPendingSetup) {
		g_shellPendingSetup = false;
		QTimer::singleShot(1000, g_callbacks, []() { ShellOpenView(ShellView::Setup, false); });
	}
}

// takes the app-wide filters back out while the code they point into is still loaded
void ShellStop()
{
	if (g_shellTitle)
		g_shellTitle->RestoreTitle();
	if (g_shellTimer)
		g_shellTimer->stop();
	if (g_shellFrameFilter) {
		qApp->removeNativeEventFilter(g_shellFrameFilter);
		delete g_shellFrameFilter;
		g_shellFrameFilter = nullptr;
	}
	if (g_obsEmbedFilter) {
		qApp->removeEventFilter(g_obsEmbedFilter);
		delete g_obsEmbedFilter;
		g_obsEmbedFilter = nullptr;
	}
	ShellHistoryStop();
	if (g_shellTaskbar) {
		g_shellTaskbar->Release();
		g_shellTaskbar = nullptr;
	}
	ShellUncloak();
	g_shellHwnd = nullptr;
}

} // namespace rk
