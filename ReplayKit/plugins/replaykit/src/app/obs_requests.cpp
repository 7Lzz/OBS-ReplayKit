// answers the requests the helper makes of obs (sources, filters, scene items, outputs, video and profile settings) over the pipe, in the request and response shapes of obs-websocket v5, which the helper used before
#include "prelude.hpp"
#include "app/obs_requests.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>
#include <atomic>
#include <cmath>
#include <functional>
#include <unordered_map>
#include <util/config-file.h>

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

// the status codes of obs-websocket v5, which the helper reads the same way
enum Code {
	Success = 100,
	MissingRequestField = 300,
	InvalidRequestField = 400,
	OutputRunning = 500,
	OutputNotRunning = 501,
	ResourceNotFound = 600,
	ResourceAlreadyExists = 601,
	InvalidResourceType = 602,
	InvalidResourceState = 604,
	InvalidInputKind = 605,
	InvalidFilterKind = 607,
	ResourceCreationFailed = 700,
	RequestProcessingFailed = 702,
	UnknownRequestType = 204,
};

struct Reply {
	bool ok = true;
	int code = Success;
	QString comment;
	QJsonObject data;
	bool unavailable = false;
};

using Handler = std::function<Reply(const QJsonObject &)>;

std::atomic<bool> g_stopped{false};

Reply Fail(int code, const QString &comment)
{
	Reply reply;
	reply.ok = false;
	reply.code = code;
	reply.comment = comment;
	return reply;
}

Reply Ok(QJsonObject data = {})
{
	Reply reply;
	reply.data = std::move(data);
	return reply;
}

QJsonObject DataToJson(obs_data_t *data)
{
	const char *json = data ? obs_data_get_json(data) : nullptr;
	return json ? QJsonDocument::fromJson(QByteArray(json)).object() : QJsonObject();
}

obs_data_t *JsonToData(const QJsonObject &object)
{
	return obs_data_create_from_json(QJsonDocument(object).toJson(QJsonDocument::Compact).constData());
}

// a string field the request has to carry, or the failure that says it does not
bool StringField(const QJsonObject &request, const char *name, std::string &out, Reply &failure)
{
	const QJsonValue value = request.value(QLatin1String(name));
	if (value.isUndefined() || value.isNull()) {
		failure = Fail(MissingRequestField, QStringLiteral("Your request is missing the `%1` field.").arg(QLatin1String(name)));
		return false;
	}
	if (!value.isString() || value.toString().isEmpty()) {
		failure = Fail(InvalidRequestField, QStringLiteral("The field `%1` must be a non-empty string.").arg(QLatin1String(name)));
		return false;
	}
	out = value.toString().toStdString();
	return true;
}

bool BoolField(const QJsonObject &request, const char *name, bool &out, Reply &failure)
{
	const QJsonValue value = request.value(QLatin1String(name));
	if (!value.isBool()) {
		failure = Fail(value.isUndefined() ? MissingRequestField : InvalidRequestField, QStringLiteral("The field `%1` must be a boolean.").arg(QLatin1String(name)));
		return false;
	}
	out = value.toBool();
	return true;
}

bool ObjectField(const QJsonObject &request, const char *name, QJsonObject &out, Reply &failure)
{
	const QJsonValue value = request.value(QLatin1String(name));
	if (!value.isObject()) {
		failure = Fail(value.isUndefined() ? MissingRequestField : InvalidRequestField, QStringLiteral("The field `%1` must be an object.").arg(QLatin1String(name)));
		return false;
	}
	out = value.toObject();
	return true;
}

bool NumberField(const QJsonObject &request, const char *name, double minimum, double maximum, double &out, Reply &failure)
{
	const QJsonValue value = request.value(QLatin1String(name));
	if (!value.isDouble() || value.toDouble() < minimum || value.toDouble() > maximum) {
		failure = Fail(value.isUndefined() ? MissingRequestField : InvalidRequestField,
			       QStringLiteral("The field `%1` must be a number from %2 to %3.").arg(QLatin1String(name)).arg(minimum).arg(maximum));
		return false;
	}
	out = value.toDouble();
	return true;
}

// owns one reference of a source for the length of a request
struct SourceRef {
	obs_source_t *source = nullptr;
	explicit SourceRef(obs_source_t *s = nullptr) : source(s) {}
	SourceRef(const SourceRef &) = delete;
	SourceRef &operator=(const SourceRef &) = delete;
	~SourceRef() { obs_source_release(source); }
	explicit operator bool() const { return source != nullptr; }
};

bool FindSource(const QJsonObject &request, const char *field, SourceRef &out, Reply &failure)
{
	std::string name;
	if (!StringField(request, field, name, failure))
		return false;
	out.source = obs_get_source_by_name(name.c_str());
	if (!out.source) {
		failure = Fail(ResourceNotFound, QStringLiteral("No source was found by the name of `%1`.").arg(QString::fromStdString(name)));
		return false;
	}
	return true;
}

bool FindInput(const QJsonObject &request, SourceRef &out, Reply &failure)
{
	if (!FindSource(request, "inputName", out, failure))
		return false;
	if (obs_source_get_type(out.source) != OBS_SOURCE_TYPE_INPUT) {
		failure = Fail(InvalidResourceType, QStringLiteral("The specified source is not an input."));
		return false;
	}
	return true;
}

enum class SceneKind { SceneOnly, GroupOnly, SceneOrGroup };

obs_scene_t *SceneOf(obs_source_t *source, SceneKind kind)
{
	const bool group = obs_source_is_group(source);
	if (group && kind == SceneKind::SceneOnly)
		return nullptr;
	if (!group && kind == SceneKind::GroupOnly)
		return nullptr;
	return group ? obs_group_from_source(source) : obs_scene_from_source(source);
}

bool FindScene(const QJsonObject &request, SceneKind kind, SourceRef &out, obs_scene_t *&scene, Reply &failure)
{
	if (!FindSource(request, "sceneName", out, failure))
		return false;
	scene = SceneOf(out.source, kind);
	if (!scene) {
		failure = Fail(InvalidResourceType, kind == SceneKind::GroupOnly ? QStringLiteral("The specified source is not a group.") : QStringLiteral("The specified source is not a scene."));
		return false;
	}
	return true;
}

