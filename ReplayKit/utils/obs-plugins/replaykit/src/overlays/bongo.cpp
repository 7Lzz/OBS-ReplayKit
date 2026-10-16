#include "prelude.hpp"
#include "overlays/bongo.hpp"
#include "overlays/bongo_animation.hpp"
#include "overlays/bongo_input.hpp"
#include "overlays/bongo_model.hpp"

#include <array>
#include <memory>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <graphics/image-file.h>

namespace rk {
namespace {

// Bongobs-Cat redrawn through obs graphics: the same Bango Cat folder, modes, sprites and live2d models, every property of its source, and View::Render layer for layer, without its own opengl window

// the opengl window of the plugin is 1280x768 and it reads the source size out of its bottom left corner, so everything is laid out in that frame
constexpr float FrameWidth = 1280, FrameHeight = 768;

// Define.cpp KeyDefine, the names mode configs use, with the virtual key Hook.cpp HookCode reads for each; HookCode gives VK_RIGHT to left and VK_LEFT to right, and that swap is kept
struct KeyName {
	const char *name;
	int vk;
};
constexpr std::array<KeyName, 61> KeyDefine = {{
	{"a", 'A'}, {"b", 'B'}, {"c", 'C'}, {"d", 'D'}, {"e", 'E'}, {"f", 'F'}, {"g", 'G'}, {"h", 'H'}, {"i", 'I'}, {"j", 'J'}, {"k", 'K'}, {"l", 'L'},
	{"m", 'M'}, {"n", 'N'}, {"o", 'O'}, {"p", 'P'}, {"q", 'Q'}, {"r", 'R'}, {"s", 'S'}, {"t", 'T'}, {"u", 'U'}, {"v", 'V'}, {"w", 'W'}, {"x", 'X'},
	{"y", 'Y'}, {"z", 'Z'}, {"0", '0'}, {"1", '1'}, {"2", '2'}, {"3", '3'}, {"4", '4'}, {"5", '5'}, {"6", '6'}, {"7", '7'}, {"8", '8'}, {"9", '9'},
	{"space", VK_SPACE}, {"lshift", VK_LSHIFT}, {"lctrl", VK_LCONTROL},
	{"f1", VK_F1}, {"f2", VK_F2}, {"f3", VK_F3}, {"f4", VK_F4}, {"f5", VK_F5}, {"f6", VK_F6}, {"f7", VK_F7}, {"f8", VK_F8}, {"f9", VK_F9}, {"f10", VK_F10}, {"f11", VK_F11}, {"f12", VK_F12},
	{"up", VK_UP}, {"down", VK_DOWN}, {"left", VK_RIGHT}, {"right", VK_LEFT}, {"<", VK_OEM_COMMA}, {">", VK_OEM_PERIOD}, {"[", VK_OEM_4}, {"]", VK_OEM_6},
	{"rshift", VK_RSHIFT}, {"rctrl", VK_RCONTROL},
}};

int KeyIndex(const QString &name)
{
	for (size_t i = 0; i < KeyDefine.size(); ++i) {
		if (name == QLatin1String(KeyDefine[i].name))
			return int(i);
	}
	return -1;
}

// premultiplication stays off like the plugins sprite and cubism shaders; opacity carries the fade of a drawable
const char *EffectSource = R"(
uniform float4x4 ViewProj;
uniform texture2d image;
uniform float opacity;
sampler_state textureSampler { Filter = Linear; AddressU = Clamp; AddressV = Clamp; };
struct VertData { float4 pos : POSITION; float2 uv : TEXCOORD0; };
VertData VSDraw(VertData v_in) { VertData v_out; v_out.pos = mul(float4(v_in.pos.xyz, 1.0), ViewProj); v_out.uv = v_in.uv; return v_out; }
float4 PSDraw(VertData v_in) : TARGET { float4 color = image.Sample(textureSampler, v_in.uv); color.a *= opacity; return color; }
technique Draw { pass { vertex_shader = VSDraw(v_in); pixel_shader = PSDraw(v_in); } }
)";

struct Image {
	gs_image_file_t file{};
	bool Load(const QString &path)
	{
		const QByteArray utf8 = path.toUtf8();
		gs_image_file_init(&file, utf8.constData());
		if (file.loaded)
			gs_image_file_init_texture(&file);
		return file.texture != nullptr;
	}
	~Image() { gs_image_file_free(&file); }
};

struct ModelSlot {
	BongoModel model;
	QStringList blinkIds;
	BongoAnimation animation;
	~ModelSlot() { model.Free(); }
};

// one entry of mode/config.json, read like InfoReader::ModeFromConfig
struct Mode {
	QString name;
	std::unique_ptr<Image> back, catBack, leftUp, rightUp;
	std::vector<std::unique_ptr<Image>> keys, leftHands, rightHands;
	std::vector<int> keyUse;
	bool hasModel = false, hasLeftHandModel = false, hasRightHandModel = false;
	std::unique_ptr<ModelSlot> cat, leftModel, rightModel;
};

struct Settings {
	QString mode;
	double x = 0, y = 0.02, scale = 1.83, delay = 1;
	int width = 1280, height = 768;
	bool breath = true, eyeBlink = true, track = true, live2d = true, relative = false, horizontalFlip = true, verticalFlip = true, mask = false;
};

QString BangoCatFolder()
{
	return QDir(QCoreApplication::applicationDirPath()).filePath(QStringLiteral("Bango Cat"));
}

QJsonObject ReadJson(const QString &path)
{
	QFile file(path);
	if (!file.open(QIODevice::ReadOnly) || file.size() > 1024 * 1024)
		return QJsonObject();
	return QJsonDocument::fromJson(file.readAll()).object();
}

QStringList Strings(const QJsonValue &value)
{
	QStringList out;
	for (const auto &entry : value.toArray())
		out.append(entry.toString());
	return out;
}

// a model folder of a mode: its first *.model3.json (Pal::GetModelName), the moc3 and texture it names, and its EyeBlink group
std::unique_ptr<ModelSlot> LoadModel(const QString &folder, const QString &bakes)
{
	const QStringList found = QDir(folder).entryList({QStringLiteral("*.model3.json")}, QDir::Files, QDir::Name);
	if (found.isEmpty())
		return nullptr;
	const QJsonObject setting = ReadJson(QDir(folder).filePath(found.first()));
	const QJsonObject files = setting["FileReferences"].toObject();
	const QStringList textures = Strings(files["Textures"]);
	if (files["Moc"].toString().isEmpty() || textures.isEmpty())
		return nullptr;
	auto slot = std::make_unique<ModelSlot>();
	for (const auto &group : setting["Groups"].toArray()) {
		const QJsonObject object = group.toObject();
		if (object["Target"].toString() == QLatin1String("Parameter") && object["Name"].toString() == QLatin1String("EyeBlink"))
			slot->blinkIds = Strings(object["Ids"]);
	}
	if (!slot->model.Load(QDir(folder).filePath(files["Moc"].toString()), QDir(folder).filePath(textures.first()), bakes)) {
		blog(LOG_WARNING, "[replaykit] bongo cat: no matching baked model for %s", QDir(folder).filePath(files["Moc"].toString()).toUtf8().constData());
		return nullptr;
	}
	return slot;
}

std::unique_ptr<Image> LoadImage(const QString &path)
{
	auto image = std::make_unique<Image>();
	if (!image->Load(path)) {
		blog(LOG_WARNING, "[replaykit] bongo cat: missing picture %s", path.toUtf8().constData());
		return nullptr;
	}
	return image;
}

std::unique_ptr<Mode> LoadMode(const QString &root, const QString &name, const QString &bakes)
{
	const QString dir = QDir(root).filePath(QStringLiteral("mode/") + name);
	const QJsonObject config = ReadJson(QDir(dir).filePath(QStringLiteral("config.json")));
	if (config.isEmpty())
		return nullptr;
	auto mode = std::make_unique<Mode>();
	mode->name = name;
	auto at = [&](const QString &folder, const QString &file) { return QDir(QDir(dir).filePath(folder)).filePath(file); };
	if (!config["BackgroundImageName"].toString().isEmpty())
		mode->back = LoadImage(QDir(dir).filePath(config["BackgroundImageName"].toString()));
	if (!config["CatBackgroundImageName"].toString().isEmpty())
		mode->catBack = LoadImage(QDir(dir).filePath(config["CatBackgroundImageName"].toString()));

	// KeysCount is the shorter of the two lists
	const QStringList keyImages = Strings(config["KeysImageName"]), keyUse = Strings(config["KeyUse"]);
	const int keysCount = int(std::min(keyImages.size(), keyUse.size()));
	for (int i = 0; i < keysCount; ++i) {
		mode->keys.push_back(LoadImage(at(config["KeysImagePath"].toString(), keyImages[i])));
		mode->keyUse.push_back(KeyIndex(keyUse[i]));
	}

	const QString leftFolder = config["LeftHandImagePath"].toString(), rightFolder = config["RightHandImagePath"].toString();
	if (!leftFolder.isEmpty()) {
		if (!config["LeftHandUpImageName"].toString().isEmpty())
			mode->leftUp = LoadImage(at(leftFolder, config["LeftHandUpImageName"].toString()));
		for (const QString &file : Strings(config["LeftHandImageName"]))
			mode->leftHands.push_back(LoadImage(at(leftFolder, file)));
	}
	if (!rightFolder.isEmpty()) {
		if (!config["RightHandUpImageName"].toString().isEmpty())
			mode->rightUp = LoadImage(at(rightFolder, config["RightHandUpImageName"].toString()));
		for (const QString &file : Strings(config["RightHandImageName"]))
			mode->rightHands.push_back(LoadImage(at(rightFolder, file)));
	}

	mode->hasModel = config["HasModel"].toBool();
	mode->hasLeftHandModel = config["ModelHasLeftHandModel"].toBool();
	mode->hasRightHandModel = config["ModelHasRightHandModel"].toBool();
	if (!config["CatModelPath"].toString().isEmpty())
		mode->cat = LoadModel(QDir(dir).filePath(config["CatModelPath"].toString()), bakes);
	if (!config["ModelLeftHandModelPath"].toString().isEmpty())
		mode->leftModel = LoadModel(QDir(dir).filePath(config["ModelLeftHandModelPath"].toString()), bakes);
	if (!config["ModelRightHandModelPath"].toString().isEmpty())
		mode->rightModel = LoadModel(QDir(dir).filePath(config["ModelRightHandModelPath"].toString()), bakes);
	return mode;
}

struct Bongo {
	obs_source_t *source;
	gs_effect_t *effect = nullptr;
	std::vector<std::unique_ptr<Mode>> modes;
	std::vector<std::unique_ptr<Image>> faces;
	std::vector<int> faceKeys;
	std::unique_ptr<BongoInput> input;

