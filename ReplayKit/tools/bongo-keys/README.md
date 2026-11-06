# Bongo Cat key labels

Each Bongo Cat mode draws its key labels into its pictures: the keyboard on the background (`bg.png`, `catbg.png`) and one picture per key held (`keyboard/0.png`, …). When a key is given another key in ReplayKit (the `bongoKeys` setting), its picture would still show the old label. This folder makes what is needed to show the new one instead, in `assets/obs-studio/obs-replayKit/obs-custom-dock/overlay-preview/bongo-keys/`.

## Matching the artist

The standard and keyboard modes' labels are Comic Sans, drawn onto each key by hand: every key at its own size, stretch, lean and place. Comic Relief (an open Comic Sans with its shapes and metrics) came out closest of about 130 handwriting, comic and rounded fonts scored against them. For each key, the font's version of the key's own label is laid over the original and nudged (scale across and down, lean, place) until it covers it best. A new label on that key is then drawn through the same fit, so it lands as the artist would have drawn it there.

- The standard mode's labels are Comic Relief Regular thickened to their weight (strokes about 15% of the letter's height); the keyboard mode's are Bold (18%).
- Comic Relief draws a few glyphs unlike the pictures (its 4 is closed, its 1 has a foot and a base). Those, and the others where it improves the match (1–7, E, R, D, S, and the keyboard mode's R), are traced from the pictures instead: the label on the key and on the held key is carried into the font's pixels through the key's fit, averaged, thinned to its centre line, and stroked again with a round brush at the font's weight. Words and other keys that use these characters (F1, F5, num 4) use the traced glyphs too.
- The keyboard mode's arrows are its own drawings, cut from the pictures.
- The feixue mode's labels are Arial: Arimo at weight 550, which matches them almost pixel for pixel.

`fit` in the manifest is how well each key's own label is matched (intersection over union of the two, 1 being identical); the standard mode's digits are the lowest because they are a few pixels tall and blurred in the pictures.

## The files

- `hand-<code>.png`, `handbold-<code>.png`, `plain-<code>.png`: every key ReplayKit lets you pick (the `KeyboardEvent.code` the setting stores, lowercased) as a white label on transparent, for the standard, keyboard and feixue modes.
  - `-short`: a shorter label (`bksp`, `caps`, a numpad key without its `num` line) for a key with too little room for the long one.
  - `-tiny`: the keycap symbol (⇧ ⇪ ⌫ ↵ ⇥ ⌃ ⇱ ⇲ ⇞ ⇟ ␣) for a key with too little room even for that.
- `face-<mode>-rest-<n>.png`, `face-<mode>-pressed-<n>.png`: key `n` of a mode with its label taken out, as a patch of the background and of its pressed picture. Only the pixels that change are in it, all of them opaque, so drawing the patch over the picture replaces them.
- `manifest.json`: how to draw on every key (below).

The mania mode is left out: its keys are coloured blocks without a label.

## Making them again

Only needed when the pictures in `assets/Bango Cat.zip` change, or to redraw the labels:

```
pip install pillow numpy opencv-python-headless scikit-image
python make.py "C:\Program Files\obs-studio\bin\64bit\Bango Cat"
```

The fonts are fetched from google/fonts the first time. The same input gives the same files, byte for byte.

```
python render.py "C:\Program Files\obs-studio\bin\64bit\Bango Cat" standard F1,,KeyB out.png
```

draws a mode with other keys on it, resting and with every key held, the way the runtimes do: `bongo.cpp` (`Relabel`) in OBS and `_live2d-bongo.html` in the Settings preview; `relabel()` in it is the reference for both.

## manifest.json

```
version    2
styles     hand | handbold | plain -> code -> { file, w, h, smallCap?, short?: {file, w, h}, tiny?: {file, w, h}, source? }
modes      standard | keyboard | feixue -> {
  style        the labels the mode uses
  backgrounds  { file: md5 } the backgrounds the faces were cut from
  slots        [ per key of the mode, in the order of its config:
    key        its name in the config ("q", "lctrl", …)
    label      the code of the label it shows today (the keyboard mode's arrows are named for the key they light for,
               which the original plugin swaps for left and right; the label is what the picture shows)
    blank      it shows no label (the standard space bar)
    matrix     [m11, m12, m21, m22] and
    anchor     [x, y]: sprite pixel (u, v) of a label at its natural size lands at
               x = anchor.x + m11 (u - w/2) + m21 (v - h/2),  y = anchor.y + m12 (u - w/2) + m22 (v - h/2)
               in pixels of the picture (612 x 354). For a key with a label of its own this is the fit of that label;
               the others are drawn like the mode's fitted keys
    room       [across, down] the most a label may measure, in sprite pixels, before it is shrunk to fit the key
    fit        how well the key's own label is matched (null for a key without one)
    rest       { patch, at: [x, y], color } its blank face on the backgrounds and the colour of its labels there
    pressed    { patch, at, color, md5 } the same on its pressed picture, and the md5 of that picture
  ]
}
```

## Drawing a key with another key's label

The helper gives the `replaykit-bongo` source the mode's codes as `key_codes` (a comma list, "" where a key keeps its own) next to their virtual keys in `keys`; the preview gets them from the `bongoKeys` setting, and the md5 of each picture in `/bongo-cat/manifest`. Both redraw the mode's pictures when its codes change.


For key `i` of a mode given `code`:

1. Leave it alone when `code` is empty, equals `label`, is not in `styles[style]`, or when the pressed picture's md5 is not `pressed.md5` (the pictures were changed by hand). Leave a background alone when its md5 is not the one in `backgrounds`.
2. Draw `rest.patch` at `rest.at` over each background, and `pressed.patch` at `pressed.at` over the pressed picture. Do this for every key that changes before drawing any label.
3. Pick the form and its shrink `f`, where `fit(e) = min(1, room[0] / e.w, room[1] / e.h)` and `down = hypot(m21, m22)`:
   - the long label at `f = fit(entry)`, unless it has a `short` and `f < 0.55` or `smallCap * f * down < 8`;
   - then the short one, unless it has a `tiny` and `fit(short) < 0.55`, which takes the tiny one;
   - a label with only a `tiny` takes it when `f < 0.55`.
4. Draw the white sprite tinted to `rest.color` on the backgrounds and to `pressed.color` on the pressed picture, through the matrix times `f`. The manifest counts pixels from their middles (like OpenCV), Qt and canvas from their corners, so half a pixel goes each way: in Qt `QTransform(m11*f, m12*f, m21*f, m22*f, anchor.x + 0.5, anchor.y + 0.5)` and draw the sprite at `(-w/2 - 0.5, -h/2 - 0.5)`; on a canvas `setTransform(m11*f, m12*f, m21*f, m22*f, anchor.x + 0.5, anchor.y + 0.5)` then `drawImage(sprite, -w/2 - 0.5, -h/2 - 0.5)`. A sprite drawn at under half its size reads better shrunk smoothly first (Qt `scaled(..., SmoothTransformation)`, or a canvas drawn at twice the size and halved), with the matrix scaled up by the same amount.

The pictures are drawn stretched over the 1280×768 frame, so all of this happens on the pictures before they are uploaded, once per change of mode or keys.

## Licences

The faces, the arrows and the traced glyphs come from the Bango Cat files ReplayKit already ships. Comic Relief (Jeff Davis) and Arimo (Steve Matteson) are under the SIL Open Font License 1.1; only pictures drawn with them are kept here, not the fonts.