bool FindSceneItem(const QJsonObject &request, SourceRef &sceneSource, obs_sceneitem_t *&item, Reply &failure)
{
	obs_scene_t *scene = nullptr;
	if (!FindScene(request, SceneKind::SceneOrGroup, sceneSource, scene, failure))
		return false;
	double id = 0;
	if (!NumberField(request, "sceneItemId", 0, 9007199254740991.0, id, failure))
		return false;
	item = obs_scene_find_sceneitem_by_id(scene, int64_t(id));
	if (!item) {
		failure = Fail(ResourceNotFound, QStringLiteral("No scene items were found in the specified scene by that ID."));
		return false;
	}
	return true;
}

bool FindFilter(const QJsonObject &request, SourceRef &source, SourceRef &filter, Reply &failure)
{
	if (!FindSource(request, "sourceName", source, failure))
		return false;
	std::string name;
	if (!StringField(request, "filterName", name, failure))
		return false;
	filter.source = obs_source_get_filter_by_name(source.source, name.c_str());
	if (!filter.source) {
		failure = Fail(ResourceNotFound, QStringLiteral("No filter was found in the source `%1` with the name `%2`.")
							 .arg(QString::fromUtf8(obs_source_get_name(source.source)), QString::fromStdString(name)));
		return false;
	}
	return true;
}

const char *const kBoundsTypes[] = {"OBS_BOUNDS_NONE", "OBS_BOUNDS_STRETCH", "OBS_BOUNDS_SCALE_INNER", "OBS_BOUNDS_SCALE_OUTER", "OBS_BOUNDS_SCALE_TO_WIDTH",
				    "OBS_BOUNDS_SCALE_TO_HEIGHT", "OBS_BOUNDS_MAX_ONLY"};
const char *const kBlendModes[] = {"OBS_BLEND_NORMAL", "OBS_BLEND_ADDITIVE", "OBS_BLEND_SUBTRACT", "OBS_BLEND_SCREEN", "OBS_BLEND_MULTIPLY", "OBS_BLEND_LIGHTEN", "OBS_BLEND_DARKEN"};

QJsonObject TransformOf(obs_sceneitem_t *item)
{
	obs_transform_info info{};
	obs_sceneitem_get_info2(item, &info);
	obs_sceneitem_crop crop{};
	obs_sceneitem_get_crop(item, &crop);
	obs_source_t *source = obs_sceneitem_get_source(item);
	const double sourceWidth = obs_source_get_width(source), sourceHeight = obs_source_get_height(source);
	const int bounds = int(info.bounds_type);
	return QJsonObject{{QStringLiteral("sourceWidth"), sourceWidth},
			   {QStringLiteral("sourceHeight"), sourceHeight},
			   {QStringLiteral("positionX"), info.pos.x},
			   {QStringLiteral("positionY"), info.pos.y},
			   {QStringLiteral("rotation"), info.rot},
			   {QStringLiteral("scaleX"), info.scale.x},
			   {QStringLiteral("scaleY"), info.scale.y},
			   {QStringLiteral("width"), info.scale.x * sourceWidth},
			   {QStringLiteral("height"), info.scale.y * sourceHeight},
			   {QStringLiteral("alignment"), int(info.alignment)},
			   {QStringLiteral("boundsType"), bounds >= 0 && bounds < int(std::size(kBoundsTypes)) ? QString::fromLatin1(kBoundsTypes[bounds]) : QString()},
			   {QStringLiteral("boundsAlignment"), int(info.bounds_alignment)},
			   {QStringLiteral("boundsWidth"), info.bounds.x},
			   {QStringLiteral("boundsHeight"), info.bounds.y},
			   {QStringLiteral("cropLeft"), crop.left},
			   {QStringLiteral("cropRight"), crop.right},
			   {QStringLiteral("cropTop"), crop.top},
			   {QStringLiteral("cropBottom"), crop.bottom},
			   {QStringLiteral("cropToBounds"), info.crop_to_bounds}};
}

QJsonArray SceneItemsOf(obs_scene_t *scene)
{
	QJsonArray items;
	obs_scene_enum_items(
		scene,
		[](obs_scene_t *, obs_sceneitem_t *item, void *data) {
			auto *items = static_cast<QJsonArray *>(data);
			obs_source_t *source = obs_sceneitem_get_source(item);
			const bool input = obs_source_get_type(source) == OBS_SOURCE_TYPE_INPUT;
			const int blend = int(obs_sceneitem_get_blending_mode(item));
			QJsonObject entry{{QStringLiteral("sceneItemId"), double(obs_sceneitem_get_id(item))},
					  {QStringLiteral("sceneItemIndex"), int(items->size())},
					  {QStringLiteral("sceneItemEnabled"), obs_sceneitem_visible(item)},
					  {QStringLiteral("sceneItemLocked"), obs_sceneitem_locked(item)},
					  {QStringLiteral("sceneItemTransform"), TransformOf(item)},
					  {QStringLiteral("sceneItemBlendMode"), blend >= 0 && blend < int(std::size(kBlendModes)) ? QString::fromLatin1(kBlendModes[blend]) : QString()},
					  {QStringLiteral("sourceName"), QString::fromUtf8(obs_source_get_name(source))},
					  {QStringLiteral("sourceUuid"), QString::fromUtf8(obs_source_get_uuid(source))},
					  {QStringLiteral("sourceType"), input ? QStringLiteral("OBS_SOURCE_TYPE_INPUT") : QStringLiteral("OBS_SOURCE_TYPE_SCENE")},
					  {QStringLiteral("inputKind"), input ? QJsonValue(QString::fromUtf8(obs_source_get_id(source))) : QJsonValue()},
					  {QStringLiteral("isGroup"), input ? QJsonValue() : QJsonValue(obs_source_is_group(source))}};
			items->append(entry);
			return true;
		},
		&items);
	return items;
}

