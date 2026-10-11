// page list of the embedded obs settings
#include "prelude.hpp"
#include "shell/shell_obs_nav.hpp"

#include <QGroupBox>
#include <QRegularExpression>
#include <QScrollArea>
#include <QStackedWidget>

#include "core/app_icon.hpp"
#include "core/app_version.hpp"
#include "core/async.hpp"
#include "core/draw_util.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

// the same mark the sidebar of the replaykit settings uses (icons/brand/github.svg); the fill is recoloured when it is drawn
const char kGithubSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path fill-rule="evenodd" d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8z"/></svg>)SVG";
const char kGithubUrl[] = "https://github.com/7Lzz/OBS-ReplayKit";

QColor Mix(const QColor &a, const QColor &b, qreal t)
{
	return QColor::fromRgbF(a.redF() + (b.redF() - a.redF()) * t, a.greenF() + (b.greenF() - a.greenF()) * t, a.blueF() + (b.blueF() - a.blueF()) * t,
				a.alphaF() + (b.alphaF() - a.alphaF()) * t);
}

// what a label or a switch of the dialog says, without its markup and its accelerator marks
QString PlainText(QString text)
{
	text.remove(QRegularExpression(QStringLiteral("<[^>]*>")));
	text.remove('&');
	return text.simplified();
}

// true for a label, a check box, a radio button or a button whose text holds the needle
bool HoldsText(QWidget *w, const QString &needle)
{
	QString text;
	if (QLabel *label = qobject_cast<QLabel *>(w))
		text = label->text();
	else if (QAbstractButton *button = qobject_cast<QAbstractButton *>(w))
		text = button->text();
	else if (QGroupBox *group = qobject_cast<QGroupBox *>(w))
		text = group->title();
	else
		return false;
	return PlainText(text).contains(needle, Qt::CaseInsensitive);
}

// the helper hands links to the default browser, the same route the sidebar of the replaykit settings takes for this one
void OpenGithub()
{
	const QByteArray path = "/open-url?url=" + QUrl::toPercentEncoding(QString::fromLatin1(kGithubUrl));
	RunAsync([path]() { HttpRequest("GET", path.constData(), 8767, nullptr, 3000); });
}

} // namespace

ShellObsNav::ShellObsNav(QListWidget *source, QDialog *dialog, QWidget *parent)
	: QWidget(parent), m_source(source), m_dialog(dialog)
{
	setMouseTracking(true);
	setFixedWidth(kShellNavW);
	m_github.load(QByteArray(kGithubSvg));
	QObject::connect(source, &QListWidget::currentRowChanged, this, [this](int) {
		MarkShownPage();
		update();
	});
	m_search = new QLineEdit(this);
	m_search->setPlaceholderText(QStringLiteral("Search settings"));
	m_search->installEventFilter(this);
	m_sendTimer.setSingleShot(true);
	m_sendTimer.setInterval(250);
	QObject::connect(&m_sendTimer, &QTimer::timeout, this, [this]() { SendSearch(); });
	QObject::connect(m_search, &QLineEdit::textChanged, this, [this]() {
		ApplySearch(true);
		m_sendTimer.start();
	});
	QObject::connect(m_search, &QLineEdit::returnPressed, this, [this]() { ApplySearch(true); });
	PlaceSearch();
	m_ease.setInterval(16);
	QObject::connect(&m_ease, &QTimer::timeout, this, [this]() { StepHover(); });
}

void ShellObsNav::showEvent(QShowEvent *event)
{
	QWidget::showEvent(event);
	// the box takes the colours of the theme as they are now
	m_search->setStyleSheet(QStringLiteral("QLineEdit { background:%1; border:1px solid %2; border-radius:4px; padding:0 9px; color:%3; font-family:'Segoe UI'; font-size:13px; selection-background-color:%4; selection-color:%5; }"
					       "QLineEdit:focus { border-color:%6; }")
						.arg(g_chromeBg.name(), g_menuField.name(), QColor(g_notifyTipFg).name(), g_menuAccent.name(), g_menuOnAccent.name(), g_menuAccentLight.name()));
	PlaceSearch();
	PullSearch();
	m_logo = ShellLogoPixmap(kIconPx, devicePixelRatioF());
	RefreshAppVersion([nav = QPointer<ShellObsNav>(this)]() {
		if (nav)
			nav->update();
	});
}

