# Bongo Cat model bakes

ReplayKit draws Bongo Cat itself (the `replaykit-bongo` source in `replaykit.dll` and the overlay preview in the dock pages). It uses the Bango Cat folder that the installer puts next to `obs64.exe`, but it does not run the Live2D runtime. Instead, each Live2D model is baked once: the vertex positions of every drawable at every key of the parameters the original plugin drives. At runtime they are blended the way the Cubism core blends keyforms. The bakes are `assets/obs-studio/obs-replayKit/obs-custom-dock/overlay-preview/bongo-models/<md5 of the moc3>.json`. A model whose moc3 has no matching bake is not drawn.

Re-bake when a model in `assets/Bango Cat.zip` changes:

```
node bake.js "C:\Program Files\obs-studio\bin\64bit\Bango Cat"
```

It writes one file per moc3 and prints how far the blend lands from the real core (well under a pixel today). `live2dcubismcore.js` is the Live2D Cubism Core for Web, used only by this tool. It ships under the Live2D Proprietary Software License, the same core the original plugin links.

## Checking the renderer against the original

With OBS running and the plugin loaded:

```
powershell -File verify\screenshots.ps1
python verify\compare.py "%TEMP%\ReplayKit\scratch\bongo-verify\cases.json"
```

`screenshots.ps1` creates temporary `replaykit-bongo` inputs in every mode and property combination in a scene that is never shown, saves a screenshot of each, and removes them again. `compare.py` renders the same frames independently, from the real Cubism core (`verify/reference.js`) and the layer rules of Bongobs-Cat `View::Render`, and prints the per-pixel difference. Expect a mean under 0.5 and a p99 around 1 (out of 255). The few larger differences sit on sprite edges and nearly transparent pixels.
