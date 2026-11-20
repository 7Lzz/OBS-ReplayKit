// the game out of focus card: the last game picture of the program, blurred and dimmed, with the monitor icon and the words game out of focus and video unavailable over it; the capture switch script shows it in place of the captures while a game of the auto game list is open but not in front, so the stream and the clips never show what is behind the game
#include "prelude.hpp"
#include "overlays/focus_card.hpp"
#include "core/draw_util.hpp"
#include "core/json_lite.hpp"
#include "core/locale.hpp"
#include "core/shared_values.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "core/ui_font.hpp"

#include <graphics/vec4.h>

#include <QFontMetricsF>
#include <QImage>
#include <QPainter>
#include <QSvgRenderer>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>

namespace rk {

namespace {

// the managed captures the card covers
constexpr const char *kCaptureNames[] = {kDisplayCaptureSourceName, kGameCaptureSourceName, kWindowCaptureSourceName};
// how dark the frozen game picture gets under the words
constexpr float kDim = 0.58f;

// the words and the icon, drawn by the qt thread on a clear picture at the canvas size, and a count of how often it was drawn that each card source compares to the one it uploaded; the ground is what the card shows with no game picture yet
std::mutex g_overlayLock;
QImage g_overlay;
uint64_t g_overlayGeneration = 0;
QColor g_ground;
QString g_overlaySignature;
QTimer *g_redrawTimer = nullptr;
std::atomic<bool> g_rendering{false};

// the card only ever follows a listed game the window capture shows, so the program is only copied while the switch is on and that capture is in the program; each time copying starts is a new epoch, and a picture of an earlier one is never shown
bool g_switchOn = false;
std::atomic<bool> g_copying{false};
std::atomic<uint64_t> g_copyEpoch{0};
uint64_t g_backdropEpoch = 0;

// the program picture at a quarter of the canvas, kept every frame the card is not in it, so the card freezes on the last game picture before it showed; it and the blur passes belong to the graphics thread
std::atomic<uint64_t> g_cardRenderedAt{0};
uint64_t g_lastCopied = 0;
uint64_t g_backdropGeneration = 0;
uint64_t g_blurredGeneration = 0;
gs_texrender_t *g_half = nullptr;
gs_texrender_t *g_quarter = nullptr;
gs_texrender_t *g_eighth = nullptr;
gs_texrender_t *g_sixteenth = nullptr;
gs_texrender_t *g_eighthUp = nullptr;
gs_texrender_t *g_quarterUp = nullptr;

QImage DrawOverlay(int width, int height)
{
	QImage overlay(width, height, QImage::Format_RGBA8888);
	overlay.fill(Qt::transparent);
	QPainter painter(&overlay);
	painter.setRenderHints(QPainter::Antialiasing | QPainter::SmoothPixmapTransform | QPainter::TextAntialiasing);

	// sizes follow the canvas height so the card looks the same at any resolution
	const qreal iconSide = height * 0.28;
	QFont title(UiFontFamily());
	title.setPixelSize(qMax(8, qRound(height * 0.062)));
	title.setWeight(QFont::Bold);
	QFont note(UiFontFamily());
	note.setPixelSize(qMax(8, qRound(height * 0.04)));
	const QString titleText = TrMessage("capture.gameOutOfFocus");
	const QString noteText = TrMessage("capture.videoUnavailable");
	const QFontMetricsF titleMetrics(title), noteMetrics(note);
	const qreal gapAfterIcon = height * 0.035, gapAfterTitle = height * 0.008;
	const qreal total = iconSide + gapAfterIcon + titleMetrics.height() + gapAfterTitle + noteMetrics.height();
	qreal y = (height - total) / 2.0;

	QSvgRenderer svg(IconPath(QStringLiteral("ui/monitor-search.svg")));
	if (svg.isValid()) {
		// drawn white, then given its colour through its own alpha, since qt has no css colour for currentColor
		const int side = qMax(1, qCeil(iconSide));
		QImage icon(side, side, QImage::Format_ARGB32_Premultiplied);
		icon.fill(Qt::transparent);
		QPainter tint(&icon);
		tint.setRenderHints(QPainter::Antialiasing | QPainter::SmoothPixmapTransform);
		svg.render(&tint, QRectF(0.0, 0.0, side, side));
		tint.setCompositionMode(QPainter::CompositionMode_SourceIn);
		tint.fillRect(icon.rect(), QColor(0xd2, 0xd2, 0xd4, 0xec));
		tint.end();
		painter.drawImage(QRectF((width - iconSide) / 2.0, y, iconSide, iconSide), icon);
	} else {
		blog(LOG_WARNING, "[replaykit] the focus card icon could not be read");
	}
	y += iconSide + gapAfterIcon;

	painter.setFont(title);
	painter.setPen(QColor(0xf4, 0xf4, 0xf5));
	painter.drawText(QRectF(0.0, y, width, titleMetrics.height()), Qt::AlignHCenter | Qt::AlignVCenter, titleText);
	y += titleMetrics.height() + gapAfterTitle;
	painter.setFont(note);
	painter.setPen(QColor(0xf4, 0xf4, 0xf5, 0xb8));
	painter.drawText(QRectF(0.0, y, width, noteMetrics.height()), Qt::AlignHCenter | Qt::AlignVCenter, noteText);
	painter.end();
	return overlay;
}

bool WindowCaptureShown()
{
	obs_source_t *capture = obs_get_source_by_name(kWindowCaptureSourceName);
	const bool shown = capture && obs_source_active(capture);
	obs_source_release(capture);
	return shown;
}

void UpdateCopying()
{
	const bool wanted = g_switchOn && WindowCaptureShown();
	if (wanted && !g_copying.load())
		g_copyEpoch++;
	g_copying.store(wanted);
}

// the words are drawn again only when the canvas size or the font changed; the ground follows the theme, which the settings poll brings
void RedrawIfChanged()
{
	obs_video_info ovi{};
	if (!obs_get_video_info(&ovi) || ovi.base_width < 16 || ovi.base_height < 16)
		return;
	const QString signature = QStringLiteral("%1x%2|%3").arg(ovi.base_width).arg(ovi.base_height).arg(UiFontFamily());
	QImage overlay;
	if (signature != g_overlaySignature) {
		g_overlaySignature = signature;
		overlay = DrawOverlay(int(ovi.base_width), int(ovi.base_height));
	}
	std::lock_guard<std::mutex> hold(g_overlayLock);
	g_ground = g_chromeBg;
	if (!overlay.isNull()) {
		g_overlay = std::move(overlay);
		g_overlayGeneration++;
	}
}

uint32_t Half(uint32_t side, uint32_t times)
{
	return std::max<uint32_t>(1, side >> times);
}

// one texture drawn into another at the given size; the default sampler is linear, so halving a side averages each two by two block
gs_texture_t *Pass(gs_texrender_t *&target, gs_texture_t *from, uint32_t cx, uint32_t cy)
{
	if (!from)
		return nullptr;
	if (!target)
		target = gs_texrender_create(GS_BGRA, GS_ZS_NONE);
	gs_texrender_reset(target);
	if (!gs_texrender_begin(target, cx, cy))
		return nullptr;
	struct vec4 clear;
	vec4_zero(&clear);
	gs_clear(GS_CLEAR_COLOR, &clear, 0.0f, 0);
	gs_ortho(0.0f, float(cx), 0.0f, float(cy), -100.0f, 100.0f);
	gs_effect_t *effect = obs_get_base_effect(OBS_EFFECT_DEFAULT);
	gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), from);
	while (gs_effect_loop(effect, "Draw"))
		gs_draw_sprite(from, 0, cx, cy);
	gs_texrender_end(target);
	return gs_texrender_get_texture(target);
}

