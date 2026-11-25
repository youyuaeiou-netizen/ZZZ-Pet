"""Build the supplied token/sleep/wake clips against the installed idle palette."""
from pathlib import Path
import hashlib, json, shutil, sys
import cv2
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path('C:/pet/ZZZ-Pet')
sys.path.insert(0, str(ROOT/'tools'))
from build_flat2d_pack import key_green, bbox
from build_dragon_human_video import blend

RUN = ROOT/'build/generated/actions-update-20261018'
PACK = RUN/'packs/gpt-dragon-chibi-v4'
SOURCE = Path('C:/Users/h/Desktop/视频/Q版')
FPS = 24
SIZE = (640, 576)


def palette(a):
    c = a[..., :3].astype(float)
    # Pale lilac hair/clothing only: exclude gold token, skin and keyed edges.
    mask = (a[..., 3] > 240) & (c[..., 2] >= c[..., 0]-5) & (c.min(2) > 100)
    return np.percentile(c[mask], [10, 50, 90], axis=0)


def smooth(values, radius=4):
    return np.array([np.mean(values[max(0, i-radius):i+radius+1], axis=0)
                     for i in range(len(values))])


def read_clip(name):
    path = SOURCE/(name+'.mp4')
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    cap = cv2.VideoCapture(str(path))
    native_fps = cap.get(cv2.CAP_PROP_FPS)
    frames, n, target = [], 0, 0
    while True:
        ok, bgr = cap.read()
        if not ok:
            break
        if n/native_fps + .5/native_fps >= target/FPS and target < 120:
            frames.append(key_green(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGBA)))
            target += 1
        n += 1
    cap.release()
    assert len(frames) == 120
    return frames, {'path': str(path), 'sha256': digest, 'decodedFrames': n, 'usedFrames': len(frames)}


def normalize(frames, reference_palette):
    boxes = np.array([bbox(a) for a in frames])
    curves = smooth([palette(a) for a in frames])
    # One physical scale for all three clips, including the prone poses.
    scale = 408/634
    base_x = 260-631*scale
    xs = np.clip(np.full(len(frames), base_x), 5-boxes[:, 0]*scale, 635-boxes[:, 2]*scale)
    xs = np.clip(smooth(xs), 5-boxes[:, 0]*scale, 635-boxes[:, 2]*scale)
    floors = smooth(boxes[:, 3], 2)
    normalized = []
    for i, a in enumerate(frames):
        lut = np.empty((256, 3), np.uint8)
        for c in range(3):
            lut[:, c] = np.clip(np.interp(np.arange(256), [0, *curves[i, :, c], 255],
                                         [0, *reference_palette[:, c], 255]), 0, 255)
        adjusted = a.copy()
        for c in range(3):
            adjusted[..., c] = lut[a[..., c], c]
        af = adjusted.astype(np.float32)/255
        af[..., :3] *= af[..., 3:]
        matrix = np.array([[scale, 0, xs[i]], [0, scale, 520-floors[i]*scale]], np.float32)
        moved = cv2.warpAffine(af, matrix, SIZE, flags=cv2.INTER_CUBIC)
        moved[..., :3] /= np.maximum(moved[..., 3:], 1/255)
        result = np.uint8(np.clip(moved*255+.5, 0, 255))
        edge = result[..., 3] < 250
        result[..., 1][edge] = np.minimum(result[..., 1], np.maximum(result[..., 0], result[..., 2]))[edge]
        result[result[..., 3] == 0] = 0
        b = bbox(result)
        assert min(b[:2]) >= 2 and b[2] <= 638 and b[3] <= 574, (i, b)
        normalized.append(result)
    return normalized, {'scale': scale, 'rawPaleMedianRange': [curves[:, 1].min(0).tolist(), curves[:, 1].max(0).tolist()],
                         'correctedPaleMedianRange': [np.min([palette(a)[1] for a in normalized], axis=0).tolist(),
                                                      np.max([palette(a)[1] for a in normalized], axis=0).tolist()],
                         'horizontalCorrectionPx': [float(min(xs)-base_x), float(max(xs)-base_x)]}


def join_start(frames, target, count=6):
    for i in range(count):
        frames[i] = blend(target, frames[i], i/(count-1))
    frames[0] = target.copy()


def join_end(frames, target, count=6):
    for i in range(count):
        frames[-count+i] = blend(frames[-count+i], target, (i+1)/count)
    frames[-1] = target.copy()


