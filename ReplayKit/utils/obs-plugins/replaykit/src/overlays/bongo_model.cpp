#include "prelude.hpp"
#include "overlays/bongo_model.hpp"

#include <array>
#include <QCryptographicHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <cmath>

namespace rk {

namespace {

bool ReadIndex(const QJsonValue &value, int limit, int &out)
{
	const double n = value.toDouble(NAN);
	if (!std::isfinite(n) || n < 0 || n >= limit || n != std::floor(n))
		return false;
	out = int(n);
	return true;
}

bool ReadNumber(const QJsonValue &value, double bound, float &out)
{
	const double n = value.toDouble(NAN);
	if (!std::isfinite(n) || std::abs(n) > bound)
		return false;
	out = float(n);
	return true;
}

} // namespace

bool BongoModel::Load(const QString &moc3, const QString &texturePath, const QString &bakes)
{
	QFile moc(moc3);
	if (!moc.open(QIODevice::ReadOnly) || moc.size() > 16 * 1024 * 1024)
		return false;
	const QString md5 = QString::fromLatin1(QCryptographicHash::hash(moc.readAll(), QCryptographicHash::Md5).toHex());
	QFile file(QDir(bakes).filePath(md5 + ".json"));
	if (!file.open(QIODevice::ReadOnly) || file.size() > 4 * 1024 * 1024)
		return false;
	const auto root = QJsonDocument::fromJson(file.readAll()).object();
	const auto parameterList = root["parameters"].toArray();
	const auto meshList = root["meshes"].toArray();
	if (root["version"].toInt() != 4 || root["moc3"].toString() != md5 || parameterList.size() > 16 || meshList.isEmpty() || meshList.size() > 64)
		return false;
	for (const auto &entry : parameterList) {
		const auto object = entry.toObject();
		Parameter parameter;
		parameter.id = object["id"].toString();
		const auto keyList = object["keys"].toArray();
		if (parameter.id.isEmpty() || keyList.size() < 2 || keyList.size() > 9 || !ReadNumber(object["default"], 1000, parameter.initial) ||
		    !ReadNumber(object["min"], 1000, parameter.minimum) || !ReadNumber(object["max"], 1000, parameter.maximum) || parameter.minimum > parameter.maximum)
			return false;
		for (const auto &key : keyList) {
			float n;
			if (!ReadNumber(key, 1000, n) || (!parameter.keys.empty() && n <= parameter.keys.back()))
				return false;
			parameter.keys.push_back(n);
		}
		parameters.push_back(std::move(parameter));
	}
	const QByteArray textureUtf8 = texturePath.toUtf8();
	gs_image_file_init(&texture, textureUtf8.constData());
	if (texture.loaded)
		gs_image_file_init_texture(&texture);
	if (!texture.texture)
		return false;
	for (const auto &entry : meshList) {
		const auto object = entry.toObject();
		const auto uv = object["uv"].toArray(), indices = object["indices"].toArray(), poses = object["poses"].toArray(), axes = object["axes"].toArray();
		if (uv.size() < 6 || uv.size() > 4096 || uv.size() % 2 || indices.isEmpty() || indices.size() > 6144 || indices.size() % 3 || axes.size() > 6)
			return false;
		Mesh mesh;
		mesh.stride = 1 + uv.size();
		size_t count = 1;
		for (const auto &value : axes) {
			int axis;
			if (!ReadIndex(value, int(parameters.size()), axis) || std::find(mesh.axes.begin(), mesh.axes.end(), axis) != mesh.axes.end())
				return false;
			mesh.axes.push_back(axis);
			count *= parameters[axis].keys.size();
		}
		if (size_t(poses.size()) != mesh.stride * count)
			return false;
		for (const auto &value : poses) {
			float n;
			if (!ReadNumber(value, 16, n))
				return false;
			mesh.poses.push_back(n);
		}
		for (const auto &value : indices) {
			int index;
			if (!ReadIndex(value, uv.size() / 2, index))
				return false;
			mesh.indices.push_back(uint32_t(index));
		}
		for (const auto &value : uv) {
			float n;
			if (!ReadNumber(value, 1, n) || n < 0)
				return false;
		}
		// one vertex per index so no index buffer has to live beside it; the meshes are a few hundred vertices at most
		auto *data = gs_vbdata_create();
		data->num = mesh.indices.size();
		data->points = static_cast<vec3 *>(bzalloc(data->num * sizeof(vec3)));
		data->num_tex = 1;
		data->tvarray = static_cast<gs_tvertarray *>(bzalloc(sizeof(gs_tvertarray)));
		data->tvarray[0].width = 2;
		auto *coords = static_cast<vec2 *>(bzalloc(data->num * sizeof(vec2)));
		data->tvarray[0].array = coords;
		for (size_t i = 0; i < data->num; ++i) {
			coords[i].x = float(uv[int(mesh.indices[i] * 2)].toDouble());
			coords[i].y = float(uv[int(mesh.indices[i] * 2 + 1)].toDouble());
		}
		mesh.buffer = gs_vertexbuffer_create(data, GS_DYNAMIC);
		if (!mesh.buffer) {
			gs_vbdata_destroy(data);
			return false;
		}
		meshes.push_back(std::move(mesh));
	}
	return true;
}

void BongoModel::Render(gs_effect_t *effect, const std::vector<float> &values, const Camera &camera)
{
	gs_eparam_t *image = gs_effect_get_param_by_name(effect, "image");
	gs_eparam_t *opacity = gs_effect_get_param_by_name(effect, "opacity");
	const auto cull = gs_get_cull_mode();
	gs_set_cull_mode(GS_NEITHER);
	for (auto &mesh : meshes) {
		// multilinear blend of the 2^axes poses around the current parameter values, same as cubism keyform interpolation
		std::array<size_t, 64> offsets{};
		std::array<float, 64> weights{};
		weights[0] = 1;
		size_t count = 1, step = mesh.stride;
		for (int axis : mesh.axes) {
			const auto &keys = parameters[axis].keys;
			size_t k = 0;
			while (k + 2 < keys.size() && values[axis] > keys[k + 1])
				++k;
			const float fraction = std::clamp((values[axis] - keys[k]) / (keys[k + 1] - keys[k]), 0.0f, 1.0f);
			for (size_t n = 0; n < count; ++n) {
				offsets[n + count] = offsets[n] + (k + 1) * step;
				weights[n + count] = weights[n] * fraction;
				offsets[n] += k * step;
				weights[n] *= 1 - fraction;
			}
			count *= 2;
			step *= keys.size();
		}
		auto sample = [&](size_t i) {
			float value = 0;
			for (size_t n = 0; n < count; ++n)
				value += weights[n] * mesh.poses[offsets[n] + i];
			return value;
		};
		const float alpha = std::clamp(sample(0), 0.0f, 1.0f);
		if (alpha <= 0.001f)
			continue;
		auto *data = gs_vertexbuffer_get_data(mesh.buffer);
		for (size_t i = 0; i < mesh.indices.size(); ++i) {
			data->points[i].x = (sample(1 + mesh.indices[i] * 2) * camera.scaleX + camera.offsetX + 1) * 0.5f * camera.width;
			data->points[i].y = (1 - (sample(2 + mesh.indices[i] * 2) * camera.scaleY + camera.offsetY)) * 0.5f * camera.height;
			data->points[i].z = 0;
		}
		gs_vertexbuffer_flush(mesh.buffer);
		gs_effect_set_texture(image, texture.texture);
		gs_effect_set_float(opacity, alpha);
		while (gs_effect_loop(effect, "Draw")) {
			gs_load_vertexbuffer(mesh.buffer);
			gs_load_indexbuffer(nullptr);
			gs_draw(GS_TRIS, 0, uint32_t(mesh.indices.size()));
		}
	}
	gs_set_cull_mode(cull);
}

void BongoModel::Free()
{
	for (auto &mesh : meshes)
		gs_vertexbuffer_destroy(mesh.buffer);
	meshes.clear();
	gs_image_file_free(&texture);
	parameters.clear();
}

} // namespace rk
