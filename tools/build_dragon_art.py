"""Reproducible vector fitting of the user's artwork; originals are never overwritten.

Development-only dependencies: Pillow, numpy, vtracer (C:/pet/dev-tools/vector-art).
Runtime contains only SVG paths, WPF vector transforms and the character manifest.
"""
from pathlib import Path
from collections import deque
import argparse, json, math, sys, xml.etree.ElementTree as ET
sys.path.insert(0, 'C:/pet/dev-tools/vector-art')
from PIL import Image
import numpy as np
import vtracer

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path('C:/Users/h/Desktop/素材/gpt')
DEST = SOURCE / '龙娘桌宠'
PACK = ROOT / 'characters/gpt-dragon'
CACHE = ROOT / 'build/.cache/dragon-art'
SVG = 'http://www.w3.org/2000/svg'
ET.register_namespace('', SVG)

def fit(name, filename, box, tolerance=12):
    original = Image.open(SOURCE / filename).convert('RGB')
    box = (box[0], box[1], min(box[2],original.width), min(box[3],original.height))
    image = original.crop(box)
    pixels = np.array(image)
    # Flood only edge-connected background. Enclosed white hair/dress are preserved.
    bg = pixels[0, 0].astype(int)
    eligible = np.max(np.abs(pixels.astype(int) - bg), axis=2) <= tolerance
    if name=='human-heart':
        eligible |= np.min(pixels,axis=2)>225
    h, w = eligible.shape
    removed = np.zeros((h,w), dtype=bool)
    queue = deque()
    for x in range(w):
        for y in (0,h-1):
            if eligible[y,x]: removed[y,x] = True; queue.append((x,y))
    for y in range(h):
        for x in (0,w-1):
            if eligible[y,x] and not removed[y,x]: removed[y,x]=True; queue.append((x,y))
    while queue:
        x,y = queue.popleft()
        for nx,ny in ((x-1,y),(x+1,y),(x,y-1),(x,y+1)):
            if 0<=nx<w and 0<=ny<h and eligible[ny,nx] and not removed[ny,nx]:
                removed[ny,nx]=True; queue.append((nx,ny))
    rgba = np.concatenate((pixels, np.where(removed,0,255).astype('uint8')[...,None]),axis=2)
    if name=='human-heart':
        # Remove the reference sheet's border and floor, outside the actual figure.
        # Its edge-cropped wing membranes are extended below using native SVG paths.
        rgba[:650,:20,3]=0
        rgba[:710,-20:,3]=0
        rgba[-12:,:,3]=0
    path = CACHE / (name+'.png')
    Image.fromarray(rgba).save(path)
    target = DEST / (name+'.svg')
    vtracer.convert_image_to_svg_py(str(path), str(target), colormode='color', hierarchical='stacked',
        mode='spline', filter_speckle=2, color_precision=7, layer_difference=4,
        corner_threshold=60, length_threshold=2, max_iterations=10, splice_threshold=45, path_precision=2)
    tree=ET.parse(target); root=tree.getroot()
    # Fixed transparent padding keeps breathing/drag transforms inside the viewport.
    root.set('viewBox', f'-20 -20 {w+40} {h+40}')
    root.set('width', str(w+40)); root.set('height', str(h+40))
    title=ET.Element(f'{{{SVG}}}title'); title.text='GPT 龙娘 / '+name+' / 真实矢量路径'
    root.insert(0,title)
    rect=ET.Element(f'{{{SVG}}}rect',dict(x='-20',y='-20',width=str(w+40),height=str(h+40),fill='#ffffff',opacity='0'))
    root.insert(1,rect)
    if name=='human-heart':
        # Complete the tips cut off by the portrait framing; match lavender membranes.
        wing=ET.fromstring(f'<g xmlns="{SVG}" fill="#cec4df" stroke="#72687f" stroke-width="2"><path d="M21 650Q-65 703 -78 818Q-47 783 20 827L22 771Q-4 754 -29 768Q-23 713 21 689Z"/><path d="M821 705Q890 750 925 908Q884 868 820 899L820 856Q850 842 877 858Q863 786 821 747Z"/></g>')
        root.insert(2,wing)
        root.set('viewBox',f'-90 -20 {w+180} {h+40}')
        rect.set('x','-90'); rect.set('width',str(w+180))
        root.set('width',str(w+180))
    tree.write(target,encoding='utf-8',xml_declaration=True)
    print(name, image.size, 'paths', len(root.findall(f'.//{{{SVG}}}path')), 'bytes', target.stat().st_size)
    return target

def overlay(base, target, markup):
    tree=ET.parse(base)
    group=ET.fromstring(f'<g xmlns="{SVG}">{markup}</g>')
    tree.getroot().append(group)
    tree.write(target,encoding='utf-8',xml_declaration=True)

def eyes(form, wink=False):
    if form=='human': centers=[(331-15,175-8,16,10),(389-15,175-8,16,10)]
    else: centers=[(286,419-15,39,27),(416,419-15,39,27)]
    result=''
    for i,(x,y,rx,ry) in enumerate(centers):
        if wink and i==0: continue
        result+=f'<path d="M{x-rx} {y-ry} Q{x} {y-ry-8} {x+rx} {y-ry} L{x+rx} {y+ry} Q{x} {y+ry+5} {x-rx} {y+ry}Z" fill="#fbeef2"/>'
        result+=f'<path d="M{x-rx+2} {y+2} Q{x} {y+ry*.55} {x+rx-2} {y+2}" fill="none" stroke="#69495f" stroke-width="{2 if form=="human" else 4}" stroke-linecap="round"/>'
    return result

