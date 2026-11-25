"""Promote the reviewed v4 video pack into the independent desktop character."""
from pathlib import Path
import json
import shutil
from PIL import Image

ROOT = Path('C:/pet/ZZZ-Pet')
SOURCE = ROOT / 'build/generated/jimeng-v4-20261016/packs/gpt-dragon-v4-preview'
DEST = ROOT / 'characters/gpt-dragon-chibi-v4'
if DEST.exists():
    raise FileExistsError('Refusing to overwrite an existing character')
manifest = json.loads((SOURCE / 'character.json').read_text(encoding='utf-8'))
manifest.update(id='gpt-dragon-chibi-v4', name='GPT 龙娘 · Q版', packVersion='1.0.0')
manifest.pop('source', None)
for action in manifest['actions']:
    action['category'] = '龙娘互动'
    for frame in action['frames']:
        target = DEST / frame['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        # A single fixed translation for each clip aligns its resting foot line;
        # never scale/reanchor individual poses during their motion.
        dy = {'idle': 0, 'heart': -2, 'drag': -2, 'release': -7}[action['id']]
        with Image.open(SOURCE / frame['path']) as image:
            canvas = Image.new('RGBA', image.size)
            canvas.paste(image, (0, dy))
            canvas.save(target)
actions = {a['id']: a for a in manifest['actions']}
air = []
for n, index in enumerate([18, 20, 22, 24, 26, 28]):
    relative = f'animations/release/air{n:04d}.png'
    shutil.copyfile(DEST / actions['drag']['frames'][index]['path'], DEST / relative)
    air.append(dict(path=relative, durationMs=42))
actions['release']['frames'] = air + actions['release']['frames']
(DEST / 'character.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
report = dict(characterId=manifest['id'], frameCount=sum(len(a['frames']) for a in manifest['actions']), fps=12,
              sourceVideos='C:/Users/h/Desktop/视频/Q版', releaseTrim=[0.25, 5], airFrames=6,
              airFrameSource='drag frames 18,20,22,24,26,28; used only during physical descent',
              fixedClipTranslationY={'idle':0,'heart':-2,'drag':-2,'release':-7},
              productionDeployment='pending')
(ROOT / 'workflow/gpt-dragon/jimeng-v4-20261016/production-build.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False))