// after obs drew the program: keeps a quarter size copy of it, unless the card was in it this frame
void OnMainRendered(void *)
{
	const uint64_t frame = obs_get_video_frame_time();
	if (frame == g_lastCopied || !g_rendering || !g_copying.load())
		return;
	g_lastCopied = frame;
	if (g_cardRenderedAt.load() == frame || !obs_get_main_texture())
		return;
	obs_video_info ovi{};
	if (!obs_get_video_info(&ovi) || ovi.base_width < 16 || ovi.base_height < 16)
		return;
	const bool srgb = gs_framebuffer_srgb_enabled();
	gs_enable_framebuffer_srgb(false);
	gs_blend_state_push();
	gs_enable_blending(false);
	if (!g_half)
		g_half = gs_texrender_create(GS_BGRA, GS_ZS_NONE);
	gs_texrender_reset(g_half);
	gs_texture_t *half = nullptr;
	if (gs_texrender_begin(g_half, Half(ovi.base_width, 1), Half(ovi.base_height, 1))) {
		struct vec4 clear;
		vec4_zero(&clear);
		gs_clear(GS_CLEAR_COLOR, &clear, 0.0f, 0);
		gs_ortho(0.0f, float(ovi.base_width), 0.0f, float(ovi.base_height), -100.0f, 100.0f);
		obs_render_main_texture_src_color_only();
		gs_texrender_end(g_half);
		half = gs_texrender_get_texture(g_half);
	}
	if (Pass(g_quarter, half, Half(ovi.base_width, 2), Half(ovi.base_height, 2))) {
		g_backdropGeneration++;
		g_backdropEpoch = g_copyEpoch.load();
	}
	gs_blend_state_pop();
	gs_enable_framebuffer_srgb(srgb);
}

