"""Build the supplied human dragon videos; keep sources and chibi form unchanged."""
from pathlib import Path
import argparse, copy, hashlib, json
import cv2
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
FPS = 24
SIZE = (900, 1000)
ANCHOR = (450, 960)
HEIGHT = 816


def bounds(a):
    y, x = np.where(a[..., 3] > 12)
    if not len(x):
        raise ValueError('Empty foreground')
    return [int(x.min()), int(y.min()), int(x.max())+1, int(y.max())+1]


def key(rgb):
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    green = (hsv[..., 0] >= 32) & (hsv[..., 0] <= 88) & (hsv[..., 1] >= 21)
    mask = np.uint8(~green)*255
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    for i in range(1, count):
        if stats[i, cv2.CC_STAT_AREA] < 32:
            mask[labels == i] = 0
    alpha = cv2.GaussianBlur(mask, (3, 3), .4)
    weight = mask.astype(np.float32)/255
    soft = alpha.astype(np.float32)/255
    color = cv2.GaussianBlur(rgb.astype(np.float32)*weight[..., None], (3, 3), .4)
    color = np.uint8(np.clip(color/np.maximum(soft[..., None], 1/255), 0, 255))
    edge = cv2.erode(mask, np.ones((5, 5), np.uint8)) < 255
    color[..., 1][edge] = np.minimum(color[..., 1], np.maximum(color[..., 0], color[..., 2]))[edge]
    color[alpha == 0] = 0
    return np.dstack((color, alpha))


def palette(a):
    sample = a[::3, ::3]
    return np.percentile(sample[sample[..., 3] > 240, :3], [15, 50, 85], axis=0)


def smooth(values, radius=6):
    values = np.asarray(values)
    return np.stack([np.mean(values[max(0, i-radius):i+radius+1], axis=0)
                     for i in range(len(values))])


def blend(a, b, t):
    # Premultiplied alpha avoids dark/green outlines in the short dissolve.
    af, bf = a.astype(np.float32)/255, b.astype(np.float32)/255
    alpha = af[..., 3:]*(1-t)+bf[..., 3:]*t
    rgb = (af[..., :3]*af[..., 3:]*(1-t)+bf[..., :3]*bf[..., 3:]*t)/np.maximum(alpha, 1/255)
    return np.uint8(np.clip(np.dstack((rgb, alpha))*255+.5, 0, 255))


def difference(a, b):
    aa = a[::5, ::5].astype(np.float32)/255
    bb = b[::5, ::5].astype(np.float32)/255
    union = np.maximum(aa[..., 3], bb[..., 3]) > .05
    return float(np.abs(aa[..., 3]-bb[..., 3])[union].mean())


def loop(frames, min_seconds=2):
    # Select a real forward-moving interval with the closest endpoint silhouettes.
    best = (float('inf'), 0, len(frames)-1)
    for i in range(min(24, len(frames))):
        for j in range(i+round(min_seconds*FPS), len(frames)):
            d = difference(frames[i], frames[j])
            if d < best[0]:
                best = (d, i, j)
    _, first, last = best
    selected = frames[first:last+1]
    target = selected[0]
    for n in range(6):
        selected[-6+n] = blend(selected[-6+n], target, (n+1)/6)
    return selected, {'firstFrame': first, 'lastFrame': last, 'endpointAlphaDifferenceBefore': best[0]}


