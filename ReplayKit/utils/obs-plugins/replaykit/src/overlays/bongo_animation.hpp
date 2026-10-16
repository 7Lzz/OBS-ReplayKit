// the parameter values of one bongo model per frame, built in the order Bongobs-Cat Model::Update builds them on the cubism model: CubismEyeBlink, mouse tracking, the mouse arm, the click paws, then CubismBreath, each step clamped like SetParameterValue
#pragma once

#include "overlays/bongo_model.hpp"

#include <algorithm>
#include <cmath>
#include <vector>
#include <QRandomGenerator>

namespace rk {

struct BongoMotionInput {
	float mouseX = 0.5f, mouseY = 0.5f;
	bool left = false, right = false;
	bool breath = true, eyeBlink = true, track = true, horizontalFlip = true, verticalFlip = true;
};

class BongoAnimation {
public:
	// each model keeps its own blink and breath clock, and they only run while the model is drawn, as in the plugin
	void Tick(double seconds)
	{
		if (!std::isfinite(seconds) || seconds <= 0)
			return;
		time += seconds;
		// CubismEyeBlink with its defaults: next blink at random * (2 * 4 s - 1), 0.1 s closing, 0.05 s closed, 0.15 s opening
		const double age = time - nextBlink;
		if (age < 0)
			eye = 1;
		else if (age < 0.1)
			eye = float(1 - age / 0.1);
		else if (age < 0.15)
			eye = 0;
		else if (age < 0.3)
			eye = float((age - 0.15) / 0.15);
		else {
			eye = 1;
			nextBlink = time + QRandomGenerator::global()->generateDouble() * 7;
		}
	}

	std::vector<float> Values(const BongoModel &model, const QStringList &blinkIds, const BongoMotionInput &in) const
	{
		std::vector<float> values;
		for (const auto &parameter : model.parameters)
			values.push_back(parameter.initial);
		auto index = [&](const char *id) {
			for (size_t i = 0; i < model.parameters.size(); ++i) {
				if (model.parameters[i].id == QLatin1String(id))
					return int(i);
			}
			return -1;
		};
		auto set = [&](int i, double value) {
			if (i >= 0)
				values[i] = float(std::clamp(value, double(model.parameters[i].minimum), double(model.parameters[i].maximum)));
		};
		auto add = [&](const char *id, double value) {
			const int i = index(id);
			if (i >= 0)
				set(i, values[i] + value);
		};
		const double x = in.mouseX, y = in.mouseY;
		if (in.eyeBlink) {
			for (const QString &id : blinkIds)
				set(index(id.toLatin1().constData()), eye);
		}
		if (in.track) {
			add("ParamAngleX", (1 - x) * 30);
			add("ParamAngleY", (1 - y) * 30);
			add("ParamAngleZ", (1 - x) * (1 - y) * -30);
			add("ParamEyeBallX", (1 - x) * 30);
			add("ParamEyeBallY", (1 - y) * 30);
		}
		add("ParamMouseX", in.horizontalFlip ? x * 60 - 30 : (1 - x) * 60 - 30);
		add("ParamMouseY", in.verticalFlip ? (1 - y) * 60 - 30 : y * 60 - 30);
		add("ParamMouseRightDown", in.right ? 1 : 0);
		add("ParamMouseLeftDown", in.left ? 1 : 0);
		if (in.breath) {
			// CubismBreath adds offset + peak * sin(t / cycle) at weight 0.5, with t = seconds * 2 * 3.14159
			const double t = time * 2 * 3.14159;
			add("ParamAngleX", 0.5 * (15 * std::sin(t / 6.5345)));
			add("ParamAngleY", 0.5 * (8 * std::sin(t / 3.5345)));
			add("ParamAngleZ", 0.5 * (10 * std::sin(t / 5.5345)));
			add("ParamBodyAngleX", 0.5 * (4 * std::sin(t / 15.5345)));
			add("ParamBreath", 0.5 * (0.5 + 0.5 * std::sin(t / 3.2345)));
		}
		return values;
	}

private:
	double time = 0;
	double nextBlink = QRandomGenerator::global()->generateDouble() * 7;
	float eye = 1;
};

} // namespace rk