void ShellObsNav::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	SetHover(kNone);
}

void ShellObsNav::mouseMoveEvent(QMouseEvent *event)
{
	SetHover(HitRow(event->position().toPoint()));
}

void ShellObsNav::mousePressEvent(QMouseEvent *event)
{
	if (event->button() == Qt::LeftButton)
		m_pressed = HitRow(event->position().toPoint());
}

void ShellObsNav::mouseReleaseEvent(QMouseEvent *event)
{
	const int hit = HitRow(event->position().toPoint());
	const int pressed = m_pressed;
	m_pressed = kNone;
	if (event->button() != Qt::LeftButton || hit == kNone || hit != pressed)
		return;
	if (hit == kGithub)
		OpenGithub();
	else if (hit == kBack)
		ShellActivate(ShellView::Settings);
	else if (m_source)
		m_source->setCurrentRow(hit);
}

void ShellObsNav::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	p.fillRect(rect(), g_chromeBg);
	const QRectF panel = PanelRect();
	p.setPen(Qt::NoPen);
	p.setBrush(g_menuPanel);
	p.drawRoundedRect(panel, 8, 8);

	DrawPageText(p, QRectF(panel.left() + 16, panel.top() + kHeadTop, panel.width() - 32, 22), Qt::AlignLeft, QStringLiteral("OBS SETTINGS"), HeadFont(), g_menuMuted, kHeadStroke);
	const QList<Row> rows = Rows();
	if (!rows.isEmpty()) {
		const int footTop = rows.last().rect.top();
		p.fillRect(QRectF(panel.left() + 16, footTop - 36, panel.width() - 32, 1), ChromeInk(0.10));
		DrawPageText(p, QRectF(panel.left() + 16, footTop - 30, panel.width() - 32, 22), Qt::AlignLeft, QStringLiteral("REPLAYKIT"), HeadFont(), g_menuMuted, kHeadStroke);
		if (VersionRowFits(rows)) {
			// the version on the left and the github mark on the right, standing on the rule like the ones in the sidebar of the replaykit settings
			const QRectF box = GithubRect();
			const qreal left = panel.left() + 16;
			DrawPageText(p, QRectF(left, box.top(), box.left() - 6 - left, box.height()), Qt::AlignLeft, AppVersionText(), VersionFont(), g_menuMuted, kVersionStroke);
			const qreal t = m_hoverT.value(kGithub, 0.0);
			if (t > 0.0) {
				QColor wash = g_menuBorderStrong;
				wash.setAlphaF(t);
				p.setPen(Qt::NoPen);
				p.setBrush(wash);
				p.drawRoundedRect(box, 4, 4);
			}
			const QSizeF glyph(15, 15);
			p.drawPixmap(QPointF(box.center().x() - glyph.width() / 2.0, box.center().y() - glyph.height() / 2.0),
				     TintedSvgPixmap(m_github, glyph, devicePixelRatioF(), Mix(g_menuMuted, g_notifyTipFg, t)));
		}
	}

	const int current = m_source ? m_source->currentRow() : -1;
	for (const Row &row : rows) {
		const bool selected = row.index >= 0 && row.index == current;
		const qreal t = m_hoverT.value(row.index, 0.0);
		// while a search is on the pages with nothing in them fade back, and the ones with a match are outlined like the tabs of the replaykit settings
		const bool dimmed = m_searching && row.index >= 0 && !m_hits.contains(row.index);
		const bool matched = m_searching && row.index >= 0 && m_hits.contains(row.index);
		p.setOpacity(dimmed ? 0.38 : 1.0);
		// a tab of the replaykit settings moves a pixel toward its text on hover and its wash fades in; the whole row does the same here
		p.save();
		p.translate(1.0 - qPow(1.0 - t, 3.0), 0);
		if (selected || t > 0.0) {
			p.setPen(Qt::NoPen);
			p.setBrush(ChromeInk(selected ? 0.13 : 0.07 * t));
			p.drawRoundedRect(QRectF(row.rect), 8, 8);
		}
		// the icon the obs dialog gives the page, drawn in the ink of the list so it follows the theme. the way back shows the app icon of the title bar instead
		const QPointF iconAt(row.rect.left() + kPadX, row.rect.center().y() - kIconPx / 2.0);
		if (row.index == kBack) {
			if (!m_logo.isNull())
				p.drawPixmap(iconAt, m_logo);
		} else if (m_source && m_source->item(row.index)) {
			const QIcon icon = m_source->item(row.index)->icon();
			if (!icon.isNull()) {
				QPixmap pm = icon.pixmap(QSize(kIconPx, kIconPx), devicePixelRatioF());
				QPainter tint(&pm);
				tint.setCompositionMode(QPainter::CompositionMode_SourceIn);
				tint.fillRect(pm.rect(), selected ? QColor(g_notifyTipFg) : ChromeInk(0.72 + 0.28 * t));
				tint.end();
				p.drawPixmap(iconAt, pm);
			}
		}
		DrawPageText(p, QRectF(row.rect).adjusted(kTextX, 0, -10, 0), Qt::AlignLeft, row.text, RowFont(selected), selected ? g_notifyTipFg : ChromeInk(0.86), kRowStroke);
		if (matched) {
			p.setPen(QPen(g_menuAccentLight, 1.0));
			p.setBrush(Qt::NoBrush);
			p.drawRoundedRect(QRectF(row.rect).adjusted(0.5, 0.5, -0.5, -0.5), 8, 8);
		}
		if (row.index == kBack) {
			// a chevron pointing back
			p.setPen(QPen(ChromeInk(0.62), 1.6, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
			p.setBrush(Qt::NoBrush);
			const QPointF c(row.rect.right() - 16, row.rect.center().y());
			p.drawPolyline(QPolygonF({QPointF(c.x() + 3, c.y() - 5), QPointF(c.x() - 2, c.y()), QPointF(c.x() + 3, c.y() + 5)}));
		}
		p.restore();
	}
	p.setOpacity(1.0);
}

QFont ShellObsNav::RowFont(bool selected)
{
	QFont f("Segoe UI");
	f.setPixelSize(14);
	f.setWeight(selected ? kRowWeightSelected : kRowWeight);
	f.setHintingPreference(QFont::PreferNoHinting);
	return f;
}

QFont ShellObsNav::VersionFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setWeight(QFont::Normal);
	f.setHintingPreference(QFont::PreferNoHinting);
	return f;
}

