"""Rotoscope the supplied human's hem gaps, using tracked contour masks."""
from pathlib import Path
import argparse, json
import cv2
import numpy as np
from PIL import Image, ImageDraw

ROI = (230, 750, 720, 990)
# Explicit negative spaces, traced from the normalized idle frame's drawn hem.
HOLES = [
    [(350,932),(366,926),(368,912),(378,903),(384,902),(397,906),
     (412,907),(414,926),(418,939),(426,951),(426,985),(342,985)],
    [(508,920),(518,922),(524,928),(536,933),(548,934),(551,985),
     (504,985),(504,948),(508,937)],
    [(454,868),(468,870),(474,882),(472,898),(466,914),(461,935),
     (458,941),(455,931),(454,914),(451,899)],
]
DRAG_HOLES = [
    [(374,925),(389,922),(403,915),(407,908),(408,931),(417,941),
     (427,947),(434,985),(362,985),(362,941)],
    [(504,908),(524,911),(540,908),(547,904),(548,931),(552,936),
     (550,985),(503,985),(503,937)],
    [(443,836),(465,836),(471,852),(469,872),(461,899),(453,920),
     (447,936),(443,938),(442,932),(446,914),(445,894),(442,875)],
]
LANDING_HOLES = [
    [(374,920),(402,916),(412,913),(415,937),(421,951),(420,985),
     (352,985),(352,937)],
    [(501,915),(520,920),(542,920),(549,923),(549,985),(501,985),(501,944)],
    [(455,879),(469,879),(472,897),(469,916),(461,940),(459,945),
     (455,940),(455,924),(453,912)],
]


def image_gray(a):
    alpha = a[...,3:]/255
    rgb = np.uint8(a[...,:3]*alpha+np.array([90,90,90])*(1-alpha))
    return cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)


def warp_mask(reference, current, mask):
    # Backward optical flow samples the reference mask at corresponding pixels.
    backward = cv2.calcOpticalFlowFarneback(image_gray(current),image_gray(reference),
                                           None,.5,5,31,5,7,1.5,0)
    yy,xx = np.mgrid[:current.shape[0],:current.shape[1]].astype(np.float32)
    return cv2.remap(mask,xx+backward[...,0],yy+backward[...,1],cv2.INTER_LINEAR,
                     borderMode=cv2.BORDER_CONSTANT)


def remove_matte_fragments(a):
    result=a.copy()
    n,labels,stats,_=cv2.connectedComponentsWithStats(np.uint8(result[...,3]>8),8)
    for label in range(1,n):
        if stats[label,cv2.CC_STAT_AREA]<80:
            result[labels==label]=0
    result[result[...,3]<=8]=0
    return result


