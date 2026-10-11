// menu that hangs off the clip and record badges
#include "prelude.hpp"
#include "shell/shell_hotkey_menu.hpp"

#include "core/state.hpp"
#include "shell/shell_badge.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

constexpr int kMenuWidth = 320;
constexpr int kMenuHeight = 172;

const char *KindName(ShellHotkeyMenu::Kind kind)
{
	return kind == ShellHotkeyMenu::Kind::Clip ? "clip" : "record";
}

} // namespace

ShellHotkeyMenu::ShellHotkeyMenu(QWidget *owner) : m_owner(owner) {}

ShellHotkeyMenu::~ShellHotkeyMenu()
{
	delete m_panel;
}

QString ShellHotkeyMenu::UrlFor(Kind kind, int tabWidth, int opens)
{
	// clip: whether obs is keeping clips right now, so the switch of the clip menu is right on its first frame and not after the page has asked obs
	return QStringLiteral("http://127.0.0.1:8767/hotkey-menu#kind=%1&tab=%2&open=%3&clip=%4").arg(QLatin1String(KindName(kind))).arg(tabWidth).arg(opens).arg(obs_frontend_replay_buffer_active() ? 1 : 0);
}

void ShellHotkeyMenu::Prepare(ShellBadge *badge, Kind kind)
{
	if (!badge)
		return;
	if (!m_panel)
		m_panel = new AnchoredPanel(m_owner, QStringLiteral("ReplayKit Hotkeys"), UrlFor(kind, badge->width(), m_opens), QSize(kMenuWidth, kMenuHeight));
	m_panel->EnsureBuilt();
}

void ShellHotkeyMenu::Toggle(ShellBadge *badge, Kind kind)
{
	if (!badge)
		return;
	Prepare(badge, kind);

	const QPointer<ShellBadge> target(badge);
	AnchoredPanel::Anchor anchor;
	anchor.key = badge;
	// the tab is on the left of the panel, and the badge is a child widget, so the panel starts on the row right below it
	anchor.edge = AnchoredPanel::Edge::Left;
	anchor.seam = 0;
	anchor.tabRect = [target]() {
		if (!target || !target->isVisible())
			return QRect();
		return target->GlobalRect();
	};
	anchor.owns = [target](const POINT &, const QPoint &qtPoint) { return target && target->GlobalRect().contains(qtPoint); };
	anchor.setOpen = [target](bool open) {
		if (target)
			target->SetOpen(open);
	};
	// a key that was being picked is given up and the obs bindings come back
	AnchoredPanel *panel = m_panel;
	anchor.onHidden = [panel]() { panel->RunScript(QStringLiteral("window.__replaykitHotkeyMenuHidden && window.__replaykitHotkeyMenuHidden();")); };
	// which badge it hangs off and how wide it is; the first call is the open itself, the repeats only say the same again every open changes the address of the page, which reaches it by a way of its own: a script can be lost while the page is loading or waking up, an address is applied by the browser itself. a second press on the same badge only closes the menu
	if (!(panel->IsOpen() && panel->AnchorKey() == badge))
		panel->SetUrl(UrlFor(kind, badge->width(), ++m_opens));
	const QString kindName = QLatin1String(KindName(kind));
	const QString width = QString::number(badge->width());
	auto call = [&](bool fresh) {
		return QStringLiteral("window.__replaykitShowHotkeyMenu && window.__replaykitShowHotkeyMenu('%1', %2, %3, %4);").arg(kindName, fresh ? QStringLiteral("true") : QStringLiteral("false"), width, obs_frontend_replay_buffer_active() ? QStringLiteral("true") : QStringLiteral("false"));
	};
	panel->Toggle(anchor, call(true));

	// a page can miss a script, while it is still loading or waking up after a long hide, so the open is sent again a moment later
	const QPointer<QWidget> window = panel->Window();
	const QString again = call(false);
	for (const int delay : {250, 900}) {
		QTimer::singleShot(delay, g_callbacks, [panel, window, badge = target, again]() {
			if (window && badge && panel->IsOpen() && panel->AnchorKey() == badge.data())
				panel->RunScript(again);
		});
	}
}

void ShellHotkeyMenu::Follow()
{
	if (m_panel && m_panel->IsShown())
		m_panel->Follow();
}

void ShellHotkeyMenu::Hide()
{
	if (m_panel && m_panel->IsShown())
		m_panel->Hide(false);
}

} // namespace rk