	std::mutex settingsLock;
	Settings settings;

	// graphics thread only from here
	size_t current = 0;
	size_t currentFace = 0;
	std::array<bool, KeyDefine.size()> pressed{};
	// relative mouse is the plugins window of one screen around the accumulated raw motion, which is the same as a position clamped to the screen that starts in the middle
	float mouseX = 0.5f, mouseY = 0.5f;
	bool relativeBefore = false;
	bool left = false, right = false;

	explicit Bongo(obs_data_t *data, obs_source_t *owner) : source(owner)
	{
		const QString replaykit = qEnvironmentVariable("APPDATA") + "/obs-studio/obs-replayKit/";
		input = std::make_unique<BongoInput>();
		const QString bakes = replaykit + "obs-custom-dock/overlay-preview/bongo-models";
		const QString root = BangoCatFolder();
		ReadSettings(data, "delaytime");

		obs_enter_graphics();
		char *errors = nullptr;
		effect = gs_effect_create(EffectSource, "replaykit-bongo.effect", &errors);
		if (!effect)
			blog(LOG_ERROR, "[replaykit] bongo cat: effect failed: %s", errors ? errors : "");
		bfree(errors);
		// InfoReader::InitFromConfig: a listed mode whose config cannot be read is skipped
		for (const QString &name : Strings(ReadJson(QDir(root).filePath(QStringLiteral("mode/config.json")))["ModelPath"])) {
			if (auto mode = LoadMode(root, name, bakes))
				modes.push_back(std::move(mode));
		}
		const QJsonObject face = ReadJson(QDir(root).filePath(QStringLiteral("face/config.json")));
		const QStringList hotKeys = Strings(face["HotKey"]), faceImages = Strings(face["FaceImageName"]);
		for (int i = 0; i < std::min(hotKeys.size(), faceImages.size()); ++i) {
			faces.push_back(LoadImage(QDir(root).filePath(QStringLiteral("face/") + faceImages[i])));
			faceKeys.push_back(KeyIndex(hotKeys[i]));
		}
		obs_leave_graphics();
		if (modes.empty())
			blog(LOG_ERROR, "[replaykit] bongo cat: no mode could be read from %s", root.toUtf8().constData());
	}