def heart(x,y,size=45):
    return f'<g transform="translate({x} {y}) scale({size/100})"><path d="M0 27C-45 -12 -72 45 0 91C72 45 45 -12 0 27Z" fill="#e9a8c4" stroke="#bd7b9c" stroke-width="3"/><path d="M-29 26Q-37 35 -24 44" fill="none" stroke="#fff3fa" stroke-width="7" stroke-linecap="round"/></g>'

def pastry(form):
    x,y=(365-15,435-8) if form=='human' else (365,670-15)
    return f'<g transform="translate({x} {y})"><path d="M-30 0L-24 35Q0 46 24 35L30 0Z" fill="#eed4ad" stroke="#ac83a1" stroke-width="2"/><path d="M-33 0Q-30 -15 -15 -17Q-22 -33 0 -39Q22 -33 16 -17Q31 -15 33 0Z" fill="#faf0fc" stroke="#ac83a1" stroke-width="2"/><circle cx="0" cy="-40" r="6" fill="#d291b3"/><path d="M-15 8L-12 29M0 9V32M15 8L12 29" stroke="#d2b58d" stroke-width="2"/></g>'

def frames(path, seconds, count=12, rotation=0, blink=None):
    result=[]
    for i in range(count):
        t=i/count*math.tau
        result.append(dict(path=blink if blink and i==count-2 else path,
            durationMs=round(seconds*1000/count), scaleX=round(1+.003*math.sin(t),5),
            scaleY=round(1+.005*math.sin(t),5),rotation=round(rotation*math.sin(t),3)))
    return result

def definition(form):
    prefix=f'poses/{form}'
    idle=prefix+'/idle.svg'; sleep=prefix+'/sleep.svg'
    common=['click','double_click','drag_start','idle_timeout','notification']
    def action(id,label,fs,triggers=(),loop=False,**extra):
        return dict(id=id,displayName=label,frames=fs,triggers=list(triggers),loop=loop,
            interruptibleBy=common,priority=1,**extra)
    actions=[
        action('idle','待机呼吸',frames(idle,4,24,blink=prefix+'/blink.svg'),['app_start','drag_release'],True),
        action('drag','拖拽反馈',frames(prefix+'/drag.svg',1.2,12,2),['drag_start'],True),
        action('sleep','睡眠',frames(sleep,4,16),['idle_timeout'],True,showSleepIndicator=True),
        action('wake','唤醒',[dict(path=sleep,durationMs=200),dict(path=prefix+'/blink.svg',durationMs=160),dict(path=idle,durationMs=400)],['notification'],nextActionId='idle'),
        action('heart','比心回应',frames(prefix+'/heart.svg',2.4,18,.8),['click','double_click'],nextActionId='idle'),
        action('feed','进食',frames(prefix+'/feed.svg',2,16,.5),nextActionId='idle'),
    ]
    # Identical logical canvas makes form changes preserve the same grounded position.
    # Aspect ratios are kept by transparent padding in SVG viewBox.
    return dict(schemaVersion=1,id='gpt-dragon',name='GPT 龙娘',minimumAppVersion='0.3.1',
        immediateClick=True,defaultActionId='idle',defaultFacing='right',defaultScale=1,
        canvasWidth=218 if form=='human' else 238,canvasHeight=380 if form=='human' else 350,
        anchorX=109 if form=='human' else 119,anchorY=374 if form=='human' else 344,
        randomIntervalMs=12000,sleepThresholdMs=60000,wakeActionId='wake',actions=actions)

def animate():
    for form in ['human','chibi']:
        output=PACK/'poses'/form; output.mkdir(parents=True,exist_ok=True)
        base=DEST/(form+'.svg')
        (output/'idle.svg').write_bytes(base.read_bytes())
        overlay(base,output/'blink.svg',eyes(form))
        overlay(base,output/'sleep.svg',eyes(form))
        overlay(base,output/'drag.svg',heart(110,160,26))
        overlay(base,output/'feed.svg',pastry(form))
        if form=='human':
            (output/'heart.svg').write_bytes((DEST/'human-heart.svg').read_bytes())
        else:
            overlay(base,output/'heart.svg',eyes(form,True)+heart(95,300,65))
    human=definition('human'); chibi=definition('chibi')
    manifest=dict(human,forms=dict(human=human,chibi=chibi),packVersion='1.0.0')
    (PACK/'character.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    (DEST/'素材说明.md').write_text('''# GPT 龙娘矢量素材

原始三张 PNG 未修改。本素材依据原画进行轮廓与色块的曲线拟合，再补绘动作表情，全部 SVG 为矢量路径，无嵌入位图。放大没有像素块，但原画分辨率限制了可还原的细节，并非人工逐笔重绘。

人物版、Q 版各包含待机呼吸与眨眼、拖拽反馈、闭眼睡眠、睁眼唤醒、点击回应和进食。人物版点击采用原画第三张的坐姿比心；Q 版采用眨眼与爱心回应。动作是轻量的矢量姿势及变换，不是完整骨骼动画。

人物版默认约 380 DIP 高，Q 版约 350 DIP 高。右键 → 角色可切换 GPT 龙娘与艾莲布；右键 → GPT 龙娘可喂食及选择形态。形态和缩放独立。
''',encoding='utf-8')

if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('--animate',action='store_true'); args=parser.parse_args()
    for path in [DEST,PACK,CACHE]: path.mkdir(parents=True,exist_ok=True)
    if args.animate: animate()
    else:
        fit('human','codex-clipboard-0bafe644-04e7-4a1d-826a-5cbb63ed0a66.png',(15,8,685,1190))
        fit('chibi','codex-clipboard-587819ba-23c8-4ae5-9c9e-e455d45cf222.png',(0,15,717,1130))
        fit('human-heart','codex-clipboard-b540513f-12be-42e2-b1e5-402a694c3afc.png',(0,0,842,1214),20)
