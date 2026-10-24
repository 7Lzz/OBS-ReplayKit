// quick action menu that hangs off the logo of the title bar
#include "prelude.hpp"
#include "shell/shell_quick_menu.hpp"

#include <QDialogButtonBox>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPushButton>
#include <QUrl>

#include "app/clip_buffer.hpp"
#include "app/obs_actions.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/state.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_title_button.hpp"
#include "shell/shell_types.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

// the page draws from the same numbers: a 40px header, 6px above and below the list, 34 a row and 9 a divider, inside a 1px border
constexpr int kMenuWidth = 236;
constexpr int kHeadH = 40;
constexpr int kListPad = 6;
constexpr int kRowH = 34;
constexpr int kSepH = 9;

QJsonObject Action(const QString &id, const QString &label, const QString &icon, bool disabled = false)
{
	return QJsonObject{{"id", id}, {"label", label}, {"icon", icon}, {"disabled", disabled}};
}

// what the plugin does itself in every view: keep a clip, start or stop recording and take a screenshot of what obs shows
QJsonArray GlobalActions()
{
	const bool clipping = ClipBufferActive();
	return QJsonArray{
		Action(QStringLiteral("save-clip"), QStringLiteral("Save clip"), QStringLiteral("quick/save-clip"), !clipping),
		obs_frontend_recording_active() ? Action(QStringLiteral("record"), QStringLiteral("Stop recording"), QStringLiteral("quick/stop"))
						: Action(QStringLiteral("record"), QStringLiteral("Start recording"), QStringLiteral("quick/record")),
		Action(QStringLiteral("screenshot"), QStringLiteral("Take screenshot"), QStringLiteral("quick/screenshot")),
	};
}

// the rows the view itself offers: the list its page gave the helper, or the actions of a native view
QJsonArray ViewActions(const QString &view, const std::string &pageList)
{
	QJsonArray out;
	if (view == QLatin1String("projector")) {
		out.append(Action(QStringLiteral("preview"), ShellPreviewTarget() ? QStringLiteral("Turn preview off") : QStringLiteral("Turn preview on"),
				  ShellPreviewTarget() ? QStringLiteral("quick/eye-off") : QStringLiteral("quick/eye")));
		return out;
	}
	if (view == QLatin1String("obs-settings")) {
		out.append(Action(QStringLiteral("obs-save"), QStringLiteral("Save"), QStringLiteral("action/save")));
		out.append(Action(QStringLiteral("obs-cancel"), QStringLiteral("Cancel"), QStringLiteral("editor/close")));
		return out;
	}
	// the helper answers with the whole http response, and the list is the body after the headers
	const size_t bodyAt = pageList.find("\r\n\r\n");
	const QJsonDocument doc = QJsonDocument::fromJson(QByteArray::fromStdString(bodyAt == std::string::npos ? pageList : pageList.substr(bodyAt + 4)));
	for (const QJsonValue &value : doc.object().value(QStringLiteral("actions")).toArray()) {
		const QJsonObject a = value.toObject();
		const QString id = a.value(QStringLiteral("id")).toString();
		if (id.isEmpty())
			continue;
		// the ids of a page are told apart from the plugins own, which Run carries out itself
		out.append(Action(QStringLiteral("page-") + id, a.value(QStringLiteral("label")).toString(), a.value(QStringLiteral("icon")).toString(), a.value(QStringLiteral("disabled")).toBool()));
	}
	return out;
}

bool ValidPart(const QString &part)
{
	static const QRegularExpression pattern(QStringLiteral("^[a-z][a-z-]{0,40}$"));
	return pattern.match(part).hasMatch();
}

void ClickObsSettingsButton(QDialogButtonBox::StandardButton which)
{
	QDialog *dialog = g_obsSettingsDialog;
	if (!dialog)
		return;
	for (QDialogButtonBox *box : dialog->findChildren<QDialogButtonBox *>()) {
		if (QPushButton *button = box->button(which)) {
			button->click();
			return;
		}
	}
}

} // namespace

QString ShellViewName()
{
	switch (g_shellView) {
	case ShellView::Home: return QStringLiteral("home");
	case ShellView::Projector: return QStringLiteral("projector");
	case ShellView::Clips: return QStringLiteral("clips");
	case ShellView::Settings: return QStringLiteral("settings");
	case ShellView::Setup: return QStringLiteral("setup");
	case ShellView::ObsSettings: return QStringLiteral("obs-settings");
	}
	return QString();
}

ShellQuickMenu::ShellQuickMenu(QWidget *owner) : m_owner(owner) {}

ShellQuickMenu::~ShellQuickMenu()
{
	delete m_panel;
}

AnchoredPanel *ShellQuickMenu::Prepare()
{
	if (!m_panel)
		m_panel = new AnchoredPanel(m_owner, QStringLiteral("ReplayKit Quick Actions"), QStringLiteral("http://127.0.0.1:8767/quick-actions-view"), QSize(kMenuWidth, 200));
	m_panel->EnsureBuilt();
	return m_panel;
}

