// the streamable account circle of the title bar
#include "prelude.hpp"
#include "shell/shell_account.hpp"

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_state.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

constexpr qreal kCircleD = 22.0;
constexpr int kPanelWidth = 300;
constexpr int kPanelHeight = 292;

bool g_pollInFlight = false;

// one colour per name, so the same account always gets the same circle. the hue comes from the letters, the lightness stays where white text reads on it
QColor ColorForName(const QString &name)
{
	uint hash = 2166136261u;
	for (const QChar c : name.toLower())
		hash = (hash ^ c.unicode()) * 16777619u;
	return QColor::fromHslF((hash % 360) / 360.0, 0.52, 0.42);
}

QString InitialOf(const QString &name)
{
	for (const QChar c : name) {
		if (c.isLetterOrNumber())
			return QString(c.toUpper());
	}
	return QStringLiteral("S");
}

} // namespace

ShellAccountButton::ShellAccountButton(QWidget *owner, QWidget *parent) : ShellTitleButton(kWidth, parent), m_owner(owner)
{
	SetTip(QStringLiteral("Streamable account"));
	onPress = [this]() { TogglePanel(); };
}

ShellAccountButton::~ShellAccountButton()
{
	delete m_panel;
}

void ShellAccountButton::SetAccount(const Account &account)
{
	if (account.signedIn == m_account.signedIn && account.name == m_account.name && account.picture == m_account.picture)
		return;
	if (account.picture != m_account.picture) {
		// the helper saves it in the obs-replayKit folder, which the plugin reads straight from disk like window_state.ini
		m_picture = QPixmap();
		m_pictureScaled = QPixmap();
		if (!account.picture.isEmpty())
			m_picture.load(qEnvironmentVariable("APPDATA") + QStringLiteral("/obs-studio/obs-replayKit/profile_picture.png"), "PNG");
	}
	m_account = account;
	SetTip(account.signedIn ? QStringLiteral("Streamable: %1").arg(account.name.isEmpty() ? QStringLiteral("signed in") : account.name) : QStringLiteral("Streamable: anonymous"));
	update();
}