def main():
    if PACK.exists():
        raise FileExistsError(PACK)
    shutil.copytree(ROOT/'characters/gpt-dragon-chibi-v4', PACK)
    manifest = json.loads((PACK/'character.json').read_text(encoding='utf-8-sig'))
    idle = next(a for a in manifest['actions'] if a['id'] == 'idle')
    stand = np.array(Image.open(PACK/idle['frames'][0]['path']).convert('RGBA'))
    target_palette = np.median([palette(np.array(Image.open(PACK/f['path']).convert('RGBA')))
                                for f in idle['frames'][:6]], axis=0)
    report = {'sourceIntegrity': 'pending', 'fps': FPS, 'paletteReference': 'existing idle first six frames',
              'referencePalette': target_palette.tolist(), 'sources': {}, 'actions': {}}
    clips = {}
    for aid, name in [('feed', 'token'), ('sleep_enter', '睡觉'), ('wake', '睡醒')]:
        raw, report['sources'][aid] = read_clip(name)
        clips[aid], report['actions'][aid] = normalize(raw, target_palette)
        print('Prepared', aid, flush=True)
        del raw
    # Keep forward motion; the settled final two seconds supply the sleep loop.
    clips['sleep'] = clips['sleep_enter'][72:]
    clips['sleep_enter'] = clips['sleep_enter'][:72]
    resting = clips['sleep'][0].copy()
    join_end(clips['sleep'], resting)
    join_start(clips['sleep_enter'], stand)
    join_end(clips['sleep_enter'], resting)
    join_start(clips['wake'], resting)
    join_end(clips['wake'], stand)
    join_start(clips['feed'], stand)
    join_end(clips['feed'], stand)
    specs = [('feed', '吃Token', '龙娘互动', False, [], 'idle'),
             ('sleep_enter', '睡觉', '休息', False, ['idle_timeout'], 'sleep'),
             ('sleep', None, '休息', True, [], None),
             ('wake', '睡醒', '休息', False, ['notification'], 'idle')]
    for aid, name, category, loop, triggers, nxt in specs:
        action = dict(id=aid, displayName=name, category=category, loop=loop, priority=10,
                      triggers=triggers, weight=1, cooldownMs=0,
                      interruptibleBy=['click', 'double_click', 'drag_start', 'notification'], frames=[])
        if nxt:
            action['nextActionId'] = nxt
        if aid == 'sleep':
            action['showSleepIndicator'] = True
        for i, a in enumerate(clips[aid]):
            relative = f'animations/{aid}-20261018/{i:04d}.png'
            path = PACK/relative
            path.parent.mkdir(exist_ok=True, parents=True)
            Image.fromarray(a).save(path)
            action['frames'].append(dict(path=relative, durationMs=round((i+1)*1000/FPS)-round(i*1000/FPS)))
        manifest['actions'].append(action)
        report['actions'].setdefault(aid, {}).update(frames=len(action['frames']), durationMs=sum(f['durationMs'] for f in action['frames']))
    manifest.update(packVersion='1.1.0', wakeActionId='wake')
    # Preserve the existing manual-only preference and idle/heart/drag/release assets.
    (PACK/'character.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    seams = [('feed', -1, None, 0), ('feed', 0, None, 0), ('sleep_enter', 0, None, 0),
             ('sleep_enter', -1, 'sleep', 0), ('sleep', -1, 'sleep', 0),
             ('wake', 0, 'sleep', 0), ('wake', -1, None, 0)]
    report['endpointMaxPixelDifference'] = {f'{a}:{i}->{b or "idle"}:{j}': int(np.abs(clips[a][i].astype(int)-(stand if b is None else clips[b][j]).astype(int)).max()) for a, i, b, j in seams}
    assert not any(report['endpointMaxPixelDifference'].values())
    sheet = Image.new('RGB', (1500, 4*290), '#242430')
    draw = ImageDraw.Draw(sheet)
    for row, aid in enumerate(['feed', 'sleep_enter', 'sleep', 'wake']):
        for col, i in enumerate(np.linspace(0, len(clips[aid])-1, 5).astype(int)):
            im = Image.fromarray(clips[aid][i]).resize((300, 270), Image.Resampling.LANCZOS)
            bg = Image.new('RGBA', (300, 270), '#242430' if col % 2 == 0 else '#f3f0eb')
            bg.alpha_composite(im)
            sheet.paste(bg.convert('RGB'), (col*300, row*290+20))
            draw.text((col*300+5, row*290+2), f'{aid} {i/FPS:.2f}s', fill='white')
    sheet.save(RUN/'processed-review.png')
    sequence = []
    for f in idle['frames']:
        a = np.array(Image.open(PACK/f['path']).convert('RGBA'))
        sequence.extend([a]*max(1, round(f['durationMs']*FPS/1000)))
    sequence += clips['feed'] + [stand]*12 + clips['sleep_enter'] + clips['sleep']*2 + clips['wake'] + [stand]*24
    writer = cv2.VideoWriter(str(RUN/'continuous-preview.mp4'), cv2.VideoWriter_fourcc(*'mp4v'), FPS, (640, 288))
    for a in sequence:
        a = cv2.resize(a, (320, 288), interpolation=cv2.INTER_AREA).astype(float)
        alpha = a[..., 3:]/255
        board = np.concatenate([a[..., :3]*alpha+np.array(bg)*(1-alpha) for bg in [(36, 36, 48), (243, 240, 235)]], axis=1).astype(np.uint8)
        writer.write(cv2.cvtColor(board, cv2.COLOR_RGB2BGR))
    writer.release()
    for s in report['sources'].values():
        assert hashlib.sha256(Path(s['path']).read_bytes()).hexdigest() == s['sha256']
    report.update(sourceIntegrity='verified_unchanged', status='candidate', frameCount=sum(len(a['frames']) for a in manifest['actions']),
                  pack=str(PACK), sourceAudio='not imported', sleepLoopSourceSeconds=[3, 5], endpointBlendMs=250)
    (RUN/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__':
    main()
