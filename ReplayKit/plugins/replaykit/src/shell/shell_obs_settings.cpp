// folds the obs settings dialog into the main window
#include "prelude.hpp"
#include "shell/shell_obs_settings.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_obs_nav.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "windows/obs_settings.hpp"

namespace rk {

namespace {

// a hidden modal dialog for as long as the folded in settings dialog lives. obs will not hide the window to the tray while any modal dialog exists, and this dialog was made non modal so it can live inside the window, which takes that protection away: a hide from the tray icon left obs holding a pointer to the settings dialog that its exec() frees on return, and the next show from the tray went through it
QPointer<QDialog> s_settingsGuard;

void ObsSettingsGuard(bool on)
{
	if (!on) {
		delete s_settingsGuard.data();
		return;
	}
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || s_settingsGuard)
		return;
	QDialog *guard = new QDialog(mw);
	guard->setObjectName(QStringLiteral("rkObsSettingsGuard"));
	guard->setModal(true);
	s_settingsGuard = guard;
}

// what obs greys out while any output runs (OBSBasicSettings load code); it reads that state once, when it builds the dialog
const char *const kLockedWhileOutputsRun[] = {"language", "videoPage", "outputMode", "outputModeLabel", "simpleOutStrEncoderLabel", "simpleOutStrEncoder", "simpleOutStrAEncoderLabel", "simpleOutStrAEncoder", "simpleRecordingGroupBox", "advOutTopContainer", "advOutRecTopContainer", "advOutRecTypeContainer", "advOutputAudioTracksTab", "advNetworkGroupBox", "sampleRate", "channelSetup", "advancedVideoContainer"};

// watches the reparented dialog so the rail goes back to Home when it closes (its own OK / Cancel / X all end in hide)
class ObsSettingsWatch : public QObject {
public:
	explicit ObsSettingsWatch(QDialog *dlg) : QObject(dlg), m_dlg(dlg)
	{
		QWidget *page = dlg->findChild<QWidget *>(QStringLiteral("videoPage"));
		m_outputsRun = page && !page->isEnabled();
		// clipping can be turned off or on from the window while the dialog stays open, and the disk buffer is an output of its own that sends no frontend event, so the state is polled
		QTimer *poll = new QTimer(this);
		QObject::connect(poll, &QTimer::timeout, this, [this]() { FollowOutputs(); });
		poll->start(500);
	}

protected:
	bool eventFilter(QObject *, QEvent *event) override
	{
		if (event->type() == QEvent::Paint) {
			if (!m_painted) {
				m_painted = true;
				// the flush of this first paint follows right after this event, so the lift is queued behind it
				QTimer::singleShot(0, g_callbacks, []() {
					blog(LOG_INFO, "[replaykit] obs settings first paint, %lld ms after the request", g_obsBuildTimer.isValid() ? (long long)g_obsBuildTimer.elapsed() : -1LL);
					g_obsBuildTimer.invalidate();
					g_obsSettingsPainted = true;
					if (g_shellView == ShellView::ObsSettings)
						ShellSurfaceView();
				});
			}
			return false;
		}
		if (event->type() != QEvent::Hide)
			return false;
		QTimer::singleShot(0, g_callbacks, []() {
			// a dialog hidden along with its host is only parked; a hide of the dialog itself is the close
			if (g_obsSettingsDialog && !g_obsSettingsDialog->isHidden())
				return;
			g_obsSettingsPainted = false;
			g_obsSettingsDialog = nullptr;
			ObsSettingsGuard(false);
			if (g_shellView == ShellView::ObsSettings)
				ShellActivate(ShellView::Home);
		});
		return false;
	}

private:
	void FollowOutputs()
	{
		const bool run = obs_video_active();
		if (run == m_outputsRun)
			return;
		m_outputsRun = run;
		for (const char *name : kLockedWhileOutputsRun)
			if (QWidget *widget = m_dlg->findChild<QWidget *>(QString::fromLatin1(name)))
				widget->setEnabled(!run);
		// the note obs puts on the video page while outputs run; any other note there (an invalid resolution) is left alone
		if (QLabel *note = m_dlg->findChild<QLabel *>(QStringLiteral("videoMsg"))) {
			const QString busy = QString::fromUtf8(obs_frontend_get_locale_string("Basic.Settings.Video.CurrentlyActive"));
			if (!run && note->text() == busy)
				note->clear();
			else if (run && note->text().isEmpty())
				note->setText(busy);
		}
		blog(LOG_INFO, "[replaykit] obs settings follow the outputs: %s", run ? "running, output settings locked" : "stopped, output settings unlocked");
	}
	QDialog *m_dlg;
	bool m_outputsRun = false;
	bool m_painted = false;
};

// QWidget::create is protected, and a pointer to it taken thru a subclass is the way c++ allows calling it on another widget; for a plain child it only marks the widget created, the same step a show of its parent takes
struct WidgetCreateAccess : QWidget {
	static void Create(QWidget *widget) { (widget->*&WidgetCreateAccess::create)(0, true, true); }
};

// runs synchronously from the first polish of the dialog, which qt sends from inside its show() after the window is created but before it counts as visible. that is the one moment a dialog can change parent without qt hiding it, and a hide would end the exec() loop obs is sitting in (the dialog would be gone the instant it appeared)
void ShellEmbedObsSettings(QDialog *dlg)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	if (!g_viewObsHost) {
		QWidget *host = new QWidget(mw);
		host->setAttribute(Qt::WA_NativeWindow, true);
		host->setAutoFillBackground(true);
		QPalette pal = host->palette();
		pal.setColor(QPalette::Window, g_chromeBg);
		host->setPalette(pal);
		QHBoxLayout *layout = new QHBoxLayout(host);
		layout->setContentsMargins(0, 0, 0, 0);
		layout->setSpacing(0);
		host->hide();
		g_viewObsHost = host;
	}
	g_obsSettingsDialog = dlg;
	g_obsSettingsPainted = false;
	ObsSettingsGuard(true);
	QObject::connect(dlg, &QObject::destroyed, g_callbacks, []() { ObsSettingsGuard(false); });
	// connected after the one obs made, so its own handling of the click runs first and this one puts the preview it switched off back
	if (QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray()) {
		QObject::connect(tray, &QSystemTrayIcon::activated, dlg, [](QSystemTrayIcon::ActivationReason reason) {
			if (reason == QSystemTrayIcon::Trigger)
				ShellTrayClickWithObsSettings();
		});
	}
	// a dialog marked native (asking for its window handle does that) keeps its own top level window thru the reparent: windows leaves it where it was, qt logs that it could not set the geometry, and every dropdown, menu and tooltip of the dialog is placed from that stray window instead of from where the dialog is drawn; unmarked, qt turns it into a plain child of the host
	dlg->setAttribute(Qt::WA_NativeWindow, false);
	dlg->setParent(g_viewObsHost, Qt::Widget);
	// obs runs this dialog with exec(), which makes it application modal, and qt then disables every other window of the app. folded into the main window that would disable the window it lives in, so nothing else could be clicked while it is open
	dlg->setWindowModality(Qt::NonModal);
	if (QWindow *handle = dlg->windowHandle())
		handle->setModality(Qt::NonModal);
	// without this the showEvent of the dialog centres it over its parent, which for a child means an offset the host layout never asked for
	dlg->setAttribute(Qt::WA_Moved, true);
	// qt clears the created state of a top level that becomes a child, and the show this runs inside never sets it again while the host is hidden; a widget that is not created only stores a new geometry and gets no resize event, so the dialog kept the layout it opened with however the window was resized, until a park and return showed the host again
	WidgetCreateAccess::Create(dlg);
	// the corner grip of the window does the resizing; the grip of the dialog sat on top of it in the same corner
	dlg->setSizeGripEnabled(false);
	QHBoxLayout *row = static_cast<QHBoxLayout *>(g_viewObsHost->layout());
	// the list of pages is replaced by the nav column, drawn like the replaykit settings and leading back to them. the dialog keeps its own list, hidden, and keeps deciding which page shows
	if (QListWidget *pages = dlg->findChild<QListWidget *>(QStringLiteral("listWidget"))) {
		pages->hide();
		ShellObsNav *nav = new ShellObsNav(pages, dlg, g_viewObsHost);
		row->addWidget(nav);
		g_obsNav = nav;
		QObject::connect(dlg, &QObject::destroyed, nav, &QObject::deleteLater);
	} else {
		blog(LOG_WARNING, "[replaykit] the obs settings dialog has no page list to stand in for, so it keeps its own");
	}
	row->addWidget(dlg, 1);
	dlg->installEventFilter(new ObsSettingsWatch(dlg));
	ShellActivate(ShellView::ObsSettings);
	// a dialog that never paints must not stay hidden under the stage
	QPointer<QDialog> embedded = dlg;
	QTimer::singleShot(600, g_callbacks, [embedded]() {
		if (!embedded || g_obsSettingsPainted)
			return;
		g_obsSettingsPainted = true;
		if (g_shellView == ShellView::ObsSettings)
			ShellSurfaceView();
	});
	blog(LOG_INFO, "[replaykit] obs settings embedded in the main window, %lld ms after the request (own native window: %s)", g_obsBuildTimer.isValid() ? (long long)g_obsBuildTimer.elapsed() : -1LL,
	     dlg->windowHandle() ? "yes" : "no");
}

} // namespace

bool ObsSettingsEmbedFilter::eventFilter(QObject *watched, QEvent *event)
{
	if (event->type() != QEvent::Polish || !g_shellOn || g_obsSettingsDialog || !watched->isWidgetType() || ShellSetupLocked())
		return false;
	QWidget *w = static_cast<QWidget *>(watched);
	if (!w->isWindow() || w->isVisible() || qstrcmp(w->metaObject()->className(), "OBSBasicSettings") != 0)
		return false;
	QDialog *dlg = qobject_cast<QDialog *>(w);
	QMainWindow *mw = ObsMainWindowQt();
	// a hidden or minimized main window has nothing to embed into, the dialog just stays the normal window it was
	if (!dlg || !mw || !mw->isVisible() || mw->isMinimized() || !g_shellTitle)
		return false;
	ShellEmbedObsSettings(dlg);
	return false;
}

} // namespace rk