// the length and the position of an output the way obs-websocket gives them, from its frames
QJsonObject OutputTimes(obs_output_t *output)
{
	const uint64_t frameNs = video_output_get_frame_time(obs_get_video());
	const uint64_t ms = output ? uint64_t(obs_output_get_total_frames(output)) * frameNs / 1000000ULL : 0;
	const QString timecode = QStringLiteral("%1:%2:%3.%4")
					 .arg(ms / 3600000, 2, 10, QLatin1Char('0'))
					 .arg((ms / 60000) % 60, 2, 10, QLatin1Char('0'))
					 .arg((ms / 1000) % 60, 2, 10, QLatin1Char('0'))
					 .arg(ms % 1000, 3, 10, QLatin1Char('0'));
	return {{QStringLiteral("outputDuration"), double(ms)}, {QStringLiteral("outputTimecode"), timecode}, {QStringLiteral("outputBytes"), output ? double(obs_output_get_total_bytes(output)) : 0.0}};
}

QJsonArray StringList(char **list)
{
	QJsonArray out;
	for (char **entry = list; entry && *entry; ++entry)
		out.append(QString::fromUtf8(*entry));
	bfree(list);
	return out;
}

bool FilterKindExists(const std::string &kind)
{
	const char *id = nullptr;
	for (size_t i = 0; obs_enum_filter_types(i, &id); ++i)
		if (id && kind == id)
			return true;
	return false;
}

bool InputKindExists(const std::string &kind)
{
	const char *id = nullptr;
	for (size_t i = 0; obs_enum_input_types(i, &id); ++i)
		if (id && kind == id)
			return true;
	return false;
}

Reply ApplySettings(obs_source_t *source, const QJsonObject &request, const char *field)
{
	Reply failure;
	QJsonObject settings;
	if (!ObjectField(request, field, settings, failure))
		return failure;
	const QJsonValue overlay = request.value(QStringLiteral("overlay"));
	obs_data_t *data = JsonToData(settings);
	if (!data)
		return Fail(RequestProcessingFailed, QStringLiteral("The settings could not be read."));
	if (overlay.isUndefined() || overlay.toBool(true))
		obs_source_update(source, data);
	else
		obs_source_reset_settings(source, data);
	obs_data_release(data);
	obs_source_update_properties(source);
	return Ok();
}

Reply OutputStatus(bool active)
{
	return Ok({{QStringLiteral("outputActive"), active}});
}

Reply StartFrontendOutput(bool active, void (*start)(), const char *what)
{
	if (active)
		return Fail(OutputRunning, QStringLiteral("The %1 is already running.").arg(QLatin1String(what)));
	start();
	return Ok();
}

Reply StopFrontendOutput(bool active, void (*stop)(), const char *what)
{
	if (!active)
		return Fail(OutputNotRunning, QStringLiteral("The %1 is not running.").arg(QLatin1String(what)));
	stop();
	return Ok();
}

bool ReplayBufferAvailable(Reply &failure)
{
	obs_output_t *output = obs_frontend_get_replay_buffer_output();
	obs_output_release(output);
	if (!output)
		failure = Fail(InvalidResourceState, QStringLiteral("Replay buffer is not available."));
	return output != nullptr;
}

