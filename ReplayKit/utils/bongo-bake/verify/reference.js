// triangles of one moc3 from the real cubism core with Bongobs-Cat Model::Update applied at time 0, in pixels of the 1280x768 plugin frame; compare.py feeds it a json request file and reads stdout
const fs = require('fs');
const path = require('path');
const { loadCore, loadModel } = require('../cubism');

const request = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
loadCore().then((core) => {
	const { model } = loadModel(core, request.moc);
	const p = model.parameters, d = model.drawables;
	const index = {};
	for (let i = 0; i < p.count; i++) index[p.ids[i]] = i;
	p.values.set(p.defaultValues);
	const set = (id, v) => { const i = index[id]; if (i !== undefined) p.values[i] = Math.max(p.minimumValues[i], Math.min(p.maximumValues[i], v)); };
	const add = (id, v) => { const i = index[id]; if (i !== undefined) set(id, p.values[i] + v); };
	const [x, y] = request.mouse;
	if (request.eyeblink) request.blinkIds.forEach((id) => set(id, 1));
	if (request.track) {
		add('ParamAngleX', (1 - x) * 30); add('ParamAngleY', (1 - y) * 30); add('ParamAngleZ', (1 - x) * (1 - y) * -30);
		add('ParamEyeBallX', (1 - x) * 30); add('ParamEyeBallY', (1 - y) * 30);
	}
	add('ParamMouseX', request.hflip ? x * 60 - 30 : (1 - x) * 60 - 30);
	add('ParamMouseY', request.vflip ? (1 - y) * 60 - 30 : y * 60 - 30);
	// breath at time 0: every sine is 0, only ParamBreath gets its 0.5 offset at weight 0.5
	if (request.breath) add('ParamBreath', 0.25);
	model.update();
	const info = model.canvasinfo, unit = info.PixelsPerUnit / info.CanvasHeight, cam = request.cam;
	const order = [...Array(d.count).keys()].sort((a, b) => d.renderOrders[a] - d.renderOrders[b]);
	const out = [];
	for (const di of order) {
		if (d.opacities[di] <= 0.001) continue;
		const vp = d.vertexPositions[di], uv = d.vertexUvs[di], pts = [], uvs = [];
		for (let v = 0; v < vp.length / 2; v++) {
			pts.push([(vp[2 * v] * unit * cam.sx + cam.ox + 1) / 2 * 1280, (1 - (vp[2 * v + 1] * unit * cam.sy + cam.oy)) / 2 * 768]);
			uvs.push([uv[2 * v], 1 - uv[2 * v + 1]]);
		}
		out.push({ opacity: d.opacities[di], pts, uvs, ind: Array.from(d.indices[di]) });
	}
	process.stdout.write(JSON.stringify(out));
});
