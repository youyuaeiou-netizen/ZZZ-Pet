"""Review-only idle pilot: retain opaque source RGB and existing alpha, timing and layout."""
from pathlib import Path
import copy,hashlib,json,shutil,sys
import cv2
import numpy as np
from PIL import Image,ImageDraw

root=Path('C:/pet/ZZZ-Pet');sys.path.insert(0,str(root/'tools'))
from build_flat2d_pack import key_green
run=root/'build/generated/clarity-pilot-20261020'
source=root/'characters/gpt-dragon';pack=run/'packs/gpt-dragon'
if pack.exists():raise FileExistsError(pack)
shutil.copytree(source,pack)
m=json.loads((pack/'character.json').read_text(encoding='utf-8-sig'))
idle=next(a for a in m['forms']['chibi']['actions'] if a['id']=='idle')
oldframes=copy.deepcopy(idle['frames'])
scale=408/634
size=(round(1280*scale),round(720*scale));offset=(round(260-631*scale),round(520-663*scale))
raws=sorted((run/'raw').glob('*.png'));assert len(raws)==len(oldframes)==60
samples={};baseline_max=0;rgb_delta=[]
for i,(raw,f) in enumerate(zip(raws,oldframes)):
    a=np.array(Image.open(raw).convert('RGBA'))
    old=key_green(a);new=key_green(a,preserve_interior=True)
    assert np.array_equal(old[...,3],new[...,3])
    core=cv2.erode(np.uint8(new[...,3]==255),np.ones((7,7),np.uint8)).astype(bool)
    assert np.array_equal(new[...,:3][core],a[...,:3][core])
    def norm(im):
        canvas=Image.new('RGBA',(640,576));canvas.paste(Image.fromarray(im).resize(size,Image.Resampling.LANCZOS),offset);return canvas
    oldim=norm(old);newim=norm(new)
    deployed=Image.open(source/f['path']).convert('RGBA')
    baseline_max=max(baseline_max,int(np.abs(np.array(oldim).astype(int)-np.array(deployed).astype(int)).max()))
    assert np.array_equal(np.array(deployed)[...,3],np.array(newim)[...,3])
    mask=np.array(newim)[...,3]>240
    rgb_delta.append(float(np.abs(np.array(newim)[...,:3].astype(float)-np.array(deployed)[...,:3])[mask].mean()))
    rel=f'animations/chibi/idle-clarity-pilot-20261020/{i:04d}.png'
    p=pack/rel;p.parent.mkdir(parents=True,exist_ok=True);newim.save(p);f['path']=rel
    if i in [0,10,40]:samples[i]=(deployed,newim)
    if i%20==0:print('Prepared idle',i,flush=True)
idle['frames']=oldframes
(pack/'character.json').write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
sheet=Image.new('RGB',(1280,800),'white');d=ImageDraw.Draw(sheet)
for row,color in enumerate(['#ffffff','#242430']):
    for col,im in enumerate(samples[0]):
        # Same dimensions as a 320x288 logical sprite at 125% display scaling.
        im=im.resize((400,360),Image.Resampling.LANCZOS)
        bg=Image.new('RGBA',(400,360),color);bg.alpha_composite(im)
        sheet.paste(bg.convert('RGB'),(col*640,row*400+25))
        # Nearest-neighbor detail crop adds no invented sharpness.
        detail=bg.crop((65,55,230,175)).resize((220,160),Image.Resampling.NEAREST)
        sheet.paste(detail.convert('RGB'),(col*640+410,row*400+100))
        d.text((col*640+10,row*400+5),'CURRENT' if col==0 else 'PILOT: preserve opaque RGB',fill='black' if row==0 else 'white')
sheet.save(run/'old-new-comparison.png')
video=cv2.VideoWriter(str(run/'idle-comparison.mp4'),cv2.VideoWriter_fourcc(*'mp4v'),24,(800,360))
for oldf,newf in zip(next(a for a in json.loads((source/'character.json').read_text(encoding='utf-8-sig'))['forms']['chibi']['actions'] if a['id']=='idle')['frames'],oldframes):
    boards=[]
    for p in [source/oldf['path'],pack/newf['path']]:
        im=Image.open(p).convert('RGBA').resize((400,360),Image.Resampling.LANCZOS);bg=Image.new('RGBA',(400,360),'white');bg.alpha_composite(im);boards.append(np.array(bg)[...,:3])
    board=cv2.cvtColor(np.concatenate(boards,axis=1),cv2.COLOR_RGB2BGR)
    for _ in range(max(1,round(newf['durationMs']*24/1000))):video.write(board)
video.release()
report={'status':'review-only; not deployed','frames':60,'timingAndAlphaUnchanged':True,'sourceOpaqueInteriorRgbPreserved':True,'oldPipelineReproductionMaxChannelDifference':baseline_max,'meanOpaquePixelRgbDifferenceRange':[min(rgb_delta),max(rgb_delta)],'canvas':[640,576],'physicalScale':scale,'sourceSha256':hashlib.sha256(Path('C:/Users/h/Desktop/视频/Q版/待机.mp4').read_bytes()).hexdigest(),'notes':'Preserve interior RGB; same alpha and Lanczos resize; no sharpening, recolor, resize change or invented detail.'}
(run/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,ensure_ascii=False))