const std::unordered_map<std::string, Handler> &Handlers()
{
	static const std::unordered_map<std::string, Handler> handlers = {
		{"GetVersion",
		 [](const QJsonObject &) {
			 return Ok({{QStringLiteral("obsVersion"), QString::fromUtf8(obs_get_version_string())}, {QStringLiteral("rpcVersion"), 1}, {QStringLiteral("platform"), QStringLiteral("windows")}});
		 }},
		{"GetVideoSettings",
		 [](const QJsonObject &) {
			 obs_video_info ovi{};
			 if (!obs_get_video_info(&ovi))
				 return Fail(RequestProcessingFailed, QStringLiteral("Unable to get internal OBS video info."));
			 return Ok({{QStringLiteral("fpsNumerator"), double(ovi.fps_num)},
				    {QStringLiteral("fpsDenominator"), double(ovi.fps_den)},
				    {QStringLiteral("baseWidth"), double(ovi.base_width)},
				    {QStringLiteral("baseHeight"), double(ovi.base_height)},
				    {QStringLiteral("outputWidth"), double(ovi.output_width)},
				    {QStringLiteral("outputHeight"), double(ovi.output_height)}});
		 }},
		{"SetVideoSettings",
		 [](const QJsonObject &request) {
			 if (obs_video_active())
				 return Fail(OutputRunning, QStringLiteral("Video settings cannot be changed while an output is active."));
			 config_t *config = obs_frontend_get_profile_config();
			 if (!config)
				 return Fail(RequestProcessingFailed, QStringLiteral("Unable to get the profile config."));
			 Reply failure;
			 const bool fps = request.contains(QStringLiteral("fpsNumerator")) || request.contains(QStringLiteral("fpsDenominator"));
			 const bool base = request.contains(QStringLiteral("baseWidth")) || request.contains(QStringLiteral("baseHeight"));
			 const bool output = request.contains(QStringLiteral("outputWidth")) || request.contains(QStringLiteral("outputHeight"));
			 double fpsNum = 0, fpsDen = 0, baseW = 0, baseH = 0, outW = 0, outH = 0;
			 if (fps && (!NumberField(request, "fpsNumerator", 1, 4294967295.0, fpsNum, failure) || !NumberField(request, "fpsDenominator", 1, 4294967295.0, fpsDen, failure)))
				 return failure;
			 if (base && (!NumberField(request, "baseWidth", 1, 8192, baseW, failure) || !NumberField(request, "baseHeight", 1, 8192, baseH, failure)))
				 return failure;
			 if (output && (!NumberField(request, "outputWidth", 1, 8192, outW, failure) || !NumberField(request, "outputHeight", 1, 8192, outH, failure)))
				 return failure;
			 if (!fps && !base && !output)
				 return Fail(MissingRequestField, QStringLiteral("You must specify at least one video-changing pair."));
			 if (fps) {
				 config_set_uint(config, "Video", "FPSType", 2);
				 config_set_uint(config, "Video", "FPSNum", uint64_t(fpsNum));
				 config_set_uint(config, "Video", "FPSDen", uint64_t(fpsDen));
			 }
			 if (base) {
				 config_set_uint(config, "Video", "BaseCX", uint64_t(baseW));
				 config_set_uint(config, "Video", "BaseCY", uint64_t(baseH));
			 }
			 if (output) {
				 config_set_uint(config, "Video", "OutputCX", uint64_t(outW));
				 config_set_uint(config, "Video", "OutputCY", uint64_t(outH));
			 }
			 config_save_safe(config, "tmp", nullptr);
			 obs_frontend_reset_video();
			 return Ok();
		 }},
		{"GetProfileParameter",
		 [](const QJsonObject &request) {
			 Reply failure;
			 std::string category, name;
			 if (!StringField(request, "parameterCategory", category, failure) || !StringField(request, "parameterName", name, failure))
				 return failure;
			 config_t *config = obs_frontend_get_profile_config();
			 if (!config)
				 return Fail(RequestProcessingFailed, QStringLiteral("Unable to get the profile config."));
			 const QJsonValue value = config_has_user_value(config, category.c_str(), name.c_str())
							  ? QJsonValue(QString::fromUtf8(config_get_string(config, category.c_str(), name.c_str())))
							  : QJsonValue();
			 const QJsonValue fallback = config_has_default_value(config, category.c_str(), name.c_str())
							     ? QJsonValue(QString::fromUtf8(config_get_default_string(config, category.c_str(), name.c_str())))
							     : QJsonValue();
			 return Ok({{QStringLiteral("parameterValue"), value}, {QStringLiteral("defaultParameterValue"), fallback}});
		 }},
		{"SetProfileParameter",
		 [](const QJsonObject &request) {
			 Reply failure;
			 std::string category, name;
			 if (!StringField(request, "parameterCategory", category, failure) || !StringField(request, "parameterName", name, failure))
				 return failure;
			 config_t *config = obs_frontend_get_profile_config();
			 if (!config)
				 return Fail(RequestProcessingFailed, QStringLiteral("Unable to get the profile config."));
			 const QJsonValue value = request.value(QStringLiteral("parameterValue"));
			 if (value.isString())
				 config_set_string(config, category.c_str(), name.c_str(), value.toString().toUtf8().constData());
			 else if (value.isNull() || value.isUndefined())
				 config_remove_value(config, category.c_str(), name.c_str());
			 else
				 return Fail(InvalidRequestField, QStringLiteral("The field `parameterValue` must be a string or null."));
			 config_save_safe(config, "tmp", nullptr);
			 return Ok();
		 }},
		{"GetProfileList",
		 [](const QJsonObject &) {
			 char *current = obs_frontend_get_current_profile();
			 const QString name = QString::fromUtf8(current ? current : "");
			 bfree(current);
			 return Ok({{QStringLiteral("currentProfileName"), name}, {QStringLiteral("profiles"), StringList(obs_frontend_get_profiles())}});
		 }},
		{"GetSceneCollectionList",
		 [](const QJsonObject &) {
			 char *current = obs_frontend_get_current_scene_collection();
			 const QString name = QString::fromUtf8(current ? current : "");
			 bfree(current);
			 return Ok({{QStringLiteral("currentSceneCollectionName"), name}, {QStringLiteral("sceneCollections"), StringList(obs_frontend_get_scene_collections())}});
		 }},
		{"GetSceneList",
		 [](const QJsonObject &) {
			 obs_frontend_source_list scenes = {};
			 obs_frontend_get_scenes(&scenes);
			 QJsonArray list;
			 for (size_t i = 0; i < scenes.sources.num; ++i) {
				 obs_source_t *scene = scenes.sources.array[i];
				 list.prepend(QJsonObject{{QStringLiteral("sceneIndex"), int(i)},
							  {QStringLiteral("sceneName"), QString::fromUtf8(obs_source_get_name(scene))},
							  {QStringLiteral("sceneUuid"), QString::fromUtf8(obs_source_get_uuid(scene))}});
			 }
			 obs_frontend_source_list_free(&scenes);
			 QJsonObject data{{QStringLiteral("scenes"), list}};
			 SourceRef program(obs_frontend_get_current_scene());
			 data[QStringLiteral("currentProgramSceneName")] = program ? QJsonValue(QString::fromUtf8(obs_source_get_name(program.source))) : QJsonValue();
			 data[QStringLiteral("currentProgramSceneUuid")] = program ? QJsonValue(QString::fromUtf8(obs_source_get_uuid(program.source))) : QJsonValue();
			 SourceRef preview(obs_frontend_preview_program_mode_active() ? obs_frontend_get_current_preview_scene() : nullptr);
			 data[QStringLiteral("currentPreviewSceneName")] = preview ? QJsonValue(QString::fromUtf8(obs_source_get_name(preview.source))) : QJsonValue();
			 data[QStringLiteral("currentPreviewSceneUuid")] = preview ? QJsonValue(QString::fromUtf8(obs_source_get_uuid(preview.source))) : QJsonValue();
			 return Ok(data);
		 }},
		{"GetSceneItemList",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source;
			 obs_scene_t *scene = nullptr;
			 if (!FindScene(request, SceneKind::SceneOnly, source, scene, failure))
				 return failure;
			 return Ok({{QStringLiteral("sceneItems"), SceneItemsOf(scene)}});
		 }},
		{"GetGroupSceneItemList",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source;
			 obs_scene_t *scene = nullptr;
			 if (!FindScene(request, SceneKind::GroupOnly, source, scene, failure))
				 return failure;
			 return Ok({{QStringLiteral("sceneItems"), SceneItemsOf(scene)}});
		 }},
		{"SetSceneItemEnabled",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 bool enabled = false;
			 if (!FindSceneItem(request, scene, item, failure) || !BoolField(request, "sceneItemEnabled", enabled, failure))
				 return failure;
			 obs_sceneitem_set_visible(item, enabled);
			 return Ok();
		 }},
		{"RemoveSceneItem",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 if (!FindSceneItem(request, scene, item, failure))
				 return failure;
			 obs_sceneitem_remove(item);
			 return Ok();
		 }},
		{"GetSceneItemTransform",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 if (!FindSceneItem(request, scene, item, failure))
				 return failure;
			 return Ok({{QStringLiteral("sceneItemTransform"), TransformOf(item)}});
		 }},
		{"SetSceneItemTransform",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 QJsonObject transform;
			 if (!FindSceneItem(request, scene, item, failure) || !ObjectField(request, "sceneItemTransform", transform, failure))
				 return failure;
			 obs_transform_info info{};
			 obs_sceneitem_get_info2(item, &info);
			 obs_sceneitem_crop crop{};
			 obs_sceneitem_get_crop(item, &crop);
			 double value = 0;
			 auto take = [&](const char *name, double minimum, double maximum, auto apply) {
				 if (!transform.contains(QLatin1String(name)))
					 return true;
				 if (!NumberField(transform, name, minimum, maximum, value, failure))
					 return false;
				 apply(value);
				 return true;
			 };
			 const double huge = 90001.0;
			 const bool read = take("positionX", -huge, huge, [&](double v) { info.pos.x = float(v); }) && take("positionY", -huge, huge, [&](double v) { info.pos.y = float(v); }) &&
					   take("rotation", -360.0, 360.0, [&](double v) { info.rot = float(v); }) &&
					   take("scaleX", -huge, huge, [&](double v) { info.scale.x = float(v); }) && take("scaleY", -huge, huge, [&](double v) { info.scale.y = float(v); }) &&
					   take("alignment", 0, 255, [&](double v) { info.alignment = uint32_t(v); }) &&
					   take("boundsAlignment", 0, 255, [&](double v) { info.bounds_alignment = uint32_t(v); }) &&
					   take("boundsWidth", 1, huge, [&](double v) { info.bounds.x = float(v); }) && take("boundsHeight", 1, huge, [&](double v) { info.bounds.y = float(v); }) &&
					   take("cropLeft", 0, 100000, [&](double v) { crop.left = int(v); }) && take("cropRight", 0, 100000, [&](double v) { crop.right = int(v); }) &&
					   take("cropTop", 0, 100000, [&](double v) { crop.top = int(v); }) && take("cropBottom", 0, 100000, [&](double v) { crop.bottom = int(v); });
			 if (!read)
				 return failure;
			 if (info.scale.x == 0.0f || info.scale.y == 0.0f)
				 return Fail(InvalidRequestField, QStringLiteral("A scale of zero is not allowed."));
			 if (transform.contains(QStringLiteral("boundsType"))) {
				 const QString bounds = transform.value(QStringLiteral("boundsType")).toString();
				 int found = -1;
				 for (int i = 0; i < int(std::size(kBoundsTypes)); ++i)
					 if (bounds == QLatin1String(kBoundsTypes[i]))
						 found = i;
				 if (found < 0)
					 return Fail(InvalidRequestField, QStringLiteral("The field `boundsType` has an invalid value."));
				 info.bounds_type = obs_bounds_type(found);
			 }
			 if (transform.contains(QStringLiteral("cropToBounds")))
				 info.crop_to_bounds = transform.value(QStringLiteral("cropToBounds")).toBool();
			 obs_sceneitem_defer_update_begin(item);
			 obs_sceneitem_set_info2(item, &info);
			 obs_sceneitem_set_crop(item, &crop);
			 obs_sceneitem_defer_update_end(item);
			 return Ok();
		 }},
		{"GetSceneItemIndex",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 if (!FindSceneItem(request, scene, item, failure))
				 return failure;
			 return Ok({{QStringLiteral("sceneItemIndex"), obs_sceneitem_get_order_position(item)}});
		 }},
		{"SetSceneItemIndex",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef scene;
			 obs_sceneitem_t *item = nullptr;
			 double index = 0;
			 if (!FindSceneItem(request, scene, item, failure) || !NumberField(request, "sceneItemIndex", 0, 100000, index, failure))
				 return failure;
			 obs_sceneitem_set_order_position(item, int(index));
			 return Ok();
		 }},
		{"CreateSceneItem",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef sceneSource, source;
			 obs_scene_t *scene = nullptr;
			 if (!FindScene(request, SceneKind::SceneOrGroup, sceneSource, scene, failure) || !FindSource(request, "sourceName", source, failure))
				 return failure;
			 if (source.source == sceneSource.source)
				 return Fail(InvalidResourceState, QStringLiteral("You cannot create scene item of a scene within itself."));
			 const QJsonValue enabled = request.value(QStringLiteral("sceneItemEnabled"));
			 obs_sceneitem_t *item = obs_scene_add(scene, source.source);
			 if (!item)
				 return Fail(ResourceCreationFailed, QStringLiteral("Failed to create the scene item."));
			 obs_sceneitem_set_visible(item, enabled.isUndefined() || enabled.toBool(true));
			 return Ok({{QStringLiteral("sceneItemId"), double(obs_sceneitem_get_id(item))}});
		 }},
		{"CreateInput",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef sceneSource;
			 obs_scene_t *scene = nullptr;
			 std::string name, kind;
			 if (!FindScene(request, SceneKind::SceneOrGroup, sceneSource, scene, failure) || !StringField(request, "inputName", name, failure) ||
			     !StringField(request, "inputKind", kind, failure))
				 return failure;
			 SourceRef existing(obs_get_source_by_name(name.c_str()));
			 if (existing)
				 return Fail(ResourceAlreadyExists, QStringLiteral("A source already exists by that input name."));
			 if (!InputKindExists(kind))
				 return Fail(InvalidInputKind, QStringLiteral("Your specified input kind is not supported by OBS."));
			 obs_data_t *settings = request.value(QStringLiteral("inputSettings")).isObject() ? JsonToData(request.value(QStringLiteral("inputSettings")).toObject()) : nullptr;
			 SourceRef input(obs_source_create(kind.c_str(), name.c_str(), settings, nullptr));
			 obs_data_release(settings);
			 if (!input)
				 return Fail(ResourceCreationFailed, QStringLiteral("Creation of the input failed."));
			 const QJsonValue enabled = request.value(QStringLiteral("sceneItemEnabled"));
			 obs_sceneitem_t *item = obs_scene_add(scene, input.source);
			 if (!item)
				 return Fail(ResourceCreationFailed, QStringLiteral("Creation of the input succeeded, but creation of the scene item failed."));
			 obs_sceneitem_set_visible(item, enabled.isUndefined() || enabled.toBool(true));
			 return Ok({{QStringLiteral("inputUuid"), QString::fromUtf8(obs_source_get_uuid(input.source))}, {QStringLiteral("sceneItemId"), double(obs_sceneitem_get_id(item))}});
		 }},
		{"RemoveInput",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 obs_source_remove(input.source);
			 return Ok();
		 }},
		{"SetInputName",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 std::string name;
			 if (!FindInput(request, input, failure) || !StringField(request, "newInputName", name, failure))
				 return failure;
			 SourceRef existing(obs_get_source_by_name(name.c_str()));
			 if (existing)
				 return Fail(ResourceAlreadyExists, QStringLiteral("A source already exists by that new input name."));
			 obs_source_set_name(input.source, name.c_str());
			 return Ok();
		 }},
		{"GetInputSettings",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 obs_data_t *settings = obs_source_get_settings(input.source);
			 Reply reply = Ok({{QStringLiteral("inputSettings"), DataToJson(settings)}, {QStringLiteral("inputKind"), QString::fromUtf8(obs_source_get_id(input.source))}});
			 obs_data_release(settings);
			 return reply;
		 }},
		{"SetInputSettings",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 return ApplySettings(input.source, request, "inputSettings");
		 }},
		{"GetInputKindList",
		 [](const QJsonObject &request) {
			 const bool unversioned = request.value(QStringLiteral("unversioned")).toBool(false);
			 QJsonArray kinds;
			 const char *id = nullptr, *unversionedId = nullptr;
			 for (size_t i = 0; obs_enum_input_types2(i, &id, &unversionedId); ++i)
				 kinds.append(QString::fromUtf8(unversioned && unversionedId ? unversionedId : id));
			 return Ok({{QStringLiteral("inputKinds"), kinds}});
		 }},
		{"GetInputMute",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 return Ok({{QStringLiteral("inputMuted"), obs_source_muted(input.source)}});
		 }},
		{"SetInputMute",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 bool muted = false;
			 if (!FindInput(request, input, failure) || !BoolField(request, "inputMuted", muted, failure))
				 return failure;
			 obs_source_set_muted(input.source, muted);
			 return Ok();
		 }},
		{"GetInputVolume",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 const float mul = obs_source_get_volume(input.source);
			 return Ok({{QStringLiteral("inputVolumeMul"), mul}, {QStringLiteral("inputVolumeDb"), mul > 0.0f ? QJsonValue(20.0 * std::log10(double(mul))) : QJsonValue()}});
		 }},
		{"SetInputVolume",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 double value = 0;
			 if (request.contains(QStringLiteral("inputVolumeMul"))) {
				 if (!NumberField(request, "inputVolumeMul", 0, 20, value, failure))
					 return failure;
			 } else if (request.contains(QStringLiteral("inputVolumeDb"))) {
				 if (!NumberField(request, "inputVolumeDb", -100, 26, value, failure))
					 return failure;
				 value = std::pow(10.0, value / 20.0);
			 } else {
				 return Fail(MissingRequestField, QStringLiteral("You must specify one volume parameter."));
			 }
			 obs_source_set_volume(input.source, float(value));
			 return Ok();
		 }},
		{"GetInputAudioMonitorType",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 if (!(obs_source_get_output_flags(input.source) & OBS_SOURCE_AUDIO))
				 return Fail(InvalidResourceState, QStringLiteral("The specified input does not support audio."));
			 return Ok({{QStringLiteral("monitorType"), obs_source_get_monitoring_enabled(input.source) ? QStringLiteral("OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT")
														      : QStringLiteral("OBS_MONITORING_TYPE_NONE")}});
		 }},
		{"SetInputAudioMonitorType",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 std::string type;
			 if (!FindInput(request, input, failure) || !StringField(request, "monitorType", type, failure))
				 return failure;
			 if (!(obs_source_get_output_flags(input.source) & OBS_SOURCE_AUDIO))
				 return Fail(InvalidResourceState, QStringLiteral("The specified input does not support audio."));
			 // obs 33 keeps monitoring on or off, and its typed calls log a deprecation on every use
			 if (type != "OBS_MONITORING_TYPE_NONE" && type != "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT")
				 return Fail(InvalidRequestField, QStringLiteral("The field `monitorType` must be OBS_MONITORING_TYPE_NONE or OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT."));
			 obs_source_set_monitoring_enabled(input.source, type != "OBS_MONITORING_TYPE_NONE");
			 return Ok();
		 }},
		{"GetInputAudioTracks",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 if (!FindInput(request, input, failure))
				 return failure;
			 if (!(obs_source_get_output_flags(input.source) & OBS_SOURCE_AUDIO))
				 return Fail(InvalidResourceState, QStringLiteral("The specified input does not support audio."));
			 const uint32_t mixers = obs_source_get_audio_mixers(input.source);
			 QJsonObject tracks;
			 for (int i = 0; i < MAX_AUDIO_MIXES; ++i)
				 tracks[QString::number(i + 1)] = (mixers & (1u << i)) != 0;
			 return Ok({{QStringLiteral("inputAudioTracks"), tracks}});
		 }},
		{"SetInputAudioTracks",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 QJsonObject tracks;
			 if (!FindInput(request, input, failure) || !ObjectField(request, "inputAudioTracks", tracks, failure))
				 return failure;
			 if (!(obs_source_get_output_flags(input.source) & OBS_SOURCE_AUDIO))
				 return Fail(InvalidResourceState, QStringLiteral("The specified input does not support audio."));
			 uint32_t mixers = obs_source_get_audio_mixers(input.source);
			 for (auto it = tracks.begin(); it != tracks.end(); ++it) {
				 bool numeric = false;
				 const int track = it.key().toInt(&numeric);
				 if (!numeric || track < 1 || track > MAX_AUDIO_MIXES || !it.value().isBool())
					 return Fail(InvalidRequestField, QStringLiteral("The track `%1` is not a valid track or its value is not a boolean.").arg(it.key()));
				 if (it.value().toBool())
					 mixers |= 1u << (track - 1);
				 else
					 mixers &= ~(1u << (track - 1));
			 }
			 obs_source_set_audio_mixers(input.source, mixers);
			 return Ok();
		 }},
		{"GetInputPropertiesListPropertyItems",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef input;
			 std::string name;
			 if (!FindInput(request, input, failure) || !StringField(request, "propertyName", name, failure))
				 return failure;
			 obs_properties_t *properties = obs_source_properties(input.source);
			 obs_property_t *property = properties ? obs_properties_get(properties, name.c_str()) : nullptr;
			 if (!property || obs_property_get_type(property) != OBS_PROPERTY_LIST) {
				 obs_properties_destroy(properties);
				 return Fail(property ? InvalidResourceType : ResourceNotFound, property ? QStringLiteral("The property is not a list.") : QStringLiteral("Unable to find a property by that name."));
			 }
			 QJsonArray items;
			 const obs_combo_format format = obs_property_list_format(property);
			 for (size_t i = 0; i < obs_property_list_item_count(property); ++i) {
				 QJsonValue value;
				 if (format == OBS_COMBO_FORMAT_INT)
					 value = double(obs_property_list_item_int(property, i));
				 else if (format == OBS_COMBO_FORMAT_FLOAT)
					 value = obs_property_list_item_float(property, i);
				 else if (format == OBS_COMBO_FORMAT_STRING)
					 value = QString::fromUtf8(obs_property_list_item_string(property, i));
				 else if (format == OBS_COMBO_FORMAT_BOOL)
					 value = obs_property_list_item_bool(property, i);
				 items.append(QJsonObject{{QStringLiteral("itemName"), QString::fromUtf8(obs_property_list_item_name(property, i))},
							  {QStringLiteral("itemEnabled"), !obs_property_list_item_disabled(property, i)},
							  {QStringLiteral("itemValue"), value}});
			 }
			 obs_properties_destroy(properties);
			 return Ok({{QStringLiteral("propertyItems"), items}});
		 }},
		{"GetSourceFilterList",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source;
			 if (!FindSource(request, "sourceName", source, failure))
				 return failure;
			 struct Gather {
				 obs_source_t *parent;
				 std::vector<std::pair<int, QJsonObject>> filters;
			 } gather{source.source, {}};
			 obs_source_enum_filters(
				 source.source,
				 [](obs_source_t *, obs_source_t *filter, void *data) {
					 auto *gather = static_cast<Gather *>(data);
					 obs_data_t *settings = obs_source_get_settings(filter);
					 const int index = int(obs_source_filter_get_index(gather->parent, filter));
					 gather->filters.emplace_back(index, QJsonObject{{QStringLiteral("filterEnabled"), obs_source_enabled(filter)},
											  {QStringLiteral("filterIndex"), index},
											  {QStringLiteral("filterKind"), QString::fromUtf8(obs_source_get_id(filter))},
											  {QStringLiteral("filterName"), QString::fromUtf8(obs_source_get_name(filter))},
											  {QStringLiteral("filterSettings"), DataToJson(settings)}});
					 obs_data_release(settings);
				 },
				 &gather);
			 std::sort(gather.filters.begin(), gather.filters.end(), [](const auto &a, const auto &b) { return a.first < b.first; });
			 QJsonArray filters;
			 for (const auto &entry : gather.filters)
				 filters.append(entry.second);
			 return Ok({{QStringLiteral("filters"), filters}});
		 }},
		{"CreateSourceFilter",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source;
			 std::string name, kind;
			 if (!FindSource(request, "sourceName", source, failure) || !StringField(request, "filterName", name, failure) || !StringField(request, "filterKind", kind, failure))
				 return failure;
			 SourceRef existing(obs_source_get_filter_by_name(source.source, name.c_str()));
			 if (existing)
				 return Fail(ResourceAlreadyExists, QStringLiteral("A filter already exists by that name."));
			 if (!FilterKindExists(kind))
				 return Fail(InvalidFilterKind, QStringLiteral("Your specified filter kind is not supported by OBS."));
			 obs_data_t *settings = request.value(QStringLiteral("filterSettings")).isObject() ? JsonToData(request.value(QStringLiteral("filterSettings")).toObject()) : nullptr;
			 SourceRef filter(obs_source_create_private(kind.c_str(), name.c_str(), settings));
			 obs_data_release(settings);
			 if (!filter)
				 return Fail(ResourceCreationFailed, QStringLiteral("Creation of the filter failed."));
			 obs_source_filter_add(source.source, filter.source);
			 return Ok();
		 }},
		{"RemoveSourceFilter",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source, filter;
			 if (!FindFilter(request, source, filter, failure))
				 return failure;
			 obs_source_filter_remove(source.source, filter.source);
			 return Ok();
		 }},
		{"SetSourceFilterSettings",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source, filter;
			 if (!FindFilter(request, source, filter, failure))
				 return failure;
			 return ApplySettings(filter.source, request, "filterSettings");
		 }},
		{"SetSourceFilterEnabled",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source, filter;
			 bool enabled = false;
			 if (!FindFilter(request, source, filter, failure) || !BoolField(request, "filterEnabled", enabled, failure))
				 return failure;
			 obs_source_set_enabled(filter.source, enabled);
			 return Ok();
		 }},
		{"SetSourceFilterIndex",
		 [](const QJsonObject &request) {
			 Reply failure;
			 SourceRef source, filter;
			 double index = 0;
			 if (!FindFilter(request, source, filter, failure) || !NumberField(request, "filterIndex", 0, 100000, index, failure))
				 return failure;
			 obs_source_filter_set_index(source.source, filter.source, size_t(index));
			 return Ok();
		 }},
		{"GetOutputSettings",
		 [](const QJsonObject &request) {
			 Reply failure;
			 std::string name;
			 if (!StringField(request, "outputName", name, failure))
				 return failure;
			 obs_output_t *output = obs_get_output_by_name(name.c_str());
			 if (!output)
				 return Fail(ResourceNotFound, QStringLiteral("No output was found with the name `%1`.").arg(QString::fromStdString(name)));
			 obs_data_t *settings = obs_output_get_settings(output);
			 Reply reply = Ok({{QStringLiteral("outputSettings"), DataToJson(settings)}});
			 obs_data_release(settings);
			 obs_output_release(output);
			 return reply;
		 }},
		{"SetOutputSettings",
		 [](const QJsonObject &request) {
			 Reply failure;
			 std::string name;
			 QJsonObject settings;
			 if (!StringField(request, "outputName", name, failure) || !ObjectField(request, "outputSettings", settings, failure))
				 return failure;
			 obs_output_t *output = obs_get_output_by_name(name.c_str());
			 if (!output)
				 return Fail(ResourceNotFound, QStringLiteral("No output was found with the name `%1`.").arg(QString::fromStdString(name)));
			 obs_data_t *data = JsonToData(settings);
			 obs_output_update(output, data);
			 obs_data_release(data);
			 obs_output_release(output);
			 return Ok();
		 }},
		{"GetReplayBufferStatus",
		 [](const QJsonObject &) {
			 Reply failure;
			 if (!ReplayBufferAvailable(failure))
				 return failure;
			 return OutputStatus(obs_frontend_replay_buffer_active());
		 }},
		{"StartReplayBuffer",
		 [](const QJsonObject &) {
			 Reply failure;
			 if (!ReplayBufferAvailable(failure))
				 return failure;
			 return StartFrontendOutput(obs_frontend_replay_buffer_active(), obs_frontend_replay_buffer_start, "replay buffer");
		 }},
		{"StopReplayBuffer",
		 [](const QJsonObject &) {
			 Reply failure;
			 if (!ReplayBufferAvailable(failure))
				 return failure;
			 return StopFrontendOutput(obs_frontend_replay_buffer_active(), obs_frontend_replay_buffer_stop, "replay buffer");
		 }},
		{"ToggleReplayBuffer",
		 [](const QJsonObject &) {
			 Reply failure;
			 if (!ReplayBufferAvailable(failure))
				 return failure;
			 const bool active = obs_frontend_replay_buffer_active();
			 if (active)
				 obs_frontend_replay_buffer_stop();
			 else
				 obs_frontend_replay_buffer_start();
			 return OutputStatus(!active);
		 }},
		{"SaveReplayBuffer",
		 [](const QJsonObject &) {
			 Reply failure;
			 if (!ReplayBufferAvailable(failure))
				 return failure;
			 if (!obs_frontend_replay_buffer_active())
				 return Fail(OutputNotRunning, QStringLiteral("The replay buffer is not running."));
			 obs_frontend_replay_buffer_save();
			 return Ok();
		 }},
		{"GetRecordStatus",
		 [](const QJsonObject &) {
			 obs_output_t *output = obs_frontend_get_recording_output();
			 QJsonObject data = OutputTimes(output);
			 obs_output_release(output);
			 data[QStringLiteral("outputActive")] = obs_frontend_recording_active();
			 data[QStringLiteral("outputPaused")] = obs_frontend_recording_paused();
			 return Ok(data);
		 }},
		{"StartRecord", [](const QJsonObject &) { return StartFrontendOutput(obs_frontend_recording_active(), obs_frontend_recording_start, "record output"); }},
		{"StopRecord", [](const QJsonObject &) { return StopFrontendOutput(obs_frontend_recording_active(), obs_frontend_recording_stop, "record output"); }},
		{"ToggleRecord",
		 [](const QJsonObject &) {
			 const bool active = obs_frontend_recording_active();
			 if (active)
				 obs_frontend_recording_stop();
			 else
				 obs_frontend_recording_start();
			 return OutputStatus(!active);
		 }},
		{"GetStreamStatus",
		 [](const QJsonObject &) {
			 obs_output_t *output = obs_frontend_get_streaming_output();
			 QJsonObject data = OutputTimes(output);
			 data[QStringLiteral("outputActive")] = obs_frontend_streaming_active();
			 data[QStringLiteral("outputReconnecting")] = output && obs_output_reconnecting(output);
			 data[QStringLiteral("outputCongestion")] = output ? double(obs_output_get_congestion(output)) : 0.0;
			 data[QStringLiteral("outputSkippedFrames")] = output ? obs_output_get_frames_dropped(output) : 0;
			 data[QStringLiteral("outputTotalFrames")] = output ? obs_output_get_total_frames(output) : 0;
			 obs_output_release(output);
			 return Ok(data);
		 }},
		{"GetVirtualCamStatus", [](const QJsonObject &) { return OutputStatus(obs_frontend_virtualcam_active()); }},
		{"StartVirtualCam", [](const QJsonObject &) { return StartFrontendOutput(obs_frontend_virtualcam_active(), obs_frontend_start_virtualcam, "virtual camera"); }},
		{"StopVirtualCam", [](const QJsonObject &) { return StopFrontendOutput(obs_frontend_virtualcam_active(), obs_frontend_stop_virtualcam, "virtual camera"); }},
	};
	return handlers;
}

