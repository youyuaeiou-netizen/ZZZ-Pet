"""Prepare Jimeng references/jobs using the existing video-to-PNG pipeline."""
from pathlib import Path
from PIL import Image
import json, shutil

ROOT=Path(__file__).resolve().parents[1]
SOURCE=Path('C:/Users/h/Desktop/素材/gpt')
OUT=SOURCE/'龙娘桌宠/即梦制作包'
WORK=ROOT/'workflow/gpt-dragon'
ACTION_TEXT={
 'idle':('待机','0–3秒：正面站立，仅胸肩轻微呼吸，长发、裙摆、两只翅膀和唯一一条尾巴轻微摆动；3秒自然回到首帧姿态。3–5秒继续同节奏，不出现额外动作。',3,True,['app_start'],None),
 'heart':('比心','0–0.7秒：从原站姿缓缓抬起双手；0.7–2.2秒：在胸前用两手的拇指和食指围成一个爱心，参考比心图的手势，轻轻微笑、短暂眨一只眼；2.2–3.5秒：缓缓放下双手、恢复原站姿与睁眼表情；3.5–5秒保持待机。只参考第三张的手势，不复制其坐姿、脸型或画面裁切，不出现悬浮爱心、文字或特效。',3.5,False,['click','double_click'],'idle'),
 'drag':('拖起悬空','0–3秒：保持同一轻微悬空姿态，双手放松、裙摆自然垂落、双翼微收、单尾轻摆，表情略疑惑，完成一次轻微摆动循环；3–5秒继续相同循环。不飞走，不改变镜头和角色中心，不加入抓住角色的手。',3,True,['drag_start'],None),
 'release':('放下缓冲','0秒从同套悬空姿态开始；0–0.6秒脚尖轻轻落地、裙摆和尾巴轻微缓冲；0.6–2秒自然恢复原站姿；2–5秒待机。脚底基准不横移，不跌倒，不掀起裙子。',2,False,['drag_release'],'idle'),
 'sleep_enter':('入睡','0–0.8秒保持站立；0.8–2秒缓缓闭眼，头部轻轻低下，肩部放松；2–3.5秒进入安静的闭眼站立睡眠；3.5–5秒保持该姿态。只改变眼睑与很小的头部动作，不坐下，不加入眼罩、床、枕头或文字。',3.5,False,['idle_timeout'],'sleep'),
 'sleep':('睡眠循环','从第一帧到最后一帧保持同一闭眼站姿，双手垂落、头部轻低；0–3秒轻微呼吸并回到第一帧姿态；3–5秒继续相同节奏。双翼、单尾和裙摆仅轻微摆动，不睁眼，不站起或坐下，不画ZZZ。',3,True,[],None),
 'wake':('醒来','0秒从同套闭眼站立睡眠姿态开始；0–1秒缓慢睁开同一对紫色眼睛；1–2秒轻抬头、肩部恢复自然位置；2–3秒回到原站姿；3–5秒保持待机。不得硬切，不能换脸、伸懒腰或新增道具。',3,False,['notification'],'idle'),
 'feed':('喂食','0–0.7秒双手捧起一块小巧的浅金色饼干，送到嘴边，食物不遮挡双眼；0.7–2.8秒小口咬食，饼干随着咬食逐渐变小，嘴部只做轻微动作；2.8–4秒吃完、放下双手、恢复原站姿；4–5秒待机。饼干吃完后完全消失，不增加餐具、人的手、烟雾、文字或其他食物。',4,False,[],'idle'),
}
IDENTITY='''参考图是唯一造型标准。保留白色长发、淡紫色阴影、紫瞳、两只白色龙角、尖耳、两只淡紫龙翼、唯一一条鳞片龙尾、白色长裙、袖摆和结状饰品。严格保持参考图脸型、眼睛大小、头身比例、发型、服饰和装饰位置。维持原画的细线和柔和阴影，不改为平涂简笔画、3D、像素风或写实风。
固定镜头，16:9，5秒，优先1080P；背景纯绿#00FF00，无地面、投影、字幕或其他人物。以提供的绿色首帧为角色位置和尺度基准，禁止缩放、横移、镜头运动或转身；角、头发、袖子、裙摆、翅膀、尾巴和道具全程完整留在画面内。只改变动作所需部位，单尾始终为一条，不增减肢体、翅膀、龙角或配饰。
'''
OUT.mkdir(parents=True,exist_ok=True); WORK.mkdir(parents=True,exist_ok=True)
fulltexts=['# 即梦 GPT 龙娘试片\n\n先制作人物版、Q版各 idle.mp4 和 heart.mp4；不要一次性生成全部动作。\n']
for form,label,original in [('human','人物版','codex-clipboard-0bafe644-04e7-4a1d-826a-5cbb63ed0a66.png'),('chibi','Q版','codex-clipboard-587819ba-23c8-4ae5-9c9e-e455d45cf222.png')]:
    dest=OUT/form; (dest/'prompts').mkdir(parents=True,exist_ok=True); (dest/'source-videos').mkdir(exist_ok=True)
    shutil.copy2(SOURCE/original,dest/'三视图.png')
    shutil.copy2(SOURCE/'codex-clipboard-b540513f-12be-42e2-b1e5-402a694c3afc.png',dest/'比心手势参考.png')
    image=Image.open(ROOT/'build/.cache/dragon-art'/f'{form}.png').convert('RGBA')
    image=image.crop(image.getchannel('A').getbbox())
    scale=702/image.height
    image=image.resize((round(image.width*scale),702),Image.Resampling.LANCZOS)
    first=Image.new('RGBA',(1920,1080),'#00ff00'); first.alpha_composite(image,((1920-image.width)//2,216))
    first.convert('RGB').save(dest/'首帧-站立.png')
    # These are keyframe drafts, not video animations. User supplied original stand is the identity reference.
    sleep=Image.open(ROOT/'characters/gpt-dragon/poses'/form/'sleep.png').convert('RGBA')
    sleep=sleep.crop(sleep.getchannel('A').getbbox()).resize(image.size,Image.Resampling.LANCZOS)
    asleep=Image.new('RGBA',(1920,1080),'#00ff00'); asleep.alpha_composite(sleep,((1920-sleep.width)//2,216))
    asleep.convert('RGB').save(dest/'首帧-睡眠草稿.png')
    actions=[]; jobs=[]
    for aid,(name,choreography,end,loop,triggers,nxt) in ACTION_TEXT.items():
        reference='首帧-睡眠草稿.png' if aid in ['sleep','wake'] else '首帧-站立.png'
        text=f'# {label} · {name}\n\n上传首帧：{reference}。'+('比心手势参考图只用于手势说明，人物身份以本形态原图为准。' if aid=='heart' else '')+'\n\n'+IDENTITY+f'\n当前形态：{label}，全程维持这个形态，禁止变大、变小或变成另一形态。\n\n'+choreography+'\n\n保存原始生成视频为 '+aid+'.mp4。不要加速、裁切画面或去除平台标识；时间截取按实际结果调整。\n'
        (dest/'prompts'/f'{aid}.md').write_text(text,encoding='utf-8')
        if aid in ['idle','heart']: fulltexts.append(text)
        action=dict(id=aid,displayName=name,category='龙娘',loop=loop,priority=0 if aid=='idle' else 10,
                    triggers=triggers,weight=1,cooldownMs=0,interruptibleBy=['click','double_click','drag_start','notification','idle_timeout'])
        if nxt: action['nextActionId']=nxt
        if aid=='sleep': action['showSleepIndicator']=True
        actions.append(action); jobs.append(dict(id=aid,input=aid+'.mp4',mode='green',start=0,end=end))
    dims=dict(canvasWidth=252,canvasHeight=434,anchorX=126,anchorY=420) if form=='human' else dict(canvasWidth=152,canvasHeight=232,anchorX=76,anchorY=223)
    layout=dict(width=dims['canvasWidth']*2,height=dims['canvasHeight']*2,anchorX=dims['anchorX']*2,anchorY=dims['anchorY']*2,standingHeight=816 if form=='human' else 408)
    config=dict(referenceStatus='approved',referenceImage=str(dest/'首帧-站立.png'),sourceRoot=str(dest/'source-videos'),decodeSize=[1920,1080],layout=layout,
                character=dict(id='gpt-dragon',name='GPT 龙娘 · '+label,packVersion='2.0.0',defaultScale=1,defaultFacing='right',immediateClick=True,sleepThresholdMs=60000,wakeActionId='wake',**dims),actions=actions,jobs=jobs)
    (WORK/f'{form}-jobs.json').write_text(json.dumps(config,ensure_ascii=False,indent=2),encoding='utf-8')
    pilot=dict(config,actions=[a for a in actions if a['id'] in ['idle','heart']],jobs=[j for j in jobs if j['id'] in ['idle','heart']])
    (WORK/f'{form}-pilot-jobs.json').write_text(json.dumps(pilot,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'先生成这四段.md').write_text('\n\n---\n\n'.join(fulltexts),encoding='utf-8')
(OUT/'使用说明.md').write_text('''# 即梦制作包

采用项目原有“参考图 → 图生视频 → FFmpeg 抽帧 → 抠绿 → 统一尺度和锚点 → PNG角色包”流程。当前仅有参考图、提示词和导入配置，还没有龙娘真实动作视频，也没有部署。

1. 在即梦选择图生视频，人物版与Q版分别上传 human/chibi 内的「首帧-站立.png」。优先选1080P、16:9、5秒，按页面实际可用选项设置。
2. 打开「先生成这四段.md」，先生成两种形态各一段待机和比心。每段单独提交；有尾帧功能时可用同一站立图作为返回姿态参考，循环必须仍逐帧检查。
3. 检查脸、眼睛、两只角、两只翼、单尾、服饰、边缘和循环接缝。脸变形、比例漂移或裁切的结果重做。睡眠首帧是闭眼覆盖草稿，须先看是否满意。
4. 视频原文件分别保存到 human/source-videos 和 chibi/source-videos，名字为 idle.mp4、heart.mp4。通过试片后再制作各 prompts 文件夹中其余六个动作。
5. 本地配置位于 C:/pet/ZZZ-Pet/workflow/gpt-dragon，试片配置为 human-pilot-jobs.json、chibi-pilot-jobs.json。每次输出使用新的 build 子目录。

试片命令（仓库根目录，替换为本机FFmpeg/FFprobe路径）：
python tools/build_flat2d_pack.py build --jobs workflow/gpt-dragon/human-pilot-jobs.json --output build/generated/dragon-human-pilot --pilot --ffmpeg <ffmpeg.exe> --ffprobe <ffprobe.exe>
Q版把human改为chibi。完整素材使用human-jobs.json/chibi-jobs.json，不加--pilot。输出 report.json 机械检查不代表视觉通过；两种形态验收后再合并到一个角色包。

人物版可见高度约408 DIP，Q版约204 DIP，与菜单缩放独立。原PNG和SVG试验稿保留。既有艾莲布提示词、视频、帧包与安装目录均未替换。
''',encoding='utf-8')
print('Jimeng kit:',OUT)