def refine(root, reference_root=None, output_name='refined-matte'):
    c=json.loads((root/'character.json').read_text(encoding='utf-8'))
    actions={a['id']:a for a in c['forms']['human']['actions']}
    reference_root=reference_root or root
    ref= np.array(Image.open(reference_root/actions['idle']['frames'][0]['path']))
    x0,y0,x1,y1=ROI
    reference=ref[y0:y1,x0:x1]
    def make_mask(holes, source):
        mask=np.zeros(reference.shape[:2],np.float32)
        for polygon in holes:
            points=np.array([(x-x0,y-y0) for x,y in polygon],np.int32)
            cv2.fillPoly(mask,[points],1)
        rgb=source[...,:3].astype(np.int16)
        # Classify only the annotated reference, never independently per frame.
        # Protect pink legs/shoes and the light front fabric inside broad guides.
        removable=(source[...,3]<20) | ((rgb[...,0]<225) & (rgb[...,0]<rgb[...,2]+6)
                    & (rgb[...,1]<rgb[...,2]-4))
        mask*=removable
        return cv2.GaussianBlur(mask,(3,3),.45)
    mask=make_mask(HOLES,reference)
    drag_reference=np.array(Image.open(root/actions['drag']['frames'][14]['path']))[y0:y1,x0:x1]
    landing_reference=np.array(Image.open(root/actions['release']['frames'][29]['path']))[y0:y1,x0:x1]
    masks, originals, result = {}, {}, {}
    for aid,action in actions.items():
        frames=[np.array(Image.open(root/f['path'])) for f in action['frames']]
        originals[aid]=frames
        tracked=[]
        for index, a in enumerate(frames):
            if aid=='sleep':
                tracked.append(np.zeros_like(mask));continue
            # Only the frontal standing/slowing-down poses use these hem spaces.
            if aid=='sleep_enter':
                strength=max(0,min(1,(32-index)/12))
            elif aid=='wake':
                strength=max(0,min(1,(index-80)/12))
            else:
                strength=1
            current=a[y0:y1,x0:x1]
            if aid=='drag':
                tracked.append(warp_mask(drag_reference,current,make_mask(DRAG_HOLES,drag_reference)))
            elif aid=='release':
                base=warp_mask(reference,current,mask)
                landing=warp_mask(landing_reference,current,make_mask(LANDING_HOLES,landing_reference))
                weight=np.exp(-((index-29)/18)**2)
                tracked.append(base*(1-weight)+landing*weight)
            else:
                tracked.append(warp_mask(reference,current,mask)*strength)
        # Average neighboring tracked masks in the current frame's coordinates.
        # This keeps an edge continuous while following the moving shoes/hem.
        stable=[]
        for i,a in enumerate(frames):
            nearby=[tracked[i]]
            for j in [i-1,i+1]:
                if 0<=j<len(frames):
                    nearby.append(warp_mask(frames[j][y0:y1,x0:x1],a[y0:y1,x0:x1],tracked[j]))
            stable.append(np.mean(nearby,axis=0))
        masks[aid]=stable
        changed=[]
        for a,m in zip(frames,stable):
            b=a.copy();b[y0:y1,x0:x1,3]=np.uint8(b[y0:y1,x0:x1,3].astype(np.float32)*(1-np.clip(m,0,1)))
            b[b[...,3]==0]=0;changed.append(remove_matte_fragments(b))
        result[aid]=changed
        print('Tracked hem mask',aid,len(changed),flush=True)
    # Exact canonical joins also remove any mask differences at action endpoints.
    stand,seated,drag=result['idle'][0],result['sleep'][0],result['drag'][0]
    for aid,start,end in [('heart',stand,stand),('feed',stand,stand),
                          ('sleep_enter',stand,seated),('wake',seated,stand),('release',drag,stand)]:
        result[aid][0]=start.copy();result[aid][-1]=end.copy()
    for aid in ['idle','drag','sleep']:result[aid][-1]=result[aid][0].copy()
    review=Image.new('RGB',(2000,4*390),'#193829');draw=ImageDraw.Draw(review)
    for row,aid in enumerate(['idle','heart','drag','release']):
        frames=result[aid]
        for col,index in enumerate(np.linspace(0,len(frames)-1,5).astype(int)):
            bg=Image.new('RGBA',(900,1000),'#193829');bg.alpha_composite(Image.fromarray(frames[index]))
            review.paste(bg.crop((230,620,720,990)).resize((400,302)).convert('RGB'),(col*400,row*390+28))
            draw.text((col*400+5,row*390+5),f'{aid} {index}',fill='white')
    review.save(root/(output_name+'-review.jpg'))
    # Retain frames separately until sampled visual review passes.
    dest=root/output_name;dest.mkdir(exist_ok=False)
    for aid,action in actions.items():
        for f,a in zip(action['frames'],result[aid]):
            p=dest/f['path'];p.parent.mkdir(parents=True,exist_ok=True);Image.fromarray(a).save(p)
    (root/'matte-settings.json').write_text(json.dumps({'roi':ROI,'idleHoles':HOLES,'dragHoles':DRAG_HOLES,'landingHoles':LANDING_HOLES,'method':'backward dense optical flow; neighboring masks reprojected before averaging; canonical endpoint masks'},indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('root',type=Path)
    parser.add_argument('--output-name',default='refined-matte')
    args=parser.parse_args();refine(args.root.resolve(),output_name=args.output_name)