QFont ShellObsNav::HeadFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setWeight(QFont::Bold);
	f.setLetterSpacing(QFont::AbsoluteSpacing, 0.99);
	f.setHintingPreference(QFont::PreferNoHinting);
	return f;
}

QRectF ShellObsNav::PanelRect() const
{
	return QRectF(10, 10, width() - 18, height() - 18);
}

QList<ShellObsNav::Row> ShellObsNav::Rows() const
{
	QList<Row> rows;
	const QRectF panel = PanelRect();
	const int x = qRound(panel.left()) + 6;
	const int w = qRound(panel.width()) - 12;
	int y = qRound(panel.top()) + kRowsTop;
	for (int i = 0; m_source && i < m_source->count(); ++i) {
		QListWidgetItem *item = m_source->item(i);
		if (!item || item->isHidden())
			continue;
		rows.append({i, QRect(x, y, w, kRowH), item->text()});
		y += kRowH + 2;
	}
	rows.append({kBack, QRect(x, qRound(panel.bottom()) - 6 - kRowH, w, kRowH), QStringLiteral("ReplayKit Settings")});
	return rows;
}

// a 24 px square, 16 px in from the right edge of the panel, whose bottom sits 6 px above the rule over the way back
QRectF ShellObsNav::GithubRect() const
{
	const QRectF panel = PanelRect();
	const qreal ruleTop = qRound(panel.bottom()) - 6 - kRowH - 36;
	return QRectF(panel.right() - 16 - 24, ruleTop - 30, 24, 24);
}

