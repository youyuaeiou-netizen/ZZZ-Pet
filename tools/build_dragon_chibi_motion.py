"""Author Q-form animation from complete ImageGen keyframes at a shared physical scale.

Writes a staged pack delta and review media. Human actions and original poses stay intact.
Uses the already installed local video-analysis Python and bundled sharp runtime.
"""
from pathlib import Path
import argparse
import copy
import hashlib
import json
import math
import subprocess

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
NODE = Path('C:/Users/h/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe')
SHARP = 'C:/Users/h/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/sharp'
FPS = 24
SPECS = [('idle', '呼吸待机', 4, True), ('drag', '被拎起悬空', 2, True),
         ('release', '短落轻缓冲', 0.875, False), ('heart', '爱心回应', 2, False),
         ('feed', '喂食反馈', 2.5, False), ('sleep_enter', '趴下入睡', 2, False),
         ('sleep', '可爱趴睡', 4, True), ('wake', '起身醒来', 2, False)]
NORMAL_SIZE = (464, 576)
SLEEP_SIZE = (640, 576)
PIXEL_SCALE = 304/757

def smooth(t):
    t = max(0, min(1, t))
    return t*t*(3-2*t)


def pulse(t, start, end):
    return math.sin(math.pi*(t-start)/(end-start))**2 if start < t < end else 0


def eyes(wink=False):
    # Follow the eye apertures; the fringe and cheek outlines remain above the patch.
    paths = [
        ('M267 396 Q300 380 331 397 L349 410 L344 442 Q303 460 279 440 L269 419 Z',
         'M272 418 Q304 429 338 417'),
        ('M400 401 Q432 380 466 393 L476 412 L468 440 Q432 457 404 441 Z',
         'M404 417 Q436 429 468 416')]
    return ''.join(f'<path d="{shape}" fill="url(#skin)"/><path d="{line}" fill="none" '
                   'stroke="#72536b" stroke-width="3.5" stroke-linecap="round"/>'
                   for i, (shape, line) in enumerate(paths) if not wink or i == 1)


def make_assets(base, cache):
    w, h = base.size
    defs = '<defs><linearGradient id="skin" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#fff7f4"/><stop offset="1" stop-color="#fce9e6"/></linearGradient></defs>'
    shapes = {
        'closed': eyes()+'<path d="M355 460H392V484H355Z" fill="#fff1ed"/><path d="M362 471Q373 480 384 471" fill="none" stroke="#aa768c" stroke-width="2.5" stroke-linecap="round"/>', 'wink': eyes(True),
        'heart': '<path d="M0 27C-45 -12 -72 45 0 91C72 45 45 -12 0 27Z" fill="#e9a8c4" stroke="#bd7b9c" stroke-width="3"/><path d="M-29 26Q-37 35 -24 44" fill="none" stroke="#fff3fa" stroke-width="7" stroke-linecap="round"/>',
        'food': '<path d="M-30 0L-24 35Q0 46 24 35L30 0Z" fill="#eed4ad" stroke="#ac83a1" stroke-width="2"/><path d="M-33 0Q-30 -15 -15 -17Q-22 -33 0 -39Q22 -33 16 -17Q31 -15 33 0Z" fill="#faf0fc" stroke="#ac83a1" stroke-width="2"/><circle cx="0" cy="-40" r="6" fill="#d291b3"/><path d="M-15 8L-12 29M0 9V32M15 8L12 29" stroke="#d2b58d" stroke-width="2"/>'}
    jobs = []
    for name, shape in shapes.items():
        if name in ('heart', 'food'):
            size = (140, 140)
            shape = '<g transform="translate(70 45)">' + shape + '</g>' if name == 'heart' else '<g transform="translate(70 70)">' + shape + '</g>'
        else:
            size = (w, h)
        svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{size[0]}" height="{size[1]}">{defs}{shape}</svg>'
        jobs.append({'svg': svg, 'target': str(cache/(name+'.png'))})
    job_path = cache/'overlays.json'
    job_path.write_text(json.dumps(jobs), encoding='utf-8')
    code = "const fs=require('fs'),sharp=require(process.argv[1]);(async()=>{for(const j of JSON.parse(fs.readFileSync(process.argv[2],'utf8')))await sharp(Buffer.from(j.svg)).png().toFile(j.target)})().catch(e=>{console.error(e);process.exitCode=1})"
    subprocess.run([str(NODE), '-e', code, SHARP, str(job_path)], check=True)
    assets = {name: Image.open(cache/(name+'.png')).convert('RGBA') for name in shapes}
    assets['eyeMask'] = assets['closed'].getchannel('A')
    for name in ('closed', 'wink'):
        pose = base.copy()
        pose.alpha_composite(assets[name])
        assets[name] = pose
    return assets