Reply Run(const std::string &type, const QJsonObject &request)
{
	if (g_stopped.load()) {
		Reply reply = Fail(0, QStringLiteral("OBS is closing."));
		reply.unavailable = true;
		return reply;
	}
	const auto found = Handlers().find(type);
	if (found == Handlers().end())
		return Fail(UnknownRequestType, QStringLiteral("Your request type is not valid."));
	return found->second(request);
}

// the answer goes back on a thread of its own, since the call blocks until the helper has taken it
void Answer(const std::string &id, const Reply &reply)
{
	QJsonObject body{{QStringLiteral("ok"), reply.ok}, {QStringLiteral("code"), reply.code}, {QStringLiteral("comment"), reply.comment}, {QStringLiteral("data"), reply.data}};
	if (reply.unavailable)
		body[QStringLiteral("unavailable")] = true;
	const std::string text = QJsonDocument(body).toJson(QJsonDocument::Compact).toStdString();
	RunAsync([id, text]() {
		const std::string path = "/obs/reply?id=" + id;
		HttpRequest("POST", path.c_str(), text.c_str(), 4000);
	});
}

} // namespace

void ObsRequestDispatch(const std::string &line)
{
	// the id is a random token of the helper, so a page cannot guess one and answer in place of obs
	static const QRegularExpression shape(QStringLiteral("^([0-9a-f]{32}) ([A-Za-z]{1,64}) (\\{.*\\})$"));
	const QRegularExpressionMatch match = shape.match(QString::fromStdString(line));
	if (!match.hasMatch()) {
		blog(LOG_WARNING, "[replaykit] obs request: a line of an unknown shape was dropped");
		return;
	}
	const std::string id = match.captured(1).toStdString();
	const std::string type = match.captured(2).toStdString();
	const QJsonObject request = QJsonDocument::fromJson(match.captured(3).toUtf8()).object();
	QMetaObject::invokeMethod(g_callbacks, [id, type, request]() { Answer(id, Run(type, request)); }, Qt::QueuedConnection);
}

void ObsRequestsStop()
{
	g_stopped.store(true);
}

} // namespace rk
