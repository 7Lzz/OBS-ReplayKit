"""expected first frame of a bongo cat source with no input held, composed the way Bongobs-Cat View::Render does, then diffed against the obs screenshots screenshots.ps1 took. usage: python compare.py cases.json [Bango Cat folder]"""
import json, os, subprocess, sys, tempfile
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = (sys.argv[2] if len(sys.argv) > 2 else 'C:/Program Files/obs-studio/bin/64bit/Bango Cat').replace('\\', '/').rstrip('/') + '/'
FW, FH = 1280, 768


def run(case, shot):
    s = case['settings']
    w = s.get('width', 1280) // 32 * 32
    h = s.get('height', 768)
    live2d, mask = s.get('live2d', True), s.get('mask', False)
    modes = json.load(open(ROOT + 'mode/config.json'))['ModelPath']
    modes = [m for m in modes if os.path.exists(ROOT + 'mode/' + m + '/config.json')]
    mode = s.get('mode', '') if s.get('mode', '') in modes else modes[0]
    md = ROOT + 'mode/' + mode + '/'
    cfg = json.load(open(md + 'config.json', encoding='utf-8'))
    frame = np.zeros((FH, FW, 4))

    def over(src):
        a = src[..., 3:4]
        frame[..., :3] = src[..., :3] * a + frame[..., :3] * (1 - a)
        frame[..., 3:4] = a + frame[..., 3:4] * (1 - a)

    def sprite(path):
        over(np.asarray(Image.open(path).convert('RGBA').resize((FW, FH), Image.BILINEAR)).astype(np.float64) / 255)

    def model(folder):
        name = [f for f in sorted(os.listdir(folder)) if f.endswith('.model3.json')][0]
        setting = json.load(open(folder + '/' + name, encoding='utf-8'))
        blink = next((g['Ids'] for g in setting.get('Groups', []) if g['Target'] == 'Parameter' and g['Name'] == 'EyeBlink'), [])
        tex = np.asarray(Image.open(folder + '/' + setting['FileReferences']['Textures'][0]).convert('RGBA')).astype(np.float64) / 255
        args = {'moc': folder + '/' + setting['FileReferences']['Moc'], 'mouse': [0.5, 0.5], 'blinkIds': blink,
                'eyeblink': s.get('eyeblink', True), 'track': s.get('track', True), 'breath': s.get('breath', True),
                'hflip': s.get('mouse_horizontal_flip', True), 'vflip': s.get('mouse_vertical_flip', True),
                'cam': {'sx': 0.615 * s.get('scale', 1.83), 'sy': 1.1 * s.get('scale', 1.83), 'ox': s.get('x', 0) - (1280 - w) / 1280, 'oy': s.get('y', 0.02) - (768 - h) / 768}}
        with tempfile.NamedTemporaryFile('w', suffix='.json', delete=False) as f:
            json.dump(args, f)
        tris = json.loads(subprocess.run(['node', os.path.join(HERE, 'reference.js'), f.name], capture_output=True, text=True, cwd=HERE).stdout.split('\n')[-1])
        os.unlink(f.name)
        th, tw = tex.shape[:2]
        for mesh in tris:
            layer = np.zeros((FH, FW, 4))
            pts = np.array(mesh['pts']); uvs = np.array(mesh['uvs']); ind = mesh['ind']
            for t in range(0, len(ind), 3):
                A, B, Cc = pts[ind[t]], pts[ind[t + 1]], pts[ind[t + 2]]
                x0, x1 = int(max(0, np.floor(min(A[0], B[0], Cc[0])))), int(min(FW - 1, np.ceil(max(A[0], B[0], Cc[0]))))
                y0, y1 = int(max(0, np.floor(min(A[1], B[1], Cc[1])))), int(min(FH - 1, np.ceil(max(A[1], B[1], Cc[1]))))
                if x1 < x0 or y1 < y0: continue
                X, Y = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
                den = (B[1] - Cc[1]) * (A[0] - Cc[0]) + (Cc[0] - B[0]) * (A[1] - Cc[1])
                if abs(den) < 1e-9: continue
                l1 = ((B[1] - Cc[1]) * (X - Cc[0]) + (Cc[0] - B[0]) * (Y - Cc[1])) / den
                l2 = ((Cc[1] - A[1]) * (X - Cc[0]) + (A[0] - Cc[0]) * (Y - Cc[1])) / den
                l3 = 1 - l1 - l2
                inside = (l1 >= 0) & (l2 >= 0) & (l3 >= 0)
                if not inside.any(): continue
                ua, ub, uc = uvs[ind[t]], uvs[ind[t + 1]], uvs[ind[t + 2]]
                u = l1 * ua[0] + l2 * ub[0] + l3 * uc[0]; v = l1 * ua[1] + l2 * ub[1] + l3 * uc[1]
                xx = np.clip(u * tw - 0.5, 0, tw - 1); yy = np.clip(v * th - 0.5, 0, th - 1)
                xa = np.floor(xx).astype(int); ya = np.floor(yy).astype(int); xb = np.minimum(xa + 1, tw - 1); yb = np.minimum(ya + 1, th - 1)
                fx = (xx - xa)[..., None]; fy = (yy - ya)[..., None]
                smp = (tex[ya, xa] * (1 - fx) + tex[ya, xb] * fx) * (1 - fy) + (tex[yb, xa] * (1 - fx) + tex[yb, xb] * fx) * fy
                smp[..., 3] *= mesh['opacity']
                layer[y0:y1 + 1, x0:x1 + 1][inside] = smp[inside]
            over(layer)

    has_model = cfg['HasModel']
    back = cfg['BackgroundImageName'] if live2d and has_model else cfg['CatBackgroundImageName']
    if back: sprite(md + back)
    if live2d and has_model and cfg['CatModelPath']: model(md + cfg['CatModelPath'])
    left_up = right_up = True
    if cfg['LeftHandImagePath'] and cfg['LeftHandImageName']: pass
    elif cfg['ModelHasLeftHandModel']:
        if not live2d: model(md + cfg['ModelLeftHandModelPath'])
        left_up = False
    elif live2d: left_up = False
    if cfg['RightHandImagePath'] and cfg['RightHandImageName']: pass
    elif cfg['ModelHasRightHandModel']:
        if not live2d: model(md + cfg['ModelRightHandModelPath'])
        right_up = False
    elif live2d: right_up = False
    if mask:
        face = json.load(open(ROOT + 'face/config.json'))
        sprite(ROOT + 'face/' + face['FaceImageName'][0])
    if left_up and cfg['LeftHandImagePath'] and cfg['LeftHandUpImageName']: sprite(md + cfg['LeftHandImagePath'] + '/' + cfg['LeftHandUpImageName'])
    if right_up and cfg['RightHandImagePath'] and cfg['RightHandUpImageName']: sprite(md + cfg['RightHandImagePath'] + '/' + cfg['RightHandUpImageName'])
    ref = frame[FH - h:FH, 0:w] if h <= FH else frame
    got = np.asarray(Image.open(shot).convert('RGBA')).astype(np.float64) / 255
    if got.shape != ref.shape:
        return 'size mismatch %s vs %s' % (got.shape, ref.shape)
    per = (np.abs(ref - got) * 255).max(axis=2)
    Image.fromarray((np.clip(ref, 0, 1) * 255).astype(np.uint8)).save(shot.replace('.png', '-ref.png'))
    return 'mean %.3f p99 %.1f max %.0f px>32 %d' % (per.mean(), np.percentile(per, 99), per.max(), (per > 32).sum())


if __name__ == '__main__':
    cases = json.load(open(sys.argv[1], encoding='utf-8-sig'))
    for case in cases:
        print('%-28s %s' % (case['name'], run(case, case['shot'])))