def build(source, output):
    if output.exists() or not output.resolve().is_relative_to((ROOT/'build').resolve()):
        raise ValueError('Choose a new output inside build')
    output.mkdir(parents=True)
    ids = ['idle', 'heart', 'drag', 'release', 'sleep_enter', 'sleep', 'wake', 'feed']
    raw, boxes, palettes, hashes = {}, {}, {}, {}
    for aid in ids:
        path = source/(aid+'.mp4')
        hashes[aid] = hashlib.sha256(path.read_bytes()).hexdigest()
        cap = cv2.VideoCapture(str(path))
        native_fps = cap.get(cv2.CAP_PROP_FPS)
        frames, last_index, native_index = [], -1, 0
        while True:
            ok, bgr = cap.read()
            if not ok:
                break
            index = round(native_index*FPS/native_fps)
            native_index += 1
            if index == last_index:
                continue
            last_index = index
            a = key(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
            frames.append(a)
        cap.release()
        if len(frames) < FPS*4:
            raise ValueError('Video too short: '+aid)
        raw[aid] = frames
        boxes[aid] = np.array([bounds(a) for a in frames], dtype=float)
        palettes[aid] = smooth([palette(a) for a in frames])
        print('Decoded', aid, len(frames), flush=True)
    reference_palette = np.median(palettes['idle'], axis=0)
    scales = {}
    for aid in ids:
        b = boxes[aid]
        height = b[:, 3]-b[:, 1]
        baseline = np.median(height[:12] if aid in ['idle', 'heart', 'feed', 'sleep_enter'] else height[-12:])
        scales[aid] = HEIGHT/baseline
    scales['sleep'] = scales['sleep_enter']*np.median(boxes['sleep_enter'][-12:, 3]-boxes['sleep_enter'][-12:, 1])/np.median(boxes['sleep'][:, 3]-boxes['sleep'][:, 1])
    normalized, report = {}, {'fps': FPS, 'size': SIZE, 'standingHeight': HEIGHT, 'sourceHashes': hashes, 'actions': {}}
    for aid in ids:
        result = []
        b = boxes[aid]
        floor = np.median(b[:12, 3] if aid in ['idle', 'heart', 'feed', 'sleep_enter'] else b[-12:, 3])
        zoom_height = smooth(b[:, 3]-b[:, 1], 8)
        for i, a in enumerate(raw[aid]):
            scale = HEIGHT/zoom_height[i] if aid == 'drag' else scales[aid]
            curve = palettes[aid][i]
            adjusted = a.copy()
            for c in range(3):
                lut = np.interp(np.arange(256), [0, *curve[:, c], 255], [0, *reference_palette[:, c], 255])
                adjusted[..., c] = np.uint8(np.clip(lut, 0, 255))[a[..., c]]
            adjusted[a[..., 3] == 0] = 0
            local_floor = float(smooth(b[:, 3], 8)[i]) if aid == 'drag' else floor
            if aid == 'release':
                # Correct the source's initial camera offset over its landing interval.
                t = min(1, i/(FPS*.7))
                t = t*t*(3-2*t)
                local_floor = b[0, 3]*(1-t)+floor*t
            matrix = np.array([[scale, 0, ANCHOR[0]-640*scale], [0, scale, ANCHOR[1]-local_floor*scale]], np.float32)
            # Transform premultiplied colors with the same alpha interpolation.
            af = adjusted.astype(np.float32)/255
            af[..., :3] *= af[..., 3:]
            moved = cv2.warpAffine(af, matrix, SIZE, flags=cv2.INTER_CUBIC)
            moved[..., :3] /= np.maximum(moved[..., 3:], 1/255)
            frame = np.uint8(np.clip(moved*255+.5, 0, 255))
            # Interpolation/curves can revive green in partially keyed contour pixels.
            frame[..., 1] = np.minimum(frame[..., 1], np.maximum(frame[..., 0], frame[..., 2]))
            frame[frame[..., 3] == 0] = 0
            box = bounds(frame)
            if min(box[:2]) < 2 or box[2] > SIZE[0]-2 or box[3] > SIZE[1]-2:
                raise ValueError(f'Clipped {aid}/{i}: {box}')
            result.append(frame)
        normalized[aid] = result
        raw[aid] = []
        report['actions'][aid] = {'decodedFrames': len(result), 'scale': scales[aid]}
        print('Normalized', aid, flush=True)
    normalized['idle'], report['idleLoop'] = loop(normalized['idle'])
    # The drag clip's first second contains unwanted zoom; stable suspension follows it.
    normalized['drag'], report['dragLoop'] = loop(normalized['drag'][FPS:])
    normalized['sleep'], report['sleepLoop'] = loop(normalized['sleep'])
    stand, seated = normalized['idle'][0], normalized['sleep'][0]
    for aid, start, end in [('heart', stand, stand), ('feed', stand, stand),
                            ('sleep_enter', stand, seated), ('wake', seated, stand),
                            ('release', normalized['drag'][0], stand)]:
        frames = normalized[aid]
        for i in range(6):
            frames[i] = blend(start, frames[i], i/5)
            frames[-6+i] = blend(frames[-6+i], end, (i+1)/6)
    config = json.loads((ROOT/'workflow/gpt-dragon/human-jobs.json').read_text(encoding='utf-8-sig'))
    existing = json.loads((ROOT/'characters/gpt-dragon/character.json').read_text(encoding='utf-8-sig'))
    human = dict(existing['forms']['human'])
    human.update(canvasWidth=SIZE[0]//2, canvasHeight=SIZE[1]//2, anchorX=ANCHOR[0]//2,
                 anchorY=ANCHOR[1]//2, packVersion='2.0.0', actions=copy.deepcopy(config['actions']))
    for action in human['actions']:
        aid = action['id']
        action['frames'] = []
        dest = output/'animations/human'/aid
        dest.mkdir(parents=True)
        for i, a in enumerate(normalized[aid]):
            path = f'animations/human/{aid}/{i:04d}.png'
            Image.fromarray(a).save(output/path)
            action['frames'].append({'path': path, 'durationMs': round((i+1)*1000/FPS)-round(i*1000/FPS)})
        report['actions'][aid].update(frames=len(action['frames']), durationMs=sum(f['durationMs'] for f in action['frames']))
    existing.update({k: v for k, v in human.items() if k != 'forms'})
    existing['forms']['human'] = human
    chibi = existing['forms']['chibi']
    chibi_map = {a['id']: a for a in chibi['actions']}
    # Forms require equal action IDs. These are existing-pose compatibility
    # mappings, not newly generated chibi video animations.
    for aid, pose, trigger, next_id in [('release', 'idle', 'drag_release', 'idle'),
                                      ('sleep_enter', 'sleep', 'idle_timeout', 'sleep')]:
        if aid in chibi_map:
            continue
        fallback = copy.deepcopy(chibi_map[pose])
        fallback.update(id=aid, displayName='放下' if aid=='release' else '入睡',
                        loop=False, triggers=[trigger], nextActionId=next_id,
                        showSleepIndicator=False, frames=copy.deepcopy(chibi_map[pose]['frames'][:1]))
        fallback['frames'][0]['durationMs'] = 250
        chibi['actions'].append(fallback)
    chibi_map['sleep']['triggers'] = []
    # Chibi assets stay byte-identical; copy only its referenced poses.
    import shutil
    for action in existing['forms']['chibi']['actions']:
        for frame in action['frames']:
            p = frame['path']
            target = output/p
            if not target.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(ROOT/'characters/gpt-dragon'/p, target)
    existing['name'] = 'GPT 龙娘'
    (output/'character.json').write_text(json.dumps(existing, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    sheet = Image.new('RGB', (1200, 8*270), '#33333b')
    draw = ImageDraw.Draw(sheet)
    for row, aid in enumerate(ids):
        frames = normalized[aid]
        for col, idx in enumerate(np.linspace(0, len(frames)-1, 6).astype(int)):
            im = Image.fromarray(frames[idx]); bg = Image.new('RGBA', SIZE, '#33333b' if col%2 == 0 else '#eeeeee')
            bg.alpha_composite(im); bg.thumbnail((195, 245))
            sheet.paste(bg.convert('RGB'), (col*200, row*270+22))
            draw.text((col*200+3, row*270+5), f'{aid} {idx/FPS:.2f}s', fill='white')
    sheet.save(output/'review.jpg')
    # A continuous movie shows the actual frame sequence and action joins.
    sequence = ['idle', 'heart', 'idle', 'drag', 'release', 'idle', 'feed', 'sleep_enter', 'sleep', 'wake', 'idle']
    writer = cv2.VideoWriter(str(output/'review.mp4'), cv2.VideoWriter_fourcc(*'mp4v'), FPS, (450, 500))
    for aid in sequence:
        for a in normalized[aid]:
            small = cv2.resize(a, (450, 500), interpolation=cv2.INTER_AREA).astype(np.float32)
            alpha = small[..., 3:]/255
            rgb = np.uint8(small[..., :3]*alpha+np.array([45, 45, 53])*(1-alpha))
            writer.write(cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))
    writer.release()
    for aid in ids:
        assert hashlib.sha256((source/(aid+'.mp4')).read_bytes()).hexdigest() == hashes[aid]
    report.update(status='mechanical_checks_passed', visualAcceptance='pending', sourceIntegrity='verified_unchanged',
                  correction='smoothed foreground RGB quantile curves; fixed scale except drag zoom correction; six-frame premultiplied endpoint dissolves')
    (output/'report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(output, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    build(args.source.resolve(), args.output.resolve())