	~Bongo()
	{
		input.reset();
		obs_enter_graphics();
		modes.clear();
		faces.clear();
		if (effect)
			gs_effect_destroy(effect);
		obs_leave_graphics();
	}

	// VtuberCreate reads the speed from delaytime and Vtuber_update from delay, and both are kept
	void ReadSettings(obs_data_t *data, const char *speedKey)
	{
		Settings next;
		next.mode = QString::fromUtf8(obs_data_get_string(data, "mode"));
		next.x = obs_data_get_double(data, "x");
		next.y = obs_data_get_double(data, "y");
		next.scale = obs_data_get_double(data, "scale");
		next.delay = obs_data_get_double(data, speedKey);
		next.width = int(obs_data_get_int(data, "width")) / 32 * 32;
		next.height = int(obs_data_get_int(data, "height"));
		next.breath = obs_data_get_bool(data, "breath");
		next.eyeBlink = obs_data_get_bool(data, "eyeblink");
		next.track = obs_data_get_bool(data, "track");
		next.live2d = obs_data_get_bool(data, "live2d");
		next.relative = obs_data_get_bool(data, "relative_mouse");
		next.horizontalFlip = obs_data_get_bool(data, "mouse_horizontal_flip");
		next.verticalFlip = obs_data_get_bool(data, "mouse_vertical_flip");
		next.mask = obs_data_get_bool(data, "mask");
		std::lock_guard<std::mutex> guard(settingsLock);
		settings = next;
	}

