"""Add supplied stretch/fire/humming to the existing merged dragon, preserving old frames."""
from pathlib import Path
import copy, hashlib, json, shutil, sys
import cv2
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path('C:/pet/ZZZ-Pet')
sys.path.insert(0, str(ROOT/'tools'))
from build_flat2d_pack import key_green, bbox
from build_dragon_human_video import blend

RUN = ROOT/'build/generated/actions-update-20261020'
PACK = RUN/'packs/gpt-dragon'
SOURCE = Path('C:/Users/h/Desktop/视频/Q版')
FPS = 24
SIZE = (640, 576)

def digest(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def palette(a):
    c = a[..., :3].astype(float)
    mask = (a[..., 3] > 240) & (c[..., 2] >= c[..., 0]-5) & (c.min(2) > 100)
    return np.percentile(c[mask], [10, 50, 90], axis=0)

def smooth(v, r=4):
    return np.array([np.mean(v[max(0,i-r):i+r+1],axis=0) for i in range(len(v))])

def decode(name, seconds):
    c=cv2.VideoCapture(str(SOURCE/(name+'.mp4')))
    native=c.get(cv2.CAP_PROP_FPS)
    i=0; target=0
    while True:
        ok,b=c.read()
        if not ok: break
        if i/native+.5/native >= target/FPS and target < seconds*FPS:
            yield key_green(cv2.cvtColor(b,cv2.COLOR_BGR2RGBA))
            target+=1
        i+=1
    c.release()
    assert target == seconds*FPS, (name,target)

def prepare(name, seconds, ref):
    boxes=[]; colors=[]
    for a in decode(name,seconds):
        boxes.append(bbox(a));colors.append(palette(a))
    boxes=np.array(boxes);curves=smooth(colors)
    first=boxes[0]
    scale=408/(first[3]-first[1])
    dx=105-first[0]*scale;dy=521-first[3]*scale
    left=float(np.min(boxes[:,0]*scale+dx));right=float(np.max(boxes[:,2]*scale+dx))
    # Enlarge only the fire viewport if needed; keep the same physical anchor/scale.
    pad=max(0,int(np.ceil(max(3-left,right-637)/2))*2)
    size=(640+2*pad,576)
    result=[]
    for i,a in enumerate(decode(name,seconds)):
        lut=np.empty((256,3),np.uint8)
        for ch in range(3):
            lut[:,ch]=np.clip(np.interp(np.arange(256),[0,*curves[i,:,ch],255],[0,*ref[:,ch],255]),0,255)
        adjusted=a.copy()
        for ch in range(3): adjusted[...,ch]=lut[a[...,ch],ch]
        f=adjusted.astype(np.float32)/255;f[...,:3]*=f[...,3:]
        matrix=np.array([[scale,0,dx+pad],[0,scale,dy]],np.float32)
        f=cv2.warpAffine(f,matrix,size,flags=cv2.INTER_CUBIC)
        f[...,:3]/=np.maximum(f[...,3:],1/255)
        out=np.uint8(np.clip(f*255+.5,0,255))
        edge=out[...,3]<250
        out[...,1][edge]=np.minimum(out[...,1],np.maximum(out[...,0],out[...,2]))[edge]
        out[out[...,3]==0]=0
        b=bbox(out)
        assert b[0]>=2 and b[1]>=2 and b[2]<=size[0]-2 and b[3]<=574,(name,i,b,size)
        result.append(out)
    return result,dict(scale=scale,offset=[dx+pad,dy],paddingPx=pad,size=list(size),sourceBoundsUnion=[int(boxes[:,0].min()),int(boxes[:,1].min()),int(boxes[:,2].max()),int(boxes[:,3].max())])

def start(frames,target,n=6):
    for i in range(n): frames[i]=blend(target,frames[i],i/(n-1))
    frames[0]=target.copy()

def end(frames,target,n=6):
    for i in range(n): frames[-n+i]=blend(frames[-n+i],target,(i+1)/n)
    frames[-1]=target.copy()

def main():
    if PACK.exists():raise FileExistsError(PACK)
    shutil.copytree(ROOT/'characters/gpt-dragon',PACK)
    m=json.loads((PACK/'character.json').read_text(encoding='utf-8-sig'))
    original=copy.deepcopy(m)
    chibi=m['forms']['chibi']
    idle=next(a for a in chibi['actions'] if a['id']=='idle')
    stand=np.array(Image.open(PACK/idle['frames'][0]['path']).convert('RGBA'))
    ref=np.median([palette(np.array(Image.open(PACK/f['path']).convert('RGBA'))) for f in idle['frames'][:6]],axis=0)
    report=dict(status='candidate',sources={},actions={},fps=FPS,sourceAudio='not imported; supplied audio remains in original MP4',paletteReference='existing chibi idle first six frames')
    clips={};meta={}
    for aid,name,seconds in [('stretch','懒腰',5),('fire','喷火',5),('humming','哼歌',15)]:
        p=SOURCE/(name+'.mp4');report['sources'][aid]=dict(path=str(p),sha256=digest(p))
        clips[aid],meta[aid]=prepare(name,seconds,ref)
        print('Prepared',aid,meta[aid],flush=True)
    raw=clips.pop('humming')
    # Search forward motion with similar silhouettes, colors, notes, and local velocity.
    small=[]
    for a in raw:
        f=cv2.resize(a,(80,72),interpolation=cv2.INTER_AREA).astype(np.float32)/255
        f[...,:3]*=f[...,3:];small.append(f)
    best=(1e9,0,0)
    for a in range(72,109):
        for b in range(216,277):
            if b-a<108:continue
            score=float(np.mean(np.abs(small[a]-small[b]))+.4*np.mean(np.abs((small[a+1]-small[a])-(small[b+1]-small[b]))))
            if score<best[0]:best=(score,a,b)
    score,a,b=best
    print('Loop source',a/FPS,b/FPS,'score',score,flush=True)
    clips['sing_enter']=raw[:a]
    clips['sing_loop']=raw[a:b]
    clips['sing_exit']=raw[b:]
    # Small endpoint corrections only; do not reverse the entire clip.
    end(clips['sing_enter'],clips['sing_loop'][0])
    end(clips['sing_loop'],clips['sing_loop'][0],6)
    start(clips['sing_exit'],clips['sing_loop'][0])
    start(clips['sing_enter'],stand)
    end(clips['sing_exit'],stand)
    report['hummingLoop']=dict(sourceSeconds=[a/FPS,b/FPS],endpointSearchScore=score,seamBlendMs=250,forwardOnly=True)
    for aid in ['stretch','fire']:
        pad=meta[aid]['paddingPx']
        target=np.pad(stand,((0,0),(pad,pad),(0,0))) if pad else stand
        start(clips[aid],target);end(clips[aid],target)
    specs=[('stretch','伸懒腰',False,'idle'),('fire','喷火',False,'idle'),('sing_enter','悠闲哼歌',False,'sing_loop'),('sing_loop',None,True,None),('sing_exit','结束哼歌',False,'idle')]
    for aid,label,loop,nxt in specs:
        action=dict(id=aid,displayName=label,category='龙娘互动',loop=loop,priority=10,triggers=[],weight=1,cooldownMs=0,interruptibleBy=['click','double_click','drag_start','notification'],frames=[])
        if nxt:action['nextActionId']=nxt
        if aid=='sing_loop':action['exitActionId']='sing_exit'
        pad=meta.get(aid,meta['humming'])['paddingPx']
        if pad:action.update(viewportWidth=320+pad,viewportHeight=288,viewportAnchorX=130+pad//2,viewportAnchorY=260)
        for i,f in enumerate(clips[aid]):
            rel=f'animations/chibi/{aid}-20261020/{i:04d}.png'
            p=PACK/rel;p.parent.mkdir(parents=True,exist_ok=True);Image.fromarray(f).save(p)
            action['frames'].append(dict(path=rel,durationMs=round((i+1)*1000/FPS)-round(i*1000/FPS)))
        # Replace by ID rather than adding a duplicate menu entry.
        chibi['actions']=[x for x in chibi['actions'] if x['id']!=aid]+[action]
        report['actions'][aid]=dict(frames=len(clips[aid]),durationMs=sum(f['durationMs'] for f in action['frames']),normalization=meta.get(aid,meta['humming']))
        for form in [m,m['forms']['human']]:
            humanidle=next(x for x in form['actions'] if x['id']=='idle')
            placeholder=dict(id=aid,displayName=None,loop=False,weight=0,triggers=[],interruptibleBy=['click','drag_start'],nextActionId='idle',frames=[copy.deepcopy(humanidle['frames'][0])])
            form['actions']=[x for x in form['actions'] if x['id']!=aid]+[placeholder]
    m['packVersion']='2.6.0';chibi['packVersion']='1.2.0'
    (PACK/'character.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    seams=[('sing_enter',-1,'sing_loop',0),('sing_loop',-1,'sing_loop',0),('sing_loop',-1,'sing_exit',0),('sing_enter',0,None,0),('sing_exit',-1,None,0)]
    report['endpointMaxPixelDifference']={f'{x}:{i}->{y or "idle"}:{j}':int(np.abs(clips[x][i].astype(int)-(stand if y is None else clips[y][j]).astype(int)).max()) for x,i,y,j in seams}
    assert not any(report['endpointMaxPixelDifference'].values())
    preserved=set()
    for form in [original,*original['forms'].values()]:
        for action in form['actions']:
            for frame in action['frames']:
                rel=frame['path'];preserved.add(rel)
                assert digest(PACK/rel)==digest(ROOT/'characters/gpt-dragon'/rel)
    report['preservedOldFrameFiles']=len(preserved)
    sheet=Image.new('RGB',(1500,len(specs)*290),'#242430');d=ImageDraw.Draw(sheet)
    preview_width=max(f[0].shape[1] for f in clips.values())
    writer=cv2.VideoWriter(str(RUN/'continuous-preview.mp4'),cv2.VideoWriter_fourcc(*'mp4v'),FPS,(preview_width,288))
    for row,(aid,_,_,_) in enumerate(specs):
        for col,i in enumerate(np.linspace(0,len(clips[aid])-1,5).astype(int)):
            f=Image.fromarray(clips[aid][i]);f.thumbnail((300,270))
            board=Image.new('RGBA',(300,270),'#242430' if col%2==0 else '#f3f0eb');board.alpha_composite(f,((300-f.width)//2,(270-f.height)//2));sheet.paste(board.convert('RGB'),(col*300,row*290+20));d.text((col*300+5,row*290+2),f'{aid} {i/FPS:.2f}s',fill='white')
    for aid in ['stretch','fire','sing_enter','sing_loop','sing_loop','sing_loop','sing_exit']:
        for f in clips[aid]:
            pad=(preview_width-f.shape[1])//2
            f=np.pad(f,((0,0),(pad,pad),(0,0)))
            f=cv2.resize(f,(preview_width//2,288),interpolation=cv2.INTER_AREA).astype(float);alpha=f[...,3:]/255
            board=np.concatenate([f[...,:3]*alpha+np.array(bg)*(1-alpha) for bg in [(36,36,48),(243,240,235)]],axis=1).astype(np.uint8)
            writer.write(cv2.cvtColor(board,cv2.COLOR_RGB2BGR))
    writer.release();sheet.save(RUN/'processed-review.png')
    for source in report['sources'].values():assert digest(Path(source['path']))==source['sha256']
    report['sourceIntegrity']='verified_unchanged'
    report['chibiFrames']=sum(len(x['frames']) for x in chibi['actions'])
    (RUN/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('Candidate ready:',PACK,flush=True)

if __name__=='__main__':main()
