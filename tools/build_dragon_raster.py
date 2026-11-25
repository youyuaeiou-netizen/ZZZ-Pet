"""Package the original artwork as lossless PNG; no generative redraw or upscaling."""
from pathlib import Path
import json, subprocess, shutil
from PIL import Image
from build_dragon_art import definition, eyes, heart, pastry

ROOT=Path(__file__).resolve().parents[1]
CACHE=ROOT/'build/.cache/dragon-art'
PACK=ROOT/'characters/gpt-dragon'
DEST=Path('C:/Users/h/Desktop/素材/gpt/龙娘桌宠/原图版')
NODE=Path('C:/Users/h/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe')

def padded(image):
    result=Image.new('RGBA',(image.width+40,image.height+40))
    result.alpha_composite(image,(20,20))
    return result

jobs=[]
for form in ['human','chibi']:
    out=PACK/'poses'/form; out.mkdir(parents=True,exist_ok=True)
    original=Image.open(CACHE/(form+'.png')).convert('RGBA')
    base=padded(original); base.save(out/'idle.png')
    for name,markup in [('blink',eyes(form)),('sleep',eyes(form)),('drag',heart(110,160,26)),
                        ('feed',pastry(form)),('heart',heart(95,300,65))]:
        if form=='human' and name=='heart':
            pose=Image.open(CACHE/'human-heart.png').convert('RGBA')
            # The source portrait crops the wings; preserve the reference instead of inventing anatomy.
            pose.thumbnail((original.width,original.height),Image.Resampling.LANCZOS)
            result=Image.new('RGBA',base.size)
            result.alpha_composite(pose,((base.width-pose.width)//2,1183-pose.height))
            result.save(out/'heart.png')
            continue
        svg=f'<svg xmlns="http://www.w3.org/2000/svg" width="{base.width}" height="{base.height}"><g transform="translate(20 20)">{markup}</g></svg>'
        jobs.append(dict(base=str(out/'idle.png'),svg=svg,target=str(out/(name+'.png'))))
    # Retired vector poses are retained in the user's artwork directory, outside the runtime pack.
    for name in ['idle','blink','sleep','drag','heart','feed']:
        old=out/(name+'.svg')
        if old.exists(): old.unlink()

jobfile=CACHE/'raster-jobs.json'; jobfile.write_text(json.dumps(jobs),encoding='utf-8')
subprocess.run([str(NODE),str(ROOT/'tools/rasterize_dragon_overlays.cjs'),str(jobfile)],check=True)
human=definition('human'); chibi=definition('chibi')
for form,manifest in [('human',human),('chibi',chibi)]:
    for action in manifest['actions']:
        for frame in action['frames']: frame['path']=frame['path'].replace('.svg','.png')
    # At 100% scale, alpha-visible heights are 408 DIP / 204 DIP respectively.
    if form=='human': manifest.update(canvasWidth=252,canvasHeight=434,anchorX=126,anchorY=420)
    else: manifest.update(canvasWidth=152,canvasHeight=232,anchorX=76,anchorY=223)
manifest=dict(human,forms=dict(human=human,chibi=chibi),packVersion='1.1.0')
(PACK/'character.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
DEST.mkdir(parents=True,exist_ok=True)
for form in ['human','chibi']:
    shutil.copy2(PACK/'poses'/form/'idle.png',DEST/(form+'.png'))
(DEST/'素材说明.md').write_text('''# GPT 龙娘原图版

使用原画正面裁切及透明背景 PNG，保留原分辨率和配色，不进行 AI 重绘。三张原始参考图未修改，SVG 试验稿保留在上级目录。

人物版可见高度约 408 DIP，Q 版约 204 DIP，即当前艾莲布的两倍与相同高度；菜单缩放独立生效。大幅放大位图仍会软化。

动作采用呼吸变换、闭眼覆盖、拖拽倾斜、原画坐姿比心及进食图标；Q 版点击显示爱心。第三张参考的翅膀在画面边缘被截断，保留其原始构图。
''',encoding='utf-8')
print('Original PNG pack ready:',PACK)