	Settings Current()
	{
		std::lock_guard<std::mutex> guard(settingsLock);
		return settings;
	}

	// View::TranslateKey: where a KeyDefine key sits in the KeyUse list of the mode
	int TranslateKey(const Mode &mode, size_t key) const
	{
		for (size_t j = 0; j < mode.keyUse.size(); ++j) {
			if (mode.keyUse[j] == int(key))
				return int(j);
		}
		return -1;
	}

	void Tick(float seconds)
	{
		const Settings now = Current();
		// a mode name that matches nothing keeps the mode that was picked before, like VtuberDelegate::ChangeMode
		for (size_t i = 0; i < modes.size(); ++i) {
			if (modes[i]->name == now.mode)
				current = i;
		}
		const bool showing = obs_source_showing(source) && !modes.empty();
		input->SetActive(showing && now.relative);
		if (!showing)
			return;

		for (size_t i = 0; i < KeyDefine.size(); ++i)
			pressed[i] = (GetAsyncKeyState(KeyDefine[i].vk) & 0x8000) != 0;
		// GetAsyncKeyState reads the physical buttons while the plugins mouse hook saw the logical ones
		const bool physicalLeft = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0, physicalRight = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
		const bool swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
		left = swapped ? physicalRight : physicalLeft;
		right = swapped ? physicalLeft : physicalRight;

		// Pal::GetDesktopResolution is the desktop window, the primary screen
		RECT desktop;
		GetWindowRect(GetDesktopWindow(), &desktop);
		const float width = float(desktop.right), height = float(desktop.bottom);
		int64_t dx, dy;
		input->Take(dx, dy);
		if (now.relative && !relativeBefore) {
			mouseX = 0.5f;
			mouseY = 0.5f;
		}
		relativeBefore = now.relative;
		if (width > 0 && height > 0) {
			if (now.relative) {
				mouseX = std::clamp(mouseX + float(dx) / width, 0.0f, 1.0f);
				mouseY = std::clamp(mouseY + float(dy) / height, 0.0f, 1.0f);
			} else {
				POINT cursor;
				if (GetCursorPos(&cursor)) {
					mouseX = float(cursor.x) / width;
					mouseY = float(cursor.y) / height;
				}
			}
		}

		// View::ReanderMask: the first held face hotkey in KeyDefine order picks the face, and it stays picked
		for (size_t i = 0; i < KeyDefine.size(); ++i) {
			if (!pressed[i])
				continue;
			const auto it = std::find(faceKeys.begin(), faceKeys.end(), int(i));
			if (it != faceKeys.end()) {
				currentFace = size_t(it - faceKeys.begin());
				break;
			}
		}

		// Model::Update runs its clocks at delta * speed and only for the models that are drawn
		Mode &mode = *modes[current];
		const double delta = double(seconds) * now.delay;
		if (now.live2d && mode.hasModel && mode.cat)
			mode.cat->animation.Tick(delta);
		if (!now.live2d && mode.leftHands.empty() && mode.hasLeftHandModel && mode.leftModel)
			mode.leftModel->animation.Tick(delta);
		if (!now.live2d && mode.rightHands.empty() && mode.hasRightHandModel && mode.rightModel)
			mode.rightModel->animation.Tick(delta);
	}