// the frozen picture blurred by halving it twice more and growing it back, once per picture
gs_texture_t *Blurred()
{
	if (!g_backdropGeneration || !g_quarter || g_backdropEpoch != g_copyEpoch.load())
		return nullptr;
	if (g_blurredGeneration != g_backdropGeneration || !g_quarterUp) {
		gs_texture_t *quarter = gs_texrender_get_texture(g_quarter);
		const uint32_t qw = gs_texture_get_width(quarter), qh = gs_texture_get_height(quarter);
		gs_blend_state_push();
		gs_enable_blending(false);
		gs_texture_t *eighth = Pass(g_eighth, quarter, Half(qw, 1), Half(qh, 1));
		gs_texture_t *sixteenth = Pass(g_sixteenth, eighth, Half(qw, 2), Half(qh, 2));
		gs_texture_t *eighthUp = Pass(g_eighthUp, sixteenth, Half(qw, 1), Half(qh, 1));
		gs_texture_t *done = Pass(g_quarterUp, eighthUp, qw, qh);
		gs_blend_state_pop();
		if (!done)
			return nullptr;
		g_blurredGeneration = g_backdropGeneration;
	}
	return gs_texrender_get_texture(g_quarterUp);
}

void DrawSolid(const QColor &colour, float alpha, uint32_t cx, uint32_t cy)
{
	gs_effect_t *effect = obs_get_base_effect(OBS_EFFECT_SOLID);
	struct vec4 value;
	vec4_set(&value, float(colour.redF()), float(colour.greenF()), float(colour.blueF()), alpha);
	gs_effect_set_vec4(gs_effect_get_param_by_name(effect, "color"), &value);
	while (gs_effect_loop(effect, "Solid"))
		gs_draw_sprite(nullptr, 0, cx, cy);
}

void DrawTexture(gs_texture_t *texture, uint32_t cx, uint32_t cy)
{
	gs_effect_t *effect = obs_get_base_effect(OBS_EFFECT_DEFAULT);
	gs_effect_set_texture(gs_effect_get_param_by_name(effect, "image"), texture);
	while (gs_effect_loop(effect, "Draw"))
		gs_draw_sprite(texture, 0, cx, cy);
}

class FocusCard {
public:
	~FocusCard()
	{
		if (!overlay_)
			return;
		obs_enter_graphics();
		gs_texture_destroy(overlay_);
		obs_leave_graphics();
	}

	// the canvas size, so the item fills the canvas at scale one whatever the canvas is
	static uint32_t Width()
	{
		obs_video_info ovi{};
		return obs_get_video_info(&ovi) ? ovi.base_width : 0;
	}
	static uint32_t Height()
	{
		obs_video_info ovi{};
		return obs_get_video_info(&ovi) ? ovi.base_height : 0;
	}

	// runs on the graphics thread, which is the one that may make textures
	void Render()
	{
		g_cardRenderedAt.store(obs_get_video_frame_time());
		const uint32_t cx = Width(), cy = Height();
		if (!cx || !cy)
			return;
		QColor ground;
		{
			std::lock_guard<std::mutex> hold(g_overlayLock);
			ground = g_ground.isValid() ? g_ground : QColor(Qt::black);
			if (generation_ != g_overlayGeneration && !g_overlay.isNull()) {
				const uint8_t *bits = g_overlay.constBits();
				gs_texture_t *next = gs_texture_create(uint32_t(g_overlay.width()), uint32_t(g_overlay.height()), GS_RGBA, 1, &bits, 0);
				if (next) {
					if (overlay_)
						gs_texture_destroy(overlay_);
					overlay_ = next;
					generation_ = g_overlayGeneration;
				}
			}
		}
		// the copies hold the encoded colours of the program, so they are drawn back without srgb conversion
		const bool srgb = gs_framebuffer_srgb_enabled();
		gs_enable_framebuffer_srgb(false);
		gs_texture_t *backdrop = Blurred();
		gs_blend_state_push();
		gs_enable_blending(false);
		if (backdrop)
			DrawTexture(backdrop, cx, cy);
		else
			DrawSolid(ground, 1.0f, cx, cy);
		gs_enable_blending(true);
		gs_blend_function(GS_BLEND_SRCALPHA, GS_BLEND_INVSRCALPHA);
		if (backdrop)
			DrawSolid(QColor(Qt::black), kDim, cx, cy);
		if (overlay_)
			DrawTexture(overlay_, cx, cy);
		gs_blend_state_pop();
		gs_enable_framebuffer_srgb(srgb);
	}

private:
	gs_texture_t *overlay_ = nullptr;
	uint64_t generation_ = 0;
};

struct SceneScan {
	obs_source_t *card = nullptr;
	bool hasCard = false;
	int index = 0;
	int topCapture = -1;
};

