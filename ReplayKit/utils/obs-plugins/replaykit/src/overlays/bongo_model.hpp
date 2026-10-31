// one Bango Cat live2d model as vertex poses baked per parameter key from its moc3, blended on the cpu the way the cubism core interpolates keyforms
#pragma once

#include <vector>

namespace rk {

// a png as a straight alpha rgba texture, decoded by qt instead of the gs_image_file api, which obs 33 replaced by an incompatible one so a dll importing either set fails to load on the other; needs the graphics context, null when the file does not decode
gs_texture_t *BongoTexture(const QString &path);

struct BongoModel {
	struct Parameter {
		QString id;
		std::vector<float> keys;
		float initial = 0, minimum = 0, maximum = 0;
	};
	struct Mesh {
		std::vector<uint32_t> indices;
		std::vector<float> poses;
		std::vector<int> axes;
		size_t stride = 0;
		gs_vertbuffer_t *buffer = nullptr;
	};
	// where model units land in the frame: ndc = units * scale + offset, then the frame is width x height pixels
	struct Camera {
		float scaleX, scaleY, offsetX, offsetY, width, height;
	};
	std::vector<Parameter> parameters;
	std::vector<Mesh> meshes;
	gs_texture_t *texture = nullptr;

	// the bake is chosen by the md5 of the moc3, so a model that was swapped for another draws nothing instead of the wrong shape
	bool Load(const QString &moc3, const QString &texturePath, const QString &bakes);
	void Render(gs_effect_t *effect, const std::vector<float> &values, const Camera &camera);
	void Free();
};

} // namespace rk
