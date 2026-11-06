// themed confirm sheet drawn over the obs window
#include "prelude.hpp"
#include "ui/confirm_dialog.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"

namespace rk {

namespace {

class RKConfirmDialog;
// the sheet that is up, so the native message of a window move can reach it
RKConfirmDialog *g_confirmDialog = nullptr;

// a themed confirm the dock cannot draw itself. it must dim EVERYTHING obs renders -- the preview and the cef docks are native child hwnds, so a plain child widget painted over them just shows black. this is a top-level frameless translucent window sized over obss client area; dwm composites the dim over the native surfaces. the card child is opaque so its text still gets cleartype. modal via QDialog::exec(). gui thread only.
class RKConfirmDialog : public QDialog {
public:
	RKConfirmDialog(QWidget *host, const QString &title, const QString &body, const QString &okText, bool danger)
		: QDialog(host), m_host(host)
	{
		setWindowFlags(Qt::Dialog | Qt::FramelessWindowHint);
		setAttribute(Qt::WA_TranslucentBackground, true);
		setAttribute(Qt::WA_DeleteOnClose, false);
		setModal(true);
		{
			QFont f = font();
			f.setFamily("Segoe UI");
			f.setPixelSize(13);
			setFont(f);
		}
		if (host)
			host->installEventFilter(this);
		syncToHost();
		g_confirmDialog = this;

		const QColor panel = g_menuPanel, brd = g_notifyBorder, ink = g_notifyTipFg, muted = g_menuMuted;
		const QColor btnBg = g_menuField, btnBgHover = g_menuFieldHover, btnBorderHover = g_menuBorderStrong;
		const QColor okBg = danger ? g_menuDanger : g_menuAccent;
		const QColor okBg2 = danger ? g_menuDanger.lighter(115) : g_menuAccentLight;
		const QColor okInk = danger ? QColor(0xFF, 0xFF, 0xFF) : g_menuOnAccent;

		// one .arg() per marker so %10/%11 can never be misread as %1 + "0"
		QString qss = QString(
			"#rkCard { background:%1; border:1px solid %2; border-radius:6px; }"
			"#rkTitle { color:%3; font-size:15px; font-weight:700; }"
			"#rkBody { color:%4; font-size:13px; }"
			"#rkCard QPushButton { min-width:84px; min-height:30px; padding:0 16px; border-radius:4px;"
			" background:%5; border:1px solid %5; color:%3; }"
			"#rkCard QPushButton:hover { background:%6; border-color:%7; }"
			"#rkCard QPushButton:pressed { background:%8; }"
			"#rkCard QPushButton:focus { border-color:%7; }"
			"#rkOk { background:%9; border-color:%9; color:%10; }"
			"#rkOk:hover { background:%11; border-color:%11; }"
			"#rkOk:pressed { background:%9; }");
		qss = qss.arg(panel.name()).arg(brd.name()).arg(ink.name()).arg(muted.name())
			  .arg(btnBg.name()).arg(btnBgHover.name()).arg(btnBorderHover.name())
			  .arg(btnBg.darker(112).name()).arg(okBg.name()).arg(okInk.name()).arg(okBg2.name());
		setStyleSheet(qss);

		auto *root = new QVBoxLayout(this);
		root->setContentsMargins(0, 0, 0, 0);
		root->addStretch();
		auto *mid = new QHBoxLayout();
		mid->addStretch();

		auto *card = new QWidget(this);
		card->setObjectName("rkCard");
		card->setAttribute(Qt::WA_StyledBackground, true);
		card->setMinimumWidth(330);
		card->setMaximumWidth(400);
		auto *cv = new QVBoxLayout(card);
		cv->setContentsMargins(20, 18, 20, 16);
		cv->setSpacing(12);

		auto *t = new QLabel(title, card);
		t->setObjectName("rkTitle");
		t->setWordWrap(true);
		auto *b = new QLabel(body, card);
		b->setObjectName("rkBody");
		b->setWordWrap(true);
		cv->addWidget(t);
		cv->addWidget(b);

		auto *btnRow = new QHBoxLayout();
		btnRow->setSpacing(8);
		btnRow->addStretch();
		auto *cancel = new QPushButton(QObject::tr("Cancel"), card);
		auto *ok = new QPushButton(okText, card);
		ok->setObjectName("rkOk");
		ok->setCursor(Qt::PointingHandCursor);
		cancel->setCursor(Qt::PointingHandCursor);
		ok->setDefault(true);
		btnRow->addWidget(cancel);
		btnRow->addWidget(ok);
		cv->addLayout(btnRow);

		mid->addWidget(card);
		mid->addStretch();
		root->addLayout(mid);
		root->addStretch();

		connect(ok, &QPushButton::clicked, this, &QDialog::accept);
		connect(cancel, &QPushButton::clicked, this, &QDialog::reject);
	}