bool ScanItem(obs_scene_t *, obs_sceneitem_t *item, void *param)
{
	auto *scan = static_cast<SceneScan *>(param);
	obs_source_t *source = obs_sceneitem_get_source(item);
	if (source == scan->card)
		scan->hasCard = true;
	const char *name = obs_source_get_name(source);
	for (const char *capture : kCaptureNames) {
		// items come bottom first, so the last capture seen is the topmost one
		if (name && std::strcmp(name, capture) == 0)
			scan->topCapture = scan->index;
	}
	scan->index++;
	return true;
}

struct AddToScene {
	obs_source_t *card = nullptr;
	int position = 0;
};

// added hidden and locked inside the scene lock, so no frame ever shows it, and right above the topmost capture, so the overlays stay over it
void AddHidden(void *param, obs_scene_t *scene)
{
	auto *add = static_cast<AddToScene *>(param);
	obs_sceneitem_t *item = obs_scene_add(scene, add->card);
	if (!item)
		return;
	obs_sceneitem_set_visible(item, false);
	obs_sceneitem_set_locked(item, true);
	obs_sceneitem_set_order_position(item, add->position);
}

} // namespace

void RegisterFocusCard()
{
	obs_source_info info{};
	info.id = kFocusCardSourceKind;
	info.type = OBS_SOURCE_TYPE_INPUT;
	info.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_CUSTOM_DRAW | OBS_SOURCE_DO_NOT_DUPLICATE;
	info.get_name = [](void *) -> const char * { return kFocusCardSourceName; };
	info.create = [](obs_data_t *, obs_source_t *) -> void * { return new FocusCard(); };
	info.destroy = [](void *data) { delete static_cast<FocusCard *>(data); };
	info.get_width = [](void *) { return FocusCard::Width(); };
	info.get_height = [](void *) { return FocusCard::Height(); };
	info.video_render = [](void *data, gs_effect_t *) { static_cast<FocusCard *>(data)->Render(); };
	obs_register_source(&info);
}

void FocusCardEnsureScenes()
{
	obs_source_t *card = obs_get_source_by_name(kFocusCardSourceName);
	if (card && std::strcmp(obs_source_get_id(card), kFocusCardSourceKind) != 0) {
		blog(LOG_WARNING, "[replaykit] a source named '%s' that is not the focus card is in the way, so no scene gets the card", kFocusCardSourceName);
		obs_source_release(card);
		return;
	}
	if (!card)
		card = obs_source_create(kFocusCardSourceKind, kFocusCardSourceName, nullptr, nullptr);
	if (!card)
		return;
	obs_frontend_source_list scenes{};
	obs_frontend_get_scenes(&scenes);
	int added = 0;
	for (size_t i = 0; i < scenes.sources.num; i++) {
		obs_scene_t *scene = obs_scene_from_source(scenes.sources.array[i]);
		if (!scene)
			continue;
		SceneScan scan;
		scan.card = card;
		obs_scene_enum_items(scene, ScanItem, &scan);
		if (scan.hasCard || scan.topCapture < 0)
			continue;
		AddToScene add{card, scan.topCapture + 1};
		obs_scene_atomic_update(scene, AddHidden, &add);
		added++;
	}
	obs_frontend_source_list_free(&scenes);
	obs_source_release(card);
	if (added)
		blog(LOG_INFO, "[replaykit] focus card added to %d scene(s)", added);
}

void FocusCardStart()
{
	RedrawIfChanged();
	FocusCardEnsureScenes();
	if (g_redrawTimer)
		return;
	g_rendering = true;
	obs_add_main_rendered_callback(OnMainRendered, nullptr);
	g_redrawTimer = new QTimer(g_callbacks);
	QObject::connect(g_redrawTimer, &QTimer::timeout, g_callbacks, []() {
		RedrawIfChanged();
		UpdateCopying();
	});
	g_redrawTimer->start(1000);
}

void FocusCardApplySettings(const std::string &settingsBody)
{
	if (const auto on = JsonBoolIfPresent(settingsBody, "screenshareAutoGameOutOfFocus"))
		g_switchOn = *on;
}

void FocusCardStop()
{
	if (!g_redrawTimer || !g_rendering)
		return;
	g_redrawTimer->stop();
	obs_remove_main_rendered_callback(OnMainRendered, nullptr);
	// the graphics lock waits for a frame that is still drawing, so nothing uses the copies once they are gone
	obs_enter_graphics();
	g_rendering = false;
	for (gs_texrender_t **render : {&g_half, &g_quarter, &g_eighth, &g_sixteenth, &g_eighthUp, &g_quarterUp}) {
		gs_texrender_destroy(*render);
		*render = nullptr;
	}
	g_backdropGeneration = 0;
	g_blurredGeneration = 0;
	g_copying.store(false);
	obs_leave_graphics();
}

} // namespace rk
