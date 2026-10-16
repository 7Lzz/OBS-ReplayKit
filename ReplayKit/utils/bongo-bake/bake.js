// bakes every live2d model of a Bango Cat folder into the files the bongo cat source of replaykit.dll and the overlay preview draw: overlay-preview/bongo-models/<md5 of the moc3>.json
// usage: node bake.js [Bango Cat folder] [output folder]
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { loadCore, loadModel } = require('./cubism');

const repo = path.resolve(__dirname, '..', '..');
const bangoCat = process.argv[2] || 'C:\\Program Files\\obs-studio\\bin\\64bit\\Bango Cat';
const output = process.argv[3] || path.join(repo, 'assets', 'obs-studio', 'obs-replayKit', 'obs-custom-dock', 'overlay-preview', 'bongo-models');
// the params Bongobs-Cat Model::Update writes; every other param stays at its default there, and baking one of those (CatParamKeyDown moves feixue paws at its middle key only) would break the multilinear blend
const DRIVEN = ['ParamAngleX', 'ParamAngleY', 'ParamAngleZ', 'ParamEyeBallX', 'ParamEyeBallY', 'ParamMouseX', 'ParamMouseY', 'ParamMouseLeftDown', 'ParamMouseRightDown', 'ParamBodyAngleX', 'ParamBreath'];
// model units to pixels with the source defaults (scale 1.83 on a 1280 wide frame), only for the error report
const PIXELS_PER_UNIT = 0.615 * 1.83 * 640;

// a fixed seed so the dependency probes, and with them the files, come out the same on every run
function random(seed) {
	return () => {
		seed = (seed + 0x6d2b79f5) | 0;
		let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
		t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
		return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
	};
}

function readJson(file) {
	return JSON.parse(fs.readFileSync(file, 'utf8'));
}

// the model folders the mode configs name, with the EyeBlink group of each model3.json
function modelFolders() {
	const found = new Map();
	for (const mode of readJson(path.join(bangoCat, 'mode', 'config.json')).ModelPath) {
		const configPath = path.join(bangoCat, 'mode', mode, 'config.json');
		if (!fs.existsSync(configPath)) continue;
		const config = readJson(configPath);
		for (const key of ['CatModelPath', 'ModelLeftHandModelPath', 'ModelRightHandModelPath']) {
			if (!config[key]) continue;
			const folder = path.join(bangoCat, 'mode', mode, config[key]);
			const setting = fs.readdirSync(folder).filter((f) => f.endsWith('.model3.json')).sort()[0];
			if (!setting) continue;
			const json = readJson(path.join(folder, setting));
			const blink = (json.Groups || []).find((g) => g.Target === 'Parameter' && g.Name === 'EyeBlink');
			found.set(path.join(folder, json.FileReferences.Moc), blink ? blink.Ids : []);
		}
	}
	return found;
}