	void Draw(gs_eparam_t *image, gs_eparam_t *opacity, Image *picture)
	{
		if (!picture)
			return;
		gs_effect_set_texture(image, picture->file.texture);
		gs_effect_set_float(opacity, 1.0f);
		while (gs_effect_loop(effect, "Draw"))
			gs_draw_sprite(picture->file.texture, 0, uint32_t(FrameWidth), uint32_t(FrameHeight));
	}

	void DrawModel(ModelSlot *slot, const Settings &now)
	{
		if (!slot)
			return;
		BongoMotionInput motion;
		motion.mouseX = mouseX;
		motion.mouseY = mouseY;
		motion.left = left;
		motion.right = right;
		motion.breath = now.breath;
		motion.eyeBlink = now.eyeBlink;
		motion.track = now.track;
		motion.horizontalFlip = now.horizontalFlip;
		motion.verticalFlip = now.verticalFlip;
		// View::UpdataViewData: scale 0.615 / 1.1 times the source scale, moved by x, y and by how much smaller the source is than the frame
		BongoModel::Camera camera;
		camera.scaleX = float(0.615 * now.scale);
		camera.scaleY = float(1.1 * now.scale);
		camera.offsetX = float(now.x - (FrameWidth - now.width) / FrameWidth);
		camera.offsetY = float(now.y - (FrameHeight - now.height) / FrameHeight);
		camera.width = FrameWidth;
		camera.height = FrameHeight;
		slot->model.Render(effect, slot->animation.Values(slot->model, slot->blinkIds, motion), camera);
	}

	// View::Render with live2d as the source asks: background, held keys, the cat, the hands or hand models, the face, then the raised hands
	void Render()
	{
		const Settings now = Current();
		if (!effect || modes.empty() || now.width <= 0 || now.height <= 0)
			return;
		Mode &mode = *modes[current];
		gs_eparam_t *image = gs_effect_get_param_by_name(effect, "image");
		gs_eparam_t *opacity = gs_effect_get_param_by_name(effect, "opacity");
		gs_blend_state_push();
		gs_enable_blending(true);
		gs_blend_function_separate(GS_BLEND_SRCALPHA, GS_BLEND_INVSRCALPHA, GS_BLEND_ONE, GS_BLEND_INVSRCALPHA);
		// the source is the bottom left width x height of the frame
		gs_matrix_push();
		gs_matrix_translate3f(0.0f, float(now.height) - FrameHeight, 0.0f);

		Draw(image, opacity, now.live2d && mode.hasModel ? mode.back.get() : mode.catBack.get());
		for (size_t i = 0; i < KeyDefine.size(); ++i) {
			const int key = pressed[i] ? TranslateKey(mode, i) : -1;
			if (key >= 0)
				Draw(image, opacity, mode.keys[key].get());
		}
		if (now.live2d && mode.hasModel)
			DrawModel(mode.cat.get(), now);

		bool leftUp = true, rightUp = true;
		if (!mode.leftHands.empty()) {
			for (size_t i = 0; i < KeyDefine.size(); ++i) {
				const int key = pressed[i] ? TranslateKey(mode, i) : -1;
				if (key >= 0 && key < int(mode.leftHands.size())) {
					Draw(image, opacity, mode.leftHands[key].get());
					leftUp = false;
					break;
				}
			}
		} else if (mode.hasLeftHandModel) {
			if (!now.live2d)
				DrawModel(mode.leftModel.get(), now);
			leftUp = false;
		} else if (now.live2d) {
			leftUp = false;
		}
		if (!mode.rightHands.empty()) {
			for (size_t i = 0; i < KeyDefine.size(); ++i) {
				const int key = pressed[i] ? TranslateKey(mode, i) - int(mode.leftHands.size()) : -1;
				if (key >= 0 && key < int(mode.rightHands.size())) {
					Draw(image, opacity, mode.rightHands[key].get());
					rightUp = false;
					break;
				}
			}
		} else if (mode.hasRightHandModel) {
			if (!now.live2d)
				DrawModel(mode.rightModel.get(), now);
			rightUp = false;
		} else if (now.live2d) {
			rightUp = false;
		}
		if (now.mask && currentFace < faces.size())
			Draw(image, opacity, faces[currentFace].get());
		if (leftUp)
			Draw(image, opacity, mode.leftUp.get());
		if (rightUp)
			Draw(image, opacity, mode.rightUp.get());

		gs_matrix_pop();
		gs_blend_state_pop();
	}

};

// the properties of VtuberGetProperties in its order; the mode list is the readable modes of the config, so it also works when obs asks without a source
obs_properties_t *Properties()
{
	obs_properties_t *props = obs_properties_create();
	obs_property_t *list = obs_properties_add_list(props, "mode", "Mode", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_STRING);
	const QString root = BangoCatFolder();
	for (const QString &name : Strings(ReadJson(QDir(root).filePath(QStringLiteral("mode/config.json")))["ModelPath"])) {
		if (ReadJson(QDir(root).filePath(QStringLiteral("mode/") + name + QStringLiteral("/config.json"))).isEmpty())
			continue;
		const QByteArray utf8 = name.toUtf8();
		obs_property_list_add_string(list, utf8.constData(), utf8.constData());
	}
	obs_properties_add_bool(props, "relative_mouse", "Relative Mouse Movement");
	obs_properties_add_bool(props, "mouse_horizontal_flip", "Mouse Horizontal Flip");
	obs_properties_add_bool(props, "mouse_vertical_flip", "Mouse Vertical Flip");
	obs_properties_add_bool(props, "mask", "Use Mask");
	obs_properties_add_bool(props, "live2d", "Live2D");
	obs_properties_add_float_slider(props, "delay", "Speed", 0.0, 10.0, 0.1);
	// kept for the same settings, but no Bango Cat model ships motions, so it changes nothing in the plugin either
	obs_properties_add_bool(props, "random_motion", "Random Motion");
	obs_properties_add_bool(props, "breath", "Breath");
	obs_properties_add_bool(props, "eyeblink", "Eye Blink");
	obs_properties_add_bool(props, "track", "Mouse Tracking");
	return props;
}

} // namespace

