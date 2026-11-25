from pathlib import Path
import sys, json, hashlib, shutil
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path('C:/pet/ZZZ-Pet')
sys.path.insert(0, str(ROOT / 'tools'))
import build_flat2d_pack as build

RUN = ROOT / 'build/generated/idle-update-20261017'
RUN.mkdir(exist_ok=False)
PACK = RUN / 'packs/gpt-dragon-chibi-v4'
shutil.copytree(ROOT / 'characters/gpt-dragon-chibi-v4', PACK)
source = Path('C:/Users/h/Desktop/视频/Q版/待机.mp4')
digest = hashlib.sha256(source.read_bytes()).hexdigest()
runtime = Path('C:/Users/h/AppData/Local/Codex/local-mp4-analysis/ffmpeg/bin')
cache = RUN / 'decoded'; cache.mkdir()
paths, boxes, meta = build.decode(dict(id='idle',input=source.name,mode='green',start=0,end=5), source.parent, cache, str(runtime/'ffmpeg.exe'), str(runtime/'ffprobe.exe'))
cfg = json.loads((ROOT/'workflow/gpt-dragon/jimeng-v4-20261016/video-jobs-v4.json').read_text(encoding='utf-8'))
frames, union = build.normalize(paths, cfg['alignment'], PACK/'animations/idle-20261017', cfg['layout'])
assert len(frames) == 60
# Preserve every original motion frame in forward order. Reduce the long
# fully closed hold, retaining the natural closing and reopening transitions.
for index, f in enumerate(frames):
    if 20 <= index <= 34:
        f['durationMs'] = 35
manifest = json.loads((PACK/'character.json').read_text(encoding='utf-8'))
manifest['packVersion'] = '1.0.3'
idle = next(a for a in manifest['actions'] if a['id']=='idle')
idle['frames'] = frames
(PACK/'character.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
preview=[]
for f in frames:
    im=Image.open(PACK/f['path']).convert('RGBA')
    assert im.size==(640,576) and im.getchannel('A').getextrema()==(0,255)
    board=Image.new('RGB',(640,288))
    sprite=im.resize((320,288),Image.Resampling.LANCZOS)
    for x,color in [(0,'#242430'),(320,'#f3f0eb')]:
        bg=Image.new('RGBA',(320,288),color); bg.alpha_composite(sprite)
        board.paste(bg.convert('RGB'),(x,0))
    preview.append(board)
preview[0].save(RUN/'待机-新视频-深浅背景.gif',save_all=True,append_images=preview[1:],duration=[f['durationMs'] for f in frames],loop=0,disposal=2)
sheet=Image.new('RGB',(1280,864),'#242430')
for k,i in enumerate([0,10,18,19,20,25,34,35,36,40,50,59]):
    im=preview[i].crop((0,0,320,288));ImageDraw.Draw(im).text((5,5),f'Frame {i}',fill='white');sheet.paste(im,((k%4)*320,(k//4)*288))
sheet.save(RUN/'review.png')
assert hashlib.sha256(source.read_bytes()).hexdigest()==digest
report=dict(source=str(source),sourceSha256=digest,sourceIntegrity='verified_unchanged',frames=len(frames),durationMs=sum(f['durationMs'] for f in frames),closedHoldMs=525,method='all 60 forward frames retained; shortened fully closed-eye interval only by duration; no redraw, reverse playback or eye patch',bounds=union,packVersion=manifest['packVersion'],status='candidate')
(RUN/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