function bake(core, mocPath, blinkIds) {
	const { bytes, model } = loadModel(core, mocPath);
	const p = model.parameters, d = model.drawables;
	const driven = new Set([...DRIVEN, ...blinkIds]);
	const keyed = [];
	for (let i = 0; i < p.count; i++) if (p.keyValues[i].length && driven.has(p.ids[i])) keyed.push(i);
	const keys = keyed.map((i) => Array.from(p.keyValues[i]));
	// the plugin scales the canvas to a height of 1 (CubismModelMatrix SetHeight(1)), so poses are kept in those units
	const unit = model.canvasinfo.PixelsPerUnit / model.canvasinfo.CanvasHeight;
	const order = [...Array(d.count).keys()].sort((a, b) => d.renderOrders[a] - d.renderOrders[b]);
	const lo = keys.map((k) => k[0]), hi = keys.map((k) => k[k.length - 1]);
	const next = random(1);
	const probe = () => keys.map((k, i) => lo[i] + next() * (hi[i] - lo[i]));
	const apply = (values) => {
		p.values.set(p.defaultValues);
		keyed.forEach((pi, k) => { p.values[pi] = Math.max(p.minimumValues[pi], Math.min(p.maximumValues[pi], values[k])); });
		model.update();
	};
	const pose = (di) => [d.opacities[di], ...Array.from(d.vertexPositions[di], (x) => x * unit)];

	const renderOrder = Array.from(d.renderOrders).join();
	for (let t = 0; t < 300; t++) {
		apply(probe());
		if (Array.from(d.renderOrders).join() !== renderOrder) throw new Error('the draw order of ' + mocPath + ' moves with its parameters, which the bake cannot express');
	}
	// a param counts for a drawable when any of its keys moves it, compared at every key since a middle key can move it and the ends put it back
	const deps = new Map(order.map((di) => [di, []]));
	for (let a = 0; a < keyed.length; a++) {
		for (let t = 0; t < 48; t++) {
			const base = probe();
			const poses = keys[a].map((k) => { const v = base.slice(); v[a] = k; apply(v); return order.map(pose); });
			order.forEach((di, k) => {
				if (!deps.get(di).includes(a) && poses.some((P) => P[k].some((x, j) => Math.abs(x - poses[0][k][j]) > 1e-7))) deps.get(di).push(a);
			});
		}
	}
	const defaults = keyed.map((pi) => p.defaultValues[pi]);
	const meshes = order.map((di) => {
		const axes = deps.get(di).sort((x, y) => x - y);
		const poses = [];
		// the first axis varies fastest, the order the renderers step through
		const walk = (level, values) => {
			if (level < 0) { apply(values); poses.push(...pose(di)); return; }
			for (const k of keys[axes[level]]) { const v = values.slice(); v[axes[level]] = k; walk(level - 1, v); }
		};
		walk(axes.length - 1, defaults.slice());
		return { id: d.ids[di], uv: Array.from(d.vertexUvs[di]).map((x, i) => (i % 2 ? 1 - x : x)), indices: Array.from(d.indices[di]), axes, poses };
	});

	// how far the blend lands from the real core anywhere in the parameter space
	let worst = 0;
	for (let t = 0; t < 2000; t++) {
		const values = probe();
		apply(values);
		meshes.forEach((mesh, k) => {
			const real = pose(order[k]);
			if (real[0] <= 0.01) return;
			let offsets = [0], weights = [1], count = 1, step = mesh.uv.length + 1;
			for (const axis of mesh.axes) {
				const k0 = keys[axis];
				let j = 0;
				while (j + 2 < k0.length && values[axis] > k0[j + 1]) j++;
				const f = Math.max(0, Math.min(1, (values[axis] - k0[j]) / (k0[j + 1] - k0[j])));
				for (let n = 0; n < count; n++) { offsets[n + count] = offsets[n] + (j + 1) * step; weights[n + count] = weights[n] * f; offsets[n] += j * step; weights[n] *= 1 - f; }
				count *= 2; step *= k0.length;
			}
			for (let i = 1; i < real.length; i++) {
				let v = 0;
				for (let n = 0; n < count; n++) v += weights[n] * mesh.poses[offsets[n] + i];
				worst = Math.max(worst, Math.abs(v - real[i]) * PIXELS_PER_UNIT);
			}
		});
	}

	const md5 = crypto.createHash('md5').update(bytes).digest('hex');
	const out = {
		version: 4,
		moc3: md5,
		parameters: keyed.map((pi, k) => ({ id: p.ids[pi], keys: keys[k], default: p.defaultValues[pi], min: p.minimumValues[pi], max: p.maximumValues[pi] })),
		meshes: meshes.map((m) => ({ id: m.id, uv: m.uv.map((x) => +x.toFixed(7)), indices: m.indices, axes: m.axes, poses: m.poses.map((x) => +x.toFixed(6)) })),
	};
	return { md5, json: JSON.stringify(out), worst };
}

loadCore().then((core) => {
	fs.mkdirSync(output, { recursive: true });
	for (const [mocPath, blinkIds] of modelFolders()) {
		const { md5, json, worst } = bake(core, mocPath, blinkIds);
		fs.writeFileSync(path.join(output, md5 + '.json'), json);
		console.log(md5 + '.json  ' + path.relative(bangoCat, mocPath) + '  worst ' + worst.toFixed(3) + ' px');
	}
});