void RegisterBongo()
{
	obs_source_info info{};
	info.id = "replaykit-bongo";
	info.type = OBS_SOURCE_TYPE_INPUT;
	info.output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_CUSTOM_DRAW;
	info.get_name = [](void *) { return "Bongo Cat"; };
	info.create = [](obs_data_t *settings, obs_source_t *source) -> void * { return new Bongo(settings, source); };
	info.destroy = [](void *data) { delete static_cast<Bongo *>(data); };
	info.update = [](void *data, obs_data_t *settings) { static_cast<Bongo *>(data)->ReadSettings(settings, "delay"); };
	// Vtuber_defaults, including its capital Mode next to the lowercase mode it actually reads
	info.get_defaults = [](obs_data_t *settings) {
		obs_data_set_default_int(settings, "width", 1280);
		obs_data_set_default_int(settings, "height", 768);
		obs_data_set_default_double(settings, "x", 0);
		obs_data_set_default_double(settings, "y", 0.02);
		obs_data_set_default_double(settings, "scale", 1.83);
		obs_data_set_default_double(settings, "delaytime", 1.0);
		obs_data_set_default_bool(settings, "random_motion", true);
		obs_data_set_default_bool(settings, "breath", true);
		obs_data_set_default_bool(settings, "eyeblink", true);
		obs_data_set_default_bool(settings, "track", true);
		obs_data_set_default_string(settings, "Mode", "standard");
		obs_data_set_default_bool(settings, "live2d", true);
		obs_data_set_default_bool(settings, "relative_mouse", false);
		obs_data_set_default_bool(settings, "mouse_horizontal_flip", true);
		obs_data_set_default_bool(settings, "mouse_vertical_flip", true);
		obs_data_set_default_bool(settings, "mask", false);
	};
	info.get_properties = [](void *) { return Properties(); };
	info.get_width = [](void *data) {
		const Settings now = static_cast<Bongo *>(data)->Current();
		return uint32_t(std::max(0, now.width));
	};
	info.get_height = [](void *data) {
		const Settings now = static_cast<Bongo *>(data)->Current();
		return uint32_t(std::max(0, now.height));
	};
	info.video_tick = [](void *data, float seconds) { static_cast<Bongo *>(data)->Tick(seconds); };
	info.video_render = [](void *data, gs_effect_t *) { static_cast<Bongo *>(data)->Render(); };
	obs_register_source(&info);
}

} // namespace rk