void ShellQuickMenu::Toggle(ShellTitleButton *button)
{
	if (!button)
		return;
	Prepare();
	if (m_panel->IsOpen() && m_panel->AnchorKey() == button) {
		m_panel->Hide(true);
		return;
	}
	if (m_asking)
		return;
	const QString view = ShellViewName();
	if (view != QLatin1String("home") && view != QLatin1String("clips") && view != QLatin1String("settings") && view != QLatin1String("setup")) {
		Open(button, std::string());
		return;
	}
	// the list of the page lives in the helper, which answers in a few milliseconds; the open waits for it so the menu is right on its first frame
	m_asking = true;
	const QPointer<ShellTitleButton> target(button);
	const std::string path = "/quick-actions/state?view=" + view.toStdString();
	RunAsync([this, target, path]() {
		const std::string body = HttpRequest("GET", path.c_str(), 8767, nullptr, 2000);
		QMetaObject::invokeMethod(g_callbacks, [this, target, body]() {
			m_asking = false;
			if (target)
				Open(target, body);
		}, Qt::QueuedConnection);
	});
}

void ShellQuickMenu::Open(ShellTitleButton *button, const std::string &pageList)
{
	const QString view = ShellViewName();
	QJsonArray groups;
	const QJsonArray own = ViewActions(view, pageList);
	if (!own.isEmpty())
		groups.append(own);
	// setup keeps the window to itself, so it offers none of the actions that would take it elsewhere
	if (view != QLatin1String("setup"))
		groups.append(GlobalActions());
	int rows = 0;
	for (const QJsonValue &group : groups)
		rows += group.toArray().size();
	// a view with nothing to offer, setup, opens no menu at all rather than an empty one
	if (rows == 0)
		return;
	const int height = 2 + kHeadH + kListPad * 2 + rows * kRowH + qMax(0, int(groups.size()) - 1) * kSepH;
	m_panel->SetSize(QSize(kMenuWidth, height));

	const QJsonObject menu{{"view", view}, {"title", QStringLiteral("Quick actions")}, {"groups", groups}};
	const QString json = QString::fromUtf8(QJsonDocument(menu).toJson(QJsonDocument::Compact));
	// joined rather than filled in with arg, since the encoded list is full of %2C and %22 that arg would take for its own markers
	const QString url = QStringLiteral("http://127.0.0.1:8767/quick-actions-view#menu=") + QString::fromLatin1(QUrl::toPercentEncoding(json)) +
			    QStringLiteral("&tab=") + QString::number(button->width()) + QStringLiteral("&open=") + QString::number(++m_opens);
	m_panel->SetUrl(url);

	const QPointer<ShellTitleButton> target(button);
	AnchoredPanel::Anchor anchor;
	anchor.key = button;
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
	const QString show = QStringLiteral("window.__replaykitShowQuickActions && window.__replaykitShowQuickActions();");
	m_panel->Toggle(anchor, show);
	// a page can miss a script while it is still waking up, so the open is said again a moment later, the way the hotkey menu does
	const QPointer<AnchoredPanel> window = m_panel;
	AnchoredPanel *panel = m_panel;
	QTimer::singleShot(250, g_callbacks, [panel, window, target, show]() {
		if (window && target && panel->IsOpen() && panel->AnchorKey() == target.data())
			panel->RunScript(show);
	});
}

void ShellQuickMenu::Follow()
{
	if (m_panel && m_panel->IsShown())
		m_panel->Follow();
}

void ShellQuickMenu::Run(const QString &view, const QString &id)
{
	// a pick made in a view that is no longer the one showing is left alone
	if (!ValidPart(view) || !ValidPart(id) || view != ShellViewName())
		return;
	AnchoredPanel::HideAll(true);
	if (id.startsWith(QLatin1String("page-"))) {
		if (g_shellHost)
			ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.quickAction && RKShell.quickAction('" + view.toStdString() + "', '" + id.mid(5).toStdString() + "');");
		return;
	}
	if (id == QLatin1String("save-clip")) {
		if (ClipBufferActive())
			ClipBufferSave();
	} else if (id == QLatin1String("record")) {
		ToggleRecording();
	} else if (id == QLatin1String("screenshot")) {
		obs_frontend_take_screenshot();
	} else if (id == QLatin1String("preview") && view == QLatin1String("projector")) {
		ShellProjectorSetPreview(!ShellPreviewTarget());
	} else if (id == QLatin1String("obs-save") && view == QLatin1String("obs-settings")) {
		ClickObsSettingsButton(QDialogButtonBox::Ok);
	} else if (id == QLatin1String("obs-cancel") && view == QLatin1String("obs-settings")) {
		ClickObsSettingsButton(QDialogButtonBox::Cancel);
	}
}

} // namespace rk
