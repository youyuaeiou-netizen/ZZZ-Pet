"""Higher-fidelity VTracer face patches, leaving source files and existing packs intact."""
from pathlib import Path
import sys, xml.etree.ElementTree as ET
sys.path.insert(0,'C:/pet/dev-tools/vector-art')
from PIL import Image
import vtracer

ROOT=Path(__file__).resolve().parents[1]
CACHE=ROOT/'build/.cache'
ART=Path('C:/Users/h/Desktop/素材/gpt/龙娘桌宠')
OUT=ART/'VTracer脸部修订'
OUT.mkdir(exist_ok=True)
NS='http://www.w3.org/2000/svg'
ET.register_namespace('',NS)
for form,box in [('human',(280,135,410,224)),('chibi',(220,350,475,505))]:
    image=Image.open(CACHE/'dragon-art'/(form+'.png'))
    region=CACHE/'dragon-art'/(form+'-face.png')
    image.crop(box).save(region)
    face=CACHE/'dragon-art'/(form+'-face.svg')
    vtracer.convert_image_to_svg_py(str(region),str(face),colormode='color',hierarchical='stacked',
        mode='spline',filter_speckle=0,color_precision=8,layer_difference=1,
        corner_threshold=60,length_threshold=1,max_iterations=10,splice_threshold=45,path_precision=3)
    tree=ET.parse(ART/(form+'.svg')); group=ET.SubElement(tree.getroot(),f'{{{NS}}}g',
        dict(id='face-detail-refinement',transform=f'translate({box[0]} {box[1]})'))
    for child in ET.parse(face).getroot():
        if child.tag.endswith('path'): group.append(child)
    target=OUT/(form+'.svg'); tree.write(target,encoding='utf-8',xml_declaration=True)
    print(form,'face paths',len(group),'total bytes',target.stat().st_size)