bool ShellObsNav::VersionRowFits(const QList<Row> &rows) const
{
	const int lastPageBottom = rows.size() >= 2 ? rows[rows.size() - 2].rect.bottom() : qRound(PanelRect().top()) + kRowsTop;
	return GithubRect().top() >= lastPageBottom + 4;
}

void ShellObsNav::hideEvent(QHideEvent *event)
{
	QWidget::hideEvent(event);
	// a change that is still waiting for its pause is sent now, since the page that reads it may come up next
	if (m_sendTimer.isActive()) {
		m_sendTimer.stop();
		SendSearch();
	}
}

void ShellObsNav::SendSearch()
{
	const QByteArray path = "/settings/search?q=" + QUrl::toPercentEncoding(m_search->text());
	RunAsync([path]() { HttpRequest("POST", path.constData(), 8767, nullptr, 3000); });
}

void ShellObsNav::PullSearch()
{
	RunAsync([nav = QPointer<ShellObsNav>(this)]() {
		const std::string body = HttpRequest("GET", "/settings/search", 8767, nullptr, 3000);
		if (body.empty() || !JsonBoolField(body, "ok", false))
			return;
		const QString shared = QString::fromStdString(ExtractJsonStringField(body, "q"));
		QMetaObject::invokeMethod(
			g_callbacks,
			[nav, shared]() {
				if (!nav || shared == nav->m_search->text())
					return;
				QSignalBlocker block(nav->m_search);
				nav->m_search->setText(shared);
				nav->ApplySearch(true);
			},
			Qt::QueuedConnection);
	});
}

void ShellObsNav::resizeEvent(QResizeEvent *event)
{
	QWidget::resizeEvent(event);
	PlaceSearch();
}

bool ShellObsNav::eventFilter(QObject *watched, QEvent *event)
{
	if (watched == m_search && event->type() == QEvent::KeyPress && static_cast<QKeyEvent *>(event)->key() == Qt::Key_Escape && !m_search->text().isEmpty()) {
		m_search->clear();
		return true;
	}
	return QWidget::eventFilter(watched, event);
}

void ShellObsNav::PlaceSearch()
{
	if (!m_search)
		return;
	const QRectF panel = PanelRect();
	m_search->setGeometry(qRound(panel.left()) + 10, qRound(panel.top()) + kSearchTop, qRound(panel.width()) - 20, kSearchH);
}

void ShellObsNav::ClearMarks()
{
	for (const QPointer<QWidget> &w : m_marked) {
		if (!w)
			continue;
		w->setProperty("rkSearchHit", false);
		w->style()->unpolish(w);
		w->style()->polish(w);
		w->update();
	}
	m_marked.clear();
}

// what a match looks like on a page of the dialog: a wash of the accent behind the label or the switch. it is put on the dialog with the first search and not when the column is built, since that happens while the dialog is still being polished, and the rule matches nothing until a search marks a widget
void ShellObsNav::InstallHitStyle()
{
	if (m_hitStyle || !m_dialog)
		return;
	m_hitStyle = true;
	QColor wash = g_menuAccentLight;
	wash.setAlpha(80);
	m_dialog->setStyleSheet(m_dialog->styleSheet() + QStringLiteral("QLabel[rkSearchHit=\"true\"], QCheckBox[rkSearchHit=\"true\"], QRadioButton[rkSearchHit=\"true\"], QPushButton[rkSearchHit=\"true\"] { background-color: rgba(%1, %2, %3, %4); border-radius: 3px; }")
								   .arg(wash.red()).arg(wash.green()).arg(wash.blue()).arg(wash.alpha()));
}

