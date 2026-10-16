// loads the cubism web core (live2dcubismcore.js beside this file) in a browser-like vm context, since its emscripten loader takes the wrong path under node globals
const fs = require('fs');
const path = require('path');
const vm = require('vm');

function loadCore() {
	const context = { console, setTimeout, clearTimeout, atob, btoa, TextDecoder, performance };
	context.window = context;
	context.self = context;
	context.location = { href: 'http://localhost/' };
	context.document = { currentScript: { src: 'http://localhost/live2dcubismcore.js' } };
	vm.createContext(context);
	vm.runInContext(fs.readFileSync(path.join(__dirname, 'live2dcubismcore.js'), 'utf8') + ';window.Live2DCubismCore = Live2DCubismCore;', context);
	const core = context.Live2DCubismCore;
	// the core finishes starting on a timer, so callers wait for this promise
	return new Promise((resolve) => setTimeout(() => resolve(core), 200));
}

function loadModel(core, mocPath) {
	const bytes = fs.readFileSync(mocPath);
	const buffer = new ArrayBuffer(bytes.length);
	new Uint8Array(buffer).set(bytes);
	const moc = core.Moc.fromArrayBuffer(buffer);
	if (!moc) throw new Error('not a moc3 the core can read: ' + mocPath);
	return { bytes, model: core.Model.fromMoc(moc) };
}

module.exports = { loadCore, loadModel };