void ShellAccountButton::PaintGlyph(QPainter &p, const QRectF &pill, qreal hover)
{
	const QPointF mid = pill.center();
	const QRectF disc(mid.x() - kCircleD / 2.0, mid.y() - kCircleD / 2.0, kCircleD, kCircleD);
	if (!m_picture.isNull()) {
		const qreal ratio = devicePixelRatioF();
		const int pixels = qRound(kCircleD * ratio);
		if (m_pictureScaled.width() != pixels) {
			m_pictureScaled = m_picture.scaled(pixels, pixels, Qt::IgnoreAspectRatio, Qt::SmoothTransformation);
			m_pictureScaled.setDevicePixelRatio(ratio);
		}
		QPainterPath round;
		round.addEllipse(disc);
		p.save();
		p.setClipPath(round);
		p.setRenderHint(QPainter::SmoothPixmapTransform, true);
		p.drawPixmap(disc, m_pictureScaled, QRectF(QPointF(0, 0), m_pictureScaled.size()));
		p.restore();
		p.setPen(QPen(ChromeInk(0.14 + 0.25 * hover), 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawEllipse(disc.adjusted(0.5, 0.5, -0.5, -0.5));
		if (m_account.signedIn) {
			p.setPen(QPen(g_chromeBar, 2.0));
			p.setBrush(g_chromeSuccess);
			p.drawEllipse(QPointF(disc.right() - 2.2, disc.bottom() - 2.2), 3.2, 3.2);
		}
		return;
	}
	if (!m_account.signedIn) {
		// nobody signed in: a see-through grey disc with the outline of a person in it. the head and the shoulders end well inside the ring, so nothing of the person runs into it
		p.setPen(Qt::NoPen);
		p.setBrush(ChromeInk(0.10 + 0.05 * hover));
		p.drawEllipse(disc);
		QPen line(ChromeInk(0.52 + 0.28 * hover), 1.4);
		line.setCapStyle(Qt::RoundCap);
		line.setJoinStyle(Qt::RoundJoin);
		p.setPen(line);
		p.setBrush(Qt::NoBrush);
		p.drawEllipse(QPointF(mid.x(), mid.y() - 3.6), 2.8, 2.8);
		QPainterPath shoulders;
		shoulders.moveTo(mid.x() - 5.2, mid.y() + 6.0);
		shoulders.lineTo(mid.x() - 5.2, mid.y() + 4.0);
		shoulders.quadTo(mid.x() - 5.2, mid.y() + 1.8, mid.x() - 3.0, mid.y() + 1.8);
		shoulders.lineTo(mid.x() + 3.0, mid.y() + 1.8);
		shoulders.quadTo(mid.x() + 5.2, mid.y() + 1.8, mid.x() + 5.2, mid.y() + 4.0);
		shoulders.lineTo(mid.x() + 5.2, mid.y() + 6.0);
		p.drawPath(shoulders);
		p.setPen(QPen(ChromeInk(0.20 + 0.22 * hover), 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawEllipse(disc.adjusted(0.5, 0.5, -0.5, -0.5));
		return;
	}
	// signed in: the colour of the account with its first letter, and the green of a live status light in the corner
	const QColor base = ColorForName(m_account.name);
	QLinearGradient fill(disc.topLeft(), disc.bottomRight());
	fill.setColorAt(0.0, base.lighter(120));
	fill.setColorAt(1.0, base.darker(118));
	p.setPen(Qt::NoPen);
	p.setBrush(fill);
	p.drawEllipse(disc);
	QFont f("Segoe UI");
	f.setPixelSize(12);
	f.setWeight(QFont::Bold);
	p.setFont(f);
	p.setPen(QColor(255, 255, 255));
	p.drawText(disc, Qt::AlignCenter, InitialOf(m_account.name));
	p.setPen(QPen(ChromeInk(0.10 + 0.25 * hover), 1.0));
	p.setBrush(Qt::NoBrush);
	p.drawEllipse(disc.adjusted(0.5, 0.5, -0.5, -0.5));
	const QPointF dot(disc.right() - 2.2, disc.bottom() - 2.2);
	p.setPen(QPen(g_chromeBar, 2.0));
	p.setBrush(g_chromeSuccess);
	p.drawEllipse(dot, 3.2, 3.2);
}

AnchoredPanel *ShellAccountButton::EnsurePanel()
{
	if (!m_panel && m_owner)
		m_panel = new AnchoredPanel(m_owner, QStringLiteral("ReplayKit Account"), QStringLiteral("http://127.0.0.1:8767/account-menu"), QSize(kPanelWidth, kPanelHeight));
	return m_panel;
}

void ShellAccountButton::TogglePanel()
{
	if (!EnsurePanel())
		return;
	const QPointer<ShellAccountButton> button(this);
	AnchoredPanel::Anchor anchor;
	anchor.key = this;
	// the same hang as the bell: the right edge of the panel is flush with the button and its top border sits on the last row of it
	anchor.edge = AnchoredPanel::Edge::Right;
	anchor.seam = 1;
	anchor.tabRect = [button]() {
		if (!button || !button->isVisible())
			return QRect();
		return button->GlobalRect();
	};
	anchor.owns = [button](const POINT &, const QPoint &qtPoint) { return button && button->GlobalRect().contains(qtPoint); };
	anchor.setOpen = [button](bool open) {
		if (button)
			button->SetOpen(open);
	};
	// signing in or out happens inside the menu, so the circle reads the account again when it goes away
	anchor.onHidden = []() { ShellAccountPoll(); };
	m_panel->Toggle(anchor, QStringLiteral("window.__replaykitShowAccount && window.__replaykitShowAccount();"));
}

void ShellAccountPoll()
{
	if (g_pollInFlight)
		return;
	g_pollInFlight = true;
	RunAsync([]() {
		const std::string body = HttpRequest("GET", "/me", 8767, nullptr, 3000);
		const bool answered = !body.empty() && body.find("signedIn") != std::string::npos;
		ShellAccountButton::Account account;
		if (answered) {
			account.signedIn = JsonBoolField(body, "signedIn", false);
			// the helper hides the real name, so the masked one is what there is to show; display name is only the words "Signed in"
			std::string name = ExtractJsonStringField(body, "maskedUsername");
			if (name.empty())
				name = ExtractJsonStringField(body, "username");
			if (name.empty())
				name = ExtractJsonStringField(body, "displayName");
			account.name = QString::fromStdString(name);
			account.picture = QString::fromStdString(ExtractJsonStringField(body, "picture"));
		}
		QMetaObject::invokeMethod(
			g_callbacks,
			[answered, account]() {
				g_pollInFlight = false;
				// a helper that is down or answering something else keeps the circle as it was
				if (!answered || !g_shellTitle)
					return;
				g_shellTitle->SetAccount(account);
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
