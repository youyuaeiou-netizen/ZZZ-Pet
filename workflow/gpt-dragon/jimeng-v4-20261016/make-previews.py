"""Preview the new source-derived pack; never alter source videos."""
from pathlib import Path
import hashlib
import json
import subprocess
from PIL import Image, ImageDraw, ImageFont

ROOT = Path('C:/pet/ZZZ-Pet')
RUN = ROOT / 'build/generated/jimeng-v4-20261016'
PACK = RUN / 'packs/gpt-dragon-v4-preview'
OUT = RUN / 'previews'
FFMPEG = 'C:/Users/h/AppData/Local/Codex/local-mp4-analysis/ffmpeg/bin/ffmpeg.exe'
OUT.mkdir(exist_ok=True)
manifest = json.loads((PACK / 'character.json').read_text(encoding='utf-8'))
font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 16)
ids = ['idle', 'heart', 'drag', 'release']
labels = ['待机·摆尾眨眼', '比心·爱心飞出', '悬空·惊慌', '落地·举手欢呼']
actions = {a['id']: a for a in manifest['actions']}
expanded = {}
checks = []
for aid in ids:
    paths = []
    for frame in actions[aid]['frames']:
        path = PACK / frame['path']
        with Image.open(path) as image:
            assert image.mode == 'RGBA' and image.size == (640, 576), path
            alpha = image.getchannel('A')
            assert alpha.getextrema() == (0, 255), path
            box = alpha.point(lambda a: 255 if a > 12 else 0).getbbox()
            assert box and box[0] > 1 and box[1] > 1 and box[2] < 639 and box[3] < 575, path
        paths.append(path)
    expanded[aid] = paths
    checks.append(dict(id=aid, frames=len(paths), durationMs=sum(f['durationMs'] for f in actions[aid]['frames'])))

board_dir = OUT / 'frames'
board_dir.mkdir(exist_ok=True)
gifs = {aid: [] for aid in ids}
for index in range(60):
    board = Image.new('RGB', (1280, 640), '#20202b')
    draw = ImageDraw.Draw(board)
    for col, aid in enumerate(ids):
        sprite = Image.open(expanded[aid][min(index, len(expanded[aid])-1)]).convert('RGBA').resize((320,288), Image.Resampling.LANCZOS)
        draw.text((col*320+12, 8), labels[col], fill='white', font=font)
        for row, color in enumerate(['#20202b', '#f4f1ed']):
            bg = Image.new('RGBA',(320,288),color)
            bg.alpha_composite(sprite)
            board.paste(bg.convert('RGB'),(col*320,32+row*304))
        single=Image.new('RGB',(320,288),'#30303b')
        single.paste(sprite,mask=sprite.getchannel('A'))
        if index<len(expanded[aid]): gifs[aid].append(single)
    board.save(board_dir/f'{index:03d}.png')
for aid,frames in gifs.items():
    frames[0].save(OUT/(aid+'.gif'),save_all=True,append_images=frames[1:],duration=83,loop=0,disposal=2)
subprocess.run([FFMPEG,'-hide_banner','-loglevel','error','-nostdin','-framerate','12','-i',str(board_dir/'%03d.png'),'-an','-c:v','libx264','-pix_fmt','yuv420p','-crf','18',str(OUT/'四动作-深浅背景.mp4')],check=True)
Image.open(board_dir/'030.png').save(OUT/'四动作-深浅背景.jpg')
before = json.loads((RUN/'qa/source-metadata.json').read_text(encoding='utf-8'))
for source in before:
    assert hashlib.sha256(Path(source['path']).read_bytes()).hexdigest()==source['sha256'], source['path']
summary=dict(totalFrames=sum(a['frames'] for a in checks),pngMode='RGBA',pngSize=[640,576],fps=12,actions=checks,sourceIntegrity='verified_unchanged',mechanicalChecks='passed',visualReview='agent reviewed; user acceptance pending',releaseSourceTrim=[0.25,5],sleepActionsIncluded=False,productionDeployed=False)
(RUN/'qa/pack-validation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False))