void ShellObsNav::ApplySearch(bool jump)
{
	ClearMarks();
	if (!m_search->text().trimmed().isEmpty())
		InstallHitStyle();
	m_hits.clear();
	const QString needle = m_search->text().trimmed();
	m_searching = !needle.isEmpty();
	QStackedWidget *pages = m_dialog ? m_dialog->findChild<QStackedWidget *>(QStringLiteral("settingsPages")) : nullptr;
	if (m_searching && pages) {
		for (int i = 0; i < pages->count(); ++i) {
			// only what the page would show: the other output mode, and anything else the dialog keeps hidden, is not something to send people to
			for (QWidget *w : pages->widget(i)->findChildren<QWidget *>()) {
				if (w->isVisibleTo(pages->widget(i)) && HoldsText(w, needle)) {
					m_hits.insert(i);
					break;
				}
			}
		}
	}
	if (jump && m_searching && m_source && !m_hits.contains(m_source->currentRow()) && !m_hits.isEmpty()) {
		int first = *m_hits.begin();
		for (int i : m_hits)
			first = qMin(first, i);
		m_source->setCurrentRow(first);
	}
	MarkShownPage();
	update();
}

void ShellObsNav::MarkShownPage()
{
	ClearMarks();
	const QString needle = m_search ? m_search->text().trimmed() : QString();
	QStackedWidget *pages = m_dialog ? m_dialog->findChild<QStackedWidget *>(QStringLiteral("settingsPages")) : nullptr;
	if (needle.isEmpty() || !pages || !pages->currentWidget())
		return;
	QWidget *first = nullptr;
	for (QWidget *w : pages->currentWidget()->findChildren<QWidget *>()) {
		if (!w->isVisibleTo(pages->currentWidget()) || !HoldsText(w, needle) || qobject_cast<QGroupBox *>(w))
			continue;
		w->setProperty("rkSearchHit", true);
		w->style()->unpolish(w);
		w->style()->polish(w);
		w->update();
		m_marked.append(QPointer<QWidget>(w));
		if (!first)
			first = w;
	}
	// the first match comes into view when the page scrolls
	for (QWidget *up = first ? first->parentWidget() : nullptr; up; up = up->parentWidget()) {
		if (QScrollArea *area = qobject_cast<QScrollArea *>(up)) {
			area->ensureWidgetVisible(first);
			break;
		}
	}
}

int ShellObsNav::HitRow(const QPoint &pt) const
{
	const QList<Row> rows = Rows();
	if (VersionRowFits(rows) && GithubRect().contains(pt))
		return kGithub;
	for (const Row &row : rows)
		if (row.rect.contains(pt))
			return row.index;
	return kNone;
}

void ShellObsNav::SetHover(int index)
{
	if (index == m_hover)
		return;
	// the tip the sidebar of the replaykit settings gives the same link as its title
	if (m_hover == kGithub && g_notifyTip)
		g_notifyTip->Leave();
	m_hover = index;
	if (index == kGithub) {
		if (!g_notifyTip)
			g_notifyTip = new NotifyTip();
		const QRectF box = GithubRect();
		g_notifyTip->ShowFor(QStringLiteral("ReplayKit on GitHub"), QRect(mapToGlobal(box.topLeft().toPoint()), box.size().toSize()));
	}
	setCursor(index == kNone ? Qt::ArrowCursor : Qt::PointingHandCursor);
	if (index != kNone && !m_hoverT.contains(index))
		m_hoverT.insert(index, 0.0);
	if (!m_ease.isActive()) {
		m_easeClock.start();
		m_ease.start();
	}
	update();
}

void ShellObsNav::StepHover()
{
	const qreal step = qreal(m_easeClock.restart()) / kHoverMs;
	bool moving = false;
	for (auto it = m_hoverT.begin(); it != m_hoverT.end();) {
		const bool on = it.key() == m_hover;
		const qreal t = qBound(0.0, it.value() + (on ? step : -step), 1.0);
		if (!on && t <= 0.0) {
			it = m_hoverT.erase(it);
			continue;
		}
		it.value() = t;
		if (!(on && t >= 1.0))
			moving = true;
		++it;
	}
	if (!moving)
		m_ease.stop();
	update();
}

} // namespace rk