def fit_pose(path, size, max_size, bottom):
    source = Image.open(path).convert('RGBA')
    bounds = source.getchannel('A').point(lambda a: 255 if a > 16 else 0).getbbox()
    if not bounds: raise ValueError('Pose is empty: '+str(path))
    crop = source.crop(bounds)
    ratio = min(max_size[0]/crop.width, max_size[1]/crop.height)
    crop = crop.resize((round(crop.width*ratio),round(crop.height*ratio)), Image.Resampling.LANCZOS)
    canvas = Image.new('RGBA', size)
    canvas.alpha_composite(crop, ((size[0]-crop.width)//2, bottom-crop.height))
    return canvas


def premult(image):
    pixels = np.asarray(image).astype(np.float32)/255
    pixels[:, :, :3] *= pixels[:, :, 3:4]
    return pixels


def composite_float(a, b, mix):
    # Linear interpolation in premultiplied alpha avoids dark transparent fringes.
    return premult(a)*(1-mix)+premult(b)*mix


def pad_pose(image, width):
    result = Image.new('RGBA',(width,image.height))
    result.alpha_composite(image,((width-image.width)//2,0))
    return result


def close_eyes(base, assets, amount, wink=False):
    # Compress the original eye aperture vertically under the eyelid instead of
    # dissolving the iris into a closed-eye drawing (which looks like ghost pupils).
    if amount <= 1e-6:
        return base.copy()
    pose = assets['wink' if wink else 'closed'].copy()
    for index, box in enumerate(((263,382,354,459), (397,382,480,459))):
        if wink and index == 0:
            continue
        if amount >= .995:
            continue
        eye = base.crop(box)
        eye.putalpha(assets['eyeMask'].crop(box))
        height = max(1, round(eye.height*(1-amount)))
        eye = eye.resize((eye.width, height), Image.Resampling.LANCZOS)
        pose.alpha_composite(eye, (box[0], 420-height//2))
    return pose


def sticker(image, asset, center, scale, opacity):
    if opacity <= 0 or scale <= 0:
        return
    size = (max(1, round(asset.width*scale)), max(1, round(asset.height*scale)))
    tile = asset.resize(size, Image.Resampling.LANCZOS)
    tile.putalpha(tile.getchannel('A').point(lambda a: round(a*opacity)))
    image.alpha_composite(tile, (round(center[0]-size[0]/2), round(center[1]-size[1]/2)))


def rgba(p):
    p=p.copy();p[:,:,:3]/=np.maximum(p[:,:,3:4],1e-6)
    return Image.fromarray(np.uint8(np.clip(p,0,1)*255+.5))


def atlas_pose(atlas,box,scale,size,foot=None):
    source=atlas.crop(box)
    mask=np.uint8(np.asarray(source.getchannel('A'))>16)
    count,labels,stats,centroids=cv2.connectedComponentsWithStats(mask,8)
    if count<2:raise ValueError('Empty keyframe')
    x,y,w,h,area=stats[1+np.argmax(stats[1:,4])]
    bounds=(max(0,x-1),max(0,y-1),min(source.width,x+w+1),min(source.height,y+h+1))
    foot=(bounds[3]-1 if foot is None else foot)-bounds[1]
    source=source.crop(bounds)
    tile=source.resize((round(source.width*scale),round(source.height*scale)),Image.Resampling.LANCZOS)
    result=Image.new('RGBA',size)
    result.alpha_composite(tile,((size[0]-tile.width)//2,round(478-foot*scale)))
    return result


def atlas_sequence(path,rows,scale,size):
    atlas=Image.open(path).convert('RGBA'); seq=[]
    for top,bottom in zip(rows,rows[1:]):
        for left,right in [(0,512),(512,1024)]:
            seq.append(atlas_pose(atlas,(left,top,right,bottom),scale,size))
    return seq


def face_box(image):
    # The exposed face, unlike the hem or wings, retains its anatomical width
    # when the knees bend. Palette and search region are specific to this pack.
    r,g,b,a=np.asarray(image).astype(np.int16).transpose(2,0,1)
    mask=np.uint8((r-b>8)&(r-g>5)&(r>150)&(g>110)&(a>128))
    mask[:30]=0;mask[340:]=0;mask[:,:90]=0;mask[:,374:]=0
    count,labels,stats,centers=cv2.connectedComponentsWithStats(mask,8)
    if count<2:raise ValueError('No face landmark in Q keyframe')
    box=stats[1+np.argmax(stats[1:,4])]
    if box[4]<800:raise ValueError('Unreliable face landmark in Q keyframe')
    return box[:4].tolist()


def align_anatomical_scale(image,reference):
    x,y,width,height=face_box(image)
    rx,ry,rwidth,rheight=face_box(reference)
    ratio=rwidth/width
    # Uniform scale preserves proportions; feet remain at the shared anchor.
    for attempt in range(3):
        matrix=np.float32([[ratio,0,rx+rwidth/2-ratio*(x+width/2)],
                           [0,ratio,478*(1-ratio)]])
        result=rgba(cv2.warpAffine(premult(image),matrix,image.size,flags=cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT))
        measured=face_box(result)[2]
        if abs(measured-rwidth)<=1:break
        ratio*=rwidth/measured
    bounds=result.getchannel('A').point(lambda a:255 if a>16 else 0).getbbox()
    if bounds[0]<2 or bounds[2]>image.width-2 or bounds[1]<2:
        raise ValueError('Anatomical scale alignment would clip a character feature')
    if abs(face_box(result)[2]-rwidth)>2:raise ValueError(f'Face scale alignment failed: {width} -> {face_box(result)[2]}, expected {rwidth}')
    return result


class Keyframes:
    def __init__(self,frames,times):
        self.frames=[premult(f) for f in frames];self.times=times;self.flows=[]
        dis=cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
        gray=[cv2.cvtColor(np.uint8(p[:,:,:3]*255),cv2.COLOR_RGB2GRAY)for p in self.frames]
        for a,b in zip(gray,gray[1:]):self.flows.append((dis.calc(a,b,None),dis.calc(b,a,None)))
        self.yy,self.xx=np.mgrid[:frames[0].height,:frames[0].width].astype(np.float32)
    def at(self,t):
        if t<=self.times[0]:return self.frames[0].copy()
        if t>=self.times[-1]:return self.frames[-1].copy()
        i=next(i for i in range(len(self.times)-1)if t<=self.times[i+1])
        u=smooth((t-self.times[i])/(self.times[i+1]-self.times[i]));f,b=self.flows[i]
        a=cv2.remap(self.frames[i],self.xx-f[:,:,0]*u,self.yy-f[:,:,1]*u,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
        z=cv2.remap(self.frames[i+1],self.xx-b[:,:,0]*(1-u),self.yy-b[:,:,1]*(1-u),cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
        # Select one complete warped drawing rather than dissolving two faces,
        # hands and dress outlines into a translucent double image.
        return a if u < .5 else z


def render(base,assets,action,t,seconds):
    phase=math.tau*t/seconds;amount=math.sin(phase)
    if action=='release':return rgba(assets['landingSequence'].at(t))
    if action=='sleep_enter':return rgba(assets['sleepSequence'].at(t))
    if action=='wake':return rgba(assets['sleepSequence'].at(2-t))
    image=assets['sleepPose'] if action=='sleep' else assets['heldPose'] if action in ('drag','fall') else assets['standPose']
    p=premult(image);h,w=p.shape[:2];yy,xx=np.mgrid[:h,:w].astype(np.float32)
    dx=np.zeros_like(xx);dy=np.zeros_like(xx)
    if action in ('drag','fall'):
        hands=np.exp(-((yy-340)/42)**2)*(np.exp(-((xx-w*.27)/32)**2)-np.exp(-((xx-w*.73)/32)**2))
        feet=np.exp(-((yy-456)/20)**2)*np.exp(-((xx-w*.5)/45)**2)
        hem=np.exp(-((yy-414)/38)**2)*np.clip(abs(xx-w*.5)/85,0,1)
        hair=np.exp(-((yy-305)/65)**2)*(np.exp(-((xx-w*.23)/35)**2)+np.exp(-((xx-w*.77)/35)**2))
        tail=np.exp(-((yy-421)/44)**2-((xx-w*.77)/40)**2)
        if action=='drag':dx=amount*(5*hands+3*feet+3*hem);dy=amount*2*hands
        else:dy=-t*(4*hair+5*hem+8*tail);dx=t*tail
    elif action=='sleep':dy=amount*1.5*np.exp(-((xx-w*.64)/70)**2-((yy-425)/35)**2)
    else:dy=-amount*1.8*np.exp(-((yy-333)/75)**2)*np.exp(-((xx-w*.5)/95)**2)
    p=cv2.remap(p,xx-dx,yy-dy,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
    if action=='drag':
        matrix=cv2.getRotationMatrix2D((w/2,88),2.2*amount,1)
        p=cv2.warpAffine(p,matrix,(w,h),flags=cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
    image=rgba(p)
    envelope=math.sin(math.pi*t/seconds)**2
    if action=='heart':sticker(image,assets['heart'],(w*.5,260-60*t/seconds),.4,envelope)
    if action=='feed':sticker(image,assets['food'],(w*.5,350-55*envelope),.4,envelope)
    return image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--sample',action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT/'build') or output.exists():
        raise ValueError('Use a new output directory under this repository build directory.')
    output.mkdir(parents=True)
    staged = output/'gpt-dragon'
    cache = output/'authoring'
    cache.mkdir()
    original = json.loads((ROOT/'characters/gpt-dragon/character.json').read_text(encoding='utf-8-sig'))
    manifest = copy.deepcopy(original)
    form = manifest['forms']['chibi']
    form.update(canvasWidth=232,canvasHeight=288,anchorX=116,anchorY=239)
    base_path = ROOT/'characters/gpt-dragon/poses/chibi/idle.png'
    base = Image.open(base_path).convert('RGBA')
    assets = make_assets(base, cache)
    poses=ROOT/'characters/gpt-dragon/poses/chibi'
    landing=atlas_sequence(poses/'landing-keyframes.png',[0,404,776,1130,1536],1.06,NORMAL_SIZE)
    sleeping=atlas_sequence(poses/'sleep-keyframes.png',[0,514,912,1210,1536],.89,SLEEP_SIZE)
    # Preserve the accepted standing artwork and every return-to-idle endpoint.
    reference=Image.open(ROOT/'characters/gpt-dragon/animations/chibi/idle/0000.png').convert('RGBA')
    landing=[align_anatomical_scale(pose,reference) for pose in landing[:-1]]+[reference]
    held=landing[0]
    existing_actions={a['id']:a for a in original['forms']['chibi']['actions']}
    existing_sleep=Image.open(ROOT/'characters/gpt-dragon'/existing_actions['sleep']['frames'][0]['path']).convert('RGBA')
    assets.update(standPose=landing[-1],heldPose=held,sleepPose=existing_sleep)
    sleeping[0]=pad_pose(landing[-1],SLEEP_SIZE[0])
    # Complete faces and drawn anatomy remain intact; no copied standing face,
    # per-action auto-fit, joint stretching or whole-character squash.
    assets['landingSequence']=Keyframes(landing[2:],[0,.12,.28,.46,.65,.875])
    assets['sleepSequence']=Keyframes(sleeping,[0,.25,.55,.88,1.22,1.52,1.78,2])
    comparison=Image.new('RGB',(1280,370),'#262633')
    for index,im in enumerate([landing[-1],held,landing[3],existing_sleep]):
        im=im.resize((im.width//2,im.height//2),Image.Resampling.LANCZOS)
        comparison.paste(im,(index*320+(320-im.width)//2,30),im)
    comparison.save(output/'size-comparison.png')
    if args.sample:
        for action,t,seconds in [('drag',0,2),('release',.12,.875),('sleep',0,4),*[('sleep_enter',t,2)for t in (.7,1,1.25,1.5,1.75)]]:
            render(base,assets,action,t,seconds).save(output/f'{action}-{t}.png')
        return
    by_id = {a['id']: a for a in form['actions']}
    sequences = {}
    rows = []
    for action, label, seconds, loop in SPECS:
        if action not in ('drag','release'):
            entry=by_id[action]
            sequences[action]=[Image.open(ROOT/'characters/gpt-dragon'/f['path']).convert('RGBA') for f in entry['frames']]
            rows.append({'id':action,'name':entry['displayName'],'frames':len(entry['frames']),'durationMs':sum(f['durationMs'] for f in entry['frames'])})
            continue
        directory = staged/'animations/chibi'/action
        directory.mkdir(parents=True)
        count = round(seconds*FPS)
        sequence, frames = [], []
        for i in range(count):
            t = i*seconds/(count-1)
            image = render(base, assets, action, t, seconds)
            name = f'{i:04}.png'
            image.save(directory/name)
            frames.append({'path': f'animations/chibi/{action}/{name}', 'durationMs': round((i+1)*1000/FPS)-round(i*1000/FPS)})
            sequence.append(image)
        sequences[action] = sequence
        entry = by_id[action]
        entry['displayName'] = label
        entry['frames'] = frames
        if action == 'release':
            # Airborne frame is held by PetWindow until real floor contact.
            air_frames=[]
            for index in range(12):
                name=f'air-{index:02}.png'
                render(base,assets,'fall',index/11,1).save(directory/name)
                air_frames.append({'path':f'animations/chibi/{action}/{name}','durationMs':1})
            entry['frames'] = air_frames+frames
        if action in ('sleep_enter','sleep','wake'):
            entry.update(viewportWidth=320,viewportHeight=288,viewportAnchorX=160,viewportAnchorY=239)
        entry['loop'] = loop
        entry['triggers'] = {'idle':['app_start'], 'drag':['drag_start'], 'release':['drag_release'],
                             'heart':['click','double_click'], 'feed':[], 'sleep_enter':['idle_timeout'],
                             'sleep':[], 'wake':['notification']}[action]
        entry['showSleepIndicator'] = action == 'sleep'
        if not loop:
            entry['nextActionId'] = 'sleep' if action == 'sleep_enter' else 'idle'
        else:
            entry.pop('nextActionId', None)
        rows.append({'id':action, 'name':label, 'frames':len(entry['frames']), 'durationMs':sum(f['durationMs'] for f in entry['frames'])})
    manifest['packVersion'] = '2.4.2'
    (staged/'character.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    assert manifest['forms']['human'] == original['forms']['human']
    assert manifest['actions'] == original['actions']
    font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 20)
    board = Image.new('RGB', (1280, 1096), '#262633')
    draw = ImageDraw.Draw(board)
    for index, (action, label, _, _) in enumerate(SPECS):
        x, y = index%4*320, index//4*548
        draw.text((x+16, y+12), label, font=font, fill='#e6dcf1')
        seq = sequences[action]
        frame = seq[len(seq)//2]
        if action == 'heart': frame = seq[len(seq)//3]
        if action == 'feed': frame = seq[len(seq)//3]
        frame = frame.resize((round(frame.width*.5),round(frame.height*.5)),Image.Resampling.LANCZOS)
        board.paste(frame, (x+(320-frame.width)//2, y+58), frame)
    board.save(output/'review.png')
    preview = []
    for i in range(FPS*4):
        panel = Image.new('RGB', board.size, '#262633')
        draw = ImageDraw.Draw(panel)
        for index, (action, label, _, loop) in enumerate(SPECS):
            x, y = index%4*320, index//4*548
            draw.text((x+16, y+12), label, font=font, fill='#e6dcf1')
            seq = sequences[action]
            frame = seq[i%len(seq) if loop else min(i, len(seq)-1)]
            frame=frame.resize((round(frame.width*.5),round(frame.height*.5)),Image.Resampling.LANCZOS)
            panel.paste(frame, (x+(320-frame.width)//2, y+58), frame)
        preview.append(panel.resize((960, 822), Image.Resampling.LANCZOS))
    writer = cv2.VideoWriter(str(output/'review.mp4'), cv2.VideoWriter_fourcc(*'mp4v'), FPS, preview[0].size)
    if not writer.isOpened(): raise RuntimeError('Review video encoder unavailable')
    for frame in preview: writer.write(cv2.cvtColor(np.asarray(frame), cv2.COLOR_RGB2BGR))
    writer.release()
    preview[0].save(output/'review.gif', save_all=True, append_images=preview[2::2], duration=83, loop=0)
    report = {'version':'2.4.2', 'fps':FPS, 'frameSize':list(NORMAL_SIZE),'sleepFrameSize':list(SLEEP_SIZE), 'atlasScales':{'landingInitial':1.06,'sleep':.89}, 'faceWidths':[face_box(pose)[2] for pose in landing], 'referenceFaceWidth':face_box(reference)[2], 'sourceSha256':hashlib.sha256(base_path.read_bytes()).hexdigest(),
              'humanUnchanged':True, 'actions':rows, 'totalFrames':sum(r['frames'] for r in rows),
              'notes':'Only drag/release changed: natural separated dangling legs and gentle cushioning. Short drop capped at 48 scaled DIP. All six other action definitions and frames preserved.'}
    (output/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__':
    main()