	~RKConfirmDialog() override
	{
		if (g_confirmDialog == this)
			g_confirmDialog = nullptr;
	}

	// from the native message of a move or a resize of the obs window, before qt has taken in the new rectangle
	void FollowWindow()
	{
		RECT wr = {};
		if (!m_host || m_host != ObsMainWindowQt() || !g_shellHwnd || IsIconic(g_shellHwnd) || !GetWindowRect(g_shellHwnd, &wr))
			return;
		SetWindowPos((HWND)winId(), nullptr, int(wr.left), int(wr.top), int(wr.right - wr.left), int(wr.bottom - wr.top), SWP_NOZORDER | SWP_NOACTIVATE);
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.fillRect(rect(), QColor(0, 0, 0, 140));
	}
	void keyPressEvent(QKeyEvent *e) override
	{
		if (e->key() == Qt::Key_Return || e->key() == Qt::Key_Enter)
			accept();
		else
			QDialog::keyPressEvent(e); // QDialog maps Esc -> reject
	}
	void mousePressEvent(QMouseEvent *e) override
	{
		if (!childAt(e->pos()))
			reject();
	}
	// the title bar under the dim stays a place to drag the window from, and the sheet follows it
	bool nativeEvent(const QByteArray &, void *message, qintptr *result) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_NCHITTEST || !ShellPointIsCaption(msg->lParam))
			return false;
		*result = HTTRANSPARENT;
		return true;
	}
	bool eventFilter(QObject *o, QEvent *e) override
	{
		if (o == m_host) {
			if (e->type() == QEvent::Move || e->type() == QEvent::Resize)
				FollowWindow();
			else if (e->type() == QEvent::WindowStateChange && m_host && m_host->isMinimized())
				reject();
			else if (e->type() == QEvent::Hide)
				reject();
		}
		return QDialog::eventFilter(o, e);
	}

private:
	void syncToHost()
	{
		if (!m_host)
			return;
		setGeometry(QRect(m_host->mapToGlobal(QPoint(0, 0)), m_host->size()));
	}

	QWidget *m_host = nullptr;
};

bool g_confirmOpen = false;

// only one themed RKConfirmDialog at a time -- a repeat trigger while one is up is ignored. (the plain QMessageBox lifecycle confirms use a different rule: replace the one on screen, see ShowLifecycleConfirm.)
struct ConfirmGuard {
	bool ok;
	ConfirmGuard() : ok(!g_confirmOpen) { if (ok) g_confirmOpen = true; }
	~ConfirmGuard() { if (ok) g_confirmOpen = false; }
};

} // namespace

void ConfirmDialogFollow()
{
	if (g_confirmDialog)
		g_confirmDialog->FollowWindow();
}

// draws RKConfirmDialog over obs and blocks until the user answers. gui thread only. a second call while one is already up is ignored (returns false) instead of stacking another dialog.
bool ShowReplayKitConfirm(const QString &title, const QString &body, const QString &okText, bool danger)
{
	ConfirmGuard guard;
	if (!guard.ok)
		return false;

	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw) {
		return QMessageBox::question(nullptr, title, body, QMessageBox::Yes | QMessageBox::No,
					    QMessageBox::No) == QMessageBox::Yes;
	}
	// obs may be minimized to the tray -- surface the window so the sheet is actually seen before it acts.
	if (mw->isMinimized())
		mw->setWindowState(mw->windowState() & ~Qt::WindowMinimized);
	if (!mw->isVisible())
		mw->showNormal();
	mw->raise();
	mw->activateWindow();

	RKConfirmDialog dlg(mw, title, body, okText, danger);
	dlg.show();
	dlg.raise();
	dlg.activateWindow();
	return dlg.exec() == QDialog::Accepted;
}

} // namespace rk
