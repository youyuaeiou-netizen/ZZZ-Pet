"""Create the flat 2D production kit, then build real PNG packs from supplied videos.

Inspired by PC2005-cloud/dsh-pet's prompt/chroma/normalization workflow;
implementation is local and independent. No model API, credentials or runtime codec.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import uuid

import numpy as np
from PIL import Image, ImageFilter

APP = Path(__file__).resolve().parents[1]
WORKSPACE = APP
SOURCE = "https://github.com/PC2005-cloud/dsh-pet"
SOURCE_REVISION = "9347fd172467fc12ea0d5aecd49809605e9bbeb8"
FPS = 12
SIZE = (616, 656)
# id, label, menu category, loop, triggers, weight, next, trim end, choreography
ACTIONS = [
    ("idle", "待机呼吸", "日常", True, [], 1, None, 3,
     "0–3秒：标准正面站立，身体仅轻微呼吸起伏，头发少量摆动；第3秒回到起始姿态。3–5秒继续同一节奏，禁止整体平移。"),
    ("blink", "眨眼", "日常", False, ["random"], 6, "idle", 3,
     "0–0.8秒保持站立；0.8–1.1秒眼睛闭合再睁开；1.1–3秒恢复标准站立。3–5秒静止呼吸；身体与发型不变。"),
    ("wave", "打招呼", "回应", False, ["click", "double_click", "random", "notification"], 2, "idle", 3,
     "0–0.6秒抬起一只短袖手臂；0.6–2.3秒小幅挥手两次，保持肩部连续；2.3–3秒放下手臂恢复标准站立。3–5秒保持站立。"),
    ("happy", "开心回应", "回应", False, ["click", "random"], 3, "idle", 3,
     "0–0.7秒露出笑容；0.7–2秒短臂小幅张开、身体轻微上下弹动；2–3秒恢复站立和平静表情。3–5秒待机，无额外特效。"),
    ("pet", "被抚摸", "回应", False, ["click"], 2, "idle", 3,
     "0–0.6秒头部轻微低下；0.6–2秒满足地眯眼，头部左右微倾，如被温柔抚摸；2–3秒恢复站立。3–5秒待机。画面不出现人的手。"),
    ("eat", "喂食", "日常", False, [], 1, "idle", 4,
     "0–0.6秒双短臂捧起一条小烤鱼，送到嘴边；烤鱼呈金黄褐色，保留清晰的鱼形和少量烤纹，大小适合双短手捧住，不遮挡双眼。0.6–2.8秒小口吃烤鱼，烤鱼随每次咬食逐渐变小，身体位置不变；2.8–4秒吃完，双手自然放下，烤鱼完全消失并回到标准站立。4–5秒待机。只出现烤鱼，不出现盘子、筷子、竹签、鱼骨、人的手、烟雾或火焰；食物不突然变成其他物品，嘴部不夸张变形。"),
    ("stretch", "伸懒腰", "日常", False, ["random"], 2, "idle", 3,
     "0–0.8秒短臂向上舒展；0.8–2秒轻轻伸懒腰，头部微仰；2–3秒放下双臂恢复站立。3–5秒待机，手臂不得变长。"),
    ("drag_hold", "拖起悬空", "日常", True, ["drag_start"], 1, None, 3,
     "全程为同一悬空姿态：短脚与短臂自然下垂，身体轻微周期摆动，疑惑表情。0–3秒完成一次完整循环；3–5秒继续同一摆动。脚底基准位置不漂移。"),
    ("put_down", "放下缓冲", "日常", False, ["drag_release"], 1, "idle", 2,
     "0秒从拖起悬空姿态开始；0–0.5秒短脚着地并轻微屈膝；0.5–1.5秒缓慢恢复标准站立；1.5–5秒站稳。角色本身不发生横向位移。"),
    ("sleep_enter", "戴眼罩入睡", "休息", False, ["idle_timeout"], 1, "sleep_idle", 4,
     "参考图片1为原站姿，图片2为同套戴眼罩站姿，图片3为同套戴眼罩坐睡姿；新姿态图确认后再生成。0–0.8秒：双短臂将浅灰眼罩从胸前轻轻戴到眼部；0.8–2.8秒：恢复垂手，戴眼罩站立保持2秒，复用原角色先戴眼罩再坐下的顺序；2.8–4秒：缓缓坐下，短脚朝前露出深灰脚底，手臂放松下垂；4–5秒：保持图片3的坐睡姿态。眼罩固定为两道闭眼弧线与白色小牙齿，禁止穿过头发、变色或露出红眼。脚底基准固定，不横移、不画文字。"),
    ("sleep_idle", "睡眠循环", "休息", True, [], 1, None, 3,
     "使用确认后的戴眼罩坐睡图作为唯一姿态参考。从第一帧到最后一帧保持同一坐姿，浅灰眼罩覆盖双眼，双短臂自然下垂、两只短脚朝前。0–3秒轻微呼吸并回到第一帧姿态；3–5秒继续同样呼吸。眼罩与脚底位置固定，不站起，不改变腿长，不画ZZZ或文字。"),
    ("wake", "醒来", "休息", False, [], 1, "idle", 3,
     "参考图片1为确认后的戴眼罩坐睡图，图片2为原站姿。0–0.8秒：保持坐姿，双短臂轻轻摘下眼罩，露出同一对红眼；0.8–2.7秒：缓缓站起，保持身体比例与脚底基准；2.7–3秒：双臂自然放下，恢复图片2站姿，眼罩从画面中自然收起；3–5秒：标准站立、轻微呼吸。不得硬切坐姿与站姿，不增加人的手。"),
    ("nod", "点头回应", "回应", False, [], 1, "idle", 3,
     "0–0.5秒：标准站立，目光朝前；0.5–1.8秒：头部小幅点头两次，发型与肩部连接稳定；1.8–3秒：恢复原头部位置与平静表情；3–5秒：保持站立。身体和脚不移动，不产生文字或符号。"),
    ("shake_head", "摇头回应", "回应", False, [], 1, "idle", 3,
     "0–0.5秒：标准站立；0.5–2秒：头部小幅左右摇动一次来回，保持双眼位置与发型一致；2–3秒：回到正面站姿；3–5秒：轻微呼吸。不要转身，不移动脚，不甩出头发或改变脸型。"),
    ("shy", "害羞回应", "回应", False, [], 1, "idle", 3,
     "0–0.6秒：脸颊增加浅淡红晕，头部轻微低下；0.6–2秒：双短臂在腹部前轻轻靠拢，红眼微微看向侧下方；2–3秒：放下短臂、恢复正面与原肤色；3–5秒：保持站立。不要遮住整张脸，不增加手指、爱心或粒子。"),
    ("look_around", "左右张望", "日常", False, [], 1, "idle", 3,
     "0–0.5秒：标准站立；0.5–1.3秒：目光和头部轻微转向画面左侧；1.3–2.1秒：轻微转向右侧；2.1–3秒：回到正面原姿态；3–5秒：保持站立。不转身、不走动，不改变角色尺度和身体方向。"),
]
PREFIX = """使用已确认的唯一2D艾莲布参考图。保留黑红头发、红眼、发夹、灰色鲨鱼服、白色牙齿领口、浅色椭圆腹部、短臂与短脚；眼睛闭合时仍保持同一角色身份。
画风为平面手绘Q版：清晰深灰描边、平涂色块、极少量硬边分区阴影。禁止写实材质、布偶纹理、3D渲染、体积渐变、复杂高光、景深和镜头运动。
视频16:9、5秒、建议1280×720或1920×1080，背景严格纯绿#00FF00，无地面、投影、杂物或字幕。仅喂食可出现指定食物，入睡与醒来可出现指定浅灰眼罩，其余动作不新增物件。
同一角色大小固定；标准站立头顶约20%画幅高度、脚底约85%，水平居中。所有肢体、头发和道具始终留在画内，各边至少10%安全留白。只改变动作所需部位，禁止角色变形、手臂增长、服饰改变、左右漂移或镜头缩放。
站立、坐睡和悬空分别使用稳定锚点；过渡片段必须连接指定的起止姿态。手臂与肩部相连，腹部轮廓连贯，不额外增加脸、手指、尾巴或配饰。
"""


def write_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def workspace_path(value: str) -> Path:
    path = Path(value)
    return path.resolve() if path.is_absolute() else (WORKSPACE / path).resolve()


def prepare(output: Path):
    output.mkdir(parents=True, exist_ok=False)
    jobs = []
    for action in ACTIONS:
        aid, name, _, _, _, _, _, end, choreography = action
        (output / f"{aid}.md").write_text(
            f"# {name}\n\n{PREFIX}\n\n## 本动作按秒安排\n\n{choreography}\n\n"
            f"文件名：`{aid}.mp4`。建议播放截取0–{end}秒；需按实际结果检查后调整。"
            "默认使用确认过的站立参考；坐睡与悬空循环还需同套已验收姿态参考。\n\n"
            f"参考项目：{SOURCE}（制作流程参考）。\n"
            "许可：提示词、动画和源视频允许开放及非商业使用，禁止商业使用。原作者 GitHub 地址署名要求只适用于基于上游成品的衍生、改版或换皮作品；独立角色和动作素材不适用该条件。\n",
            encoding="utf-8")
        jobs.append({"id": aid, "input": f"{aid}.mp4", "mode": "green", "start": 0, "end": end})
    write_json(output / "video-jobs.json", {
        "referenceStatus": "pending", "referenceImage": None,
        "poseReferences": {"stand_masked": {"status": "pending", "image": None},
                           "sit_masked": {"status": "pending", "image": None}},
        "sourceRoot": "workflow/source-videos", "jobs": jobs})
    (output / "README.md").write_text(
        f"# 2D艾莲布制作包\n\n状态：参考图与真实视频待提供；目标{len(ACTIONS)}个动作，默认每段5秒。没有可发布的新角色动画。\n\n"
        "1. 确认唯一站姿，填写video-jobs.json的referenceImage（工作区相对路径）和referenceStatus=approved；戴眼罩站姿、坐姿分别填入poseReferences并确认后设status=approved。\n"
        "2. 先生成idle.mp4、wave.mp4。每段使用同一参考图，视频放sourceRoot；不同角色姿态须保留同一比例。\n"
        "3. 使用各动作Markdown中的提示词，默认截取时间仅是制作建议；按视频实际动作调整start/end。\n"
        "4. 普通绿幕使用mode=green；手工透明MOV或VP9-alpha WebM使用mode=alpha，修改input扩展名。\n"
        "5. 在仓库根目录执行`python tools/build_flat2d_pack.py build --jobs workflow/video-jobs.json --output build/generated/ellen-flat2d --pilot`。CLI文件参数按显式路径解析；配置中的sourceRoot/referenceImage相对仓库根。\n"
        "6. 先确认待机、挥手预览和戴眼罩坐姿参考，再补齐其他视频。每次构建选择新的输出目录；`--pilot`只控制睡眠阈值和唤醒设置，不覆盖既有输出。\n\n"
        "工具统一从idle第一帧估计站立尺度与脚底，给全部片段使用同一个变换；不逐帧追踪裁切。"
        "其他视频若尺寸或位置漂移，应重新生成或在jobs的alignment中显式填写同一解码画布坐标的anchorX、anchorY、standingHeight。"
        "脚底应固定，站起坐下属于真实动作，不将每帧最低点吸附到地面。\n\n"
        "输出：616×656 RGBA PNG、12FPS、逻辑画布308×328、锚点154/315；原视频保留，连续相同帧合并。"
        "拒绝假透明、空帧、裁切、非法时间段与缺失输入；report.json中的视觉验收始终为pending，机械检查不等于视觉认可。"
        "失败输出保留错误报告，没有character.json，不进入应用。\n\n"
        "预览：`dotnet run --project src/DesktopPet/DesktopPet.csproj -- --preview-pack <输出目录父目录>`，"
        "预览目录需包含ellen-flat2d子目录；预览自动使用其中的preview-settings.json和.runtime隔离数据，不能同时传--data-dir。未确认前不设为默认。\n\n"
        f"参考：{SOURCE}，调研版本{SOURCE_REVISION}。代码参考遵守MIT版权声明；提示词、动画、源视频允许非商业使用，禁止商业使用。"
        "原作者GitHub地址署名要求仅适用于基于上游成品的衍生、改版或换皮作品；独立角色和动作素材不适用该条件。"
        "本工具未复制对方人物动画或视频，不移除平台要求保留的标识。\n",
        encoding="utf-8")


def key_green(rgba: np.ndarray) -> np.ndarray:
    rgb = rgba[..., :3].astype(np.float32) / 255
    hi, lo = rgb.max(axis=2), rgb.min(axis=2)
    delta = hi - lo
    safe = np.maximum(delta, 1e-6)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    hue = np.where(hi == r, ((g - b) / safe) % 6,
                   np.where(hi == g, (b - r) / safe + 2, (r - g) / safe + 4)) * 60
    sat = delta / np.maximum(hi, 1e-6)
    # Compressed, muted screens also contaminate dark contour pixels. A
    # brightness cutoff leaves those pixels as opaque green speckles.
    green = (hue >= 64) & (hue <= 176) & (sat >= .08)
    if green.mean() < .02:
        raise ValueError("No sufficiently large green background; use alpha input or repair the video")
    mask = Image.fromarray(np.where(green, 0, 255).astype(np.uint8))
    # Remove isolated codec pixels, then soften only the contour. Blur colors
    # with the same mask so newly transparent edges cannot retain screen RGB.
    clean = mask.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))
    solid = np.array(clean, dtype=np.float32) / 255
    soft = np.array(clean.filter(ImageFilter.GaussianBlur(.4)), dtype=np.float32) / 255
    result = rgba.copy()
    result[..., 3] = np.minimum(rgba[..., 3], np.rint(soft * 255).astype(np.uint8))
    for channel in range(3):
        weighted = Image.fromarray(np.rint(rgba[..., channel] * solid).astype(np.uint8))
        blurred = np.array(weighted.filter(ImageFilter.GaussianBlur(.4)), dtype=np.float32)
        result[..., channel] = np.rint(np.clip(blurred / np.maximum(soft, 1 / 255), 0, 255)).astype(np.uint8)
    edge = np.array(clean.filter(ImageFilter.MinFilter(5))) < 255
    result[..., 1] = np.where(edge, np.minimum(result[..., 1], np.maximum(result[..., 0], result[..., 2])), result[..., 1])
    result[result[..., 3] == 0] = 0
    return result


def bbox(rgba: np.ndarray):
    ys, xs = np.where(rgba[..., 3] > 12)
    if len(xs) == 0:
        raise ValueError("Empty foreground frame")
    if not np.any(rgba[..., 3] < 100) or not np.any(rgba[..., 3] > 200):
        raise ValueError("Frame has no real transparent and opaque pixels")
    return int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1


def decode(job, root: Path, cache: Path, ffmpeg: str, ffprobe: str):
    src = (root / job["input"]).resolve()
    if not src.is_relative_to(root) or not src.is_file():
        raise ValueError(f"Missing input or input outside sourceRoot: {job['input']}")
    start, end = float(job["start"]), float(job["end"])
    if not all(math.isfinite(v) for v in (start, end)) or start < 0 or end <= start or end - start > 30:
        raise ValueError(f"Invalid trim: {start}–{end}")
    probe = subprocess.run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
                            "stream=width,height:format=duration", "-of", "json", str(src)],
                           check=True, capture_output=True, encoding="utf-8")
    info = json.loads(probe.stdout)
    stream = info["streams"][0]
    if abs(stream["width"] / stream["height"] - 16 / 9) > .03:
        raise ValueError("Input must use the common 16:9 video canvas")
    if end > float(info["format"]["duration"]) + .1:
        raise ValueError("Trim exceeds source duration")
    if job["mode"] not in ("green", "alpha"):
        raise ValueError("mode must be green or alpha")
    target = cache / job["id"]
    target.mkdir()
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin"]
    if job["mode"] == "alpha" and src.suffix.lower() == ".webm":
        cmd += ["-c:v", "libvpx-vp9"]
    cmd += ["-i", str(src), "-ss", str(start), "-t", str(end - start), "-an", "-vf",
            f"scale=1280:720,fps={FPS}", "-pix_fmt", "rgba", str(target / "%05d.png")]
    subprocess.run(cmd, check=True, capture_output=True)
    paths = sorted(target.glob("*.png"))
    if not paths:
        raise ValueError("No frames decoded")
    boxes = []
    for path in paths:
        with Image.open(path) as image:
            rgba = np.array(image.convert("RGBA"))
        if job["mode"] == "green":
            rgba = key_green(rgba)
        box = bbox(rgba)
        if box[0] <= 1 or box[1] <= 1 or box[2] >= 1279 or box[3] >= 719:
            raise ValueError(f"Foreground touches source edge: {path.name}")
        boxes.append(box)
        Image.fromarray(rgba).save(path)
    return paths, boxes, {"input": job["input"], "sha256": hashlib.sha256(src.read_bytes()).hexdigest(),
                          "start": start, "end": end, "decodedFrames": len(paths), "mode": job["mode"]}


def normalize(paths, alignment, output: Path):
    height = float(alignment["standingHeight"])
    ax, ay = float(alignment["anchorX"]), float(alignment["anchorY"])
    if not all(math.isfinite(v) for v in (height, ax, ay)) or height <= 0 or not 0 <= ax <= 1280 or not 0 <= ay <= 720:
        raise ValueError("Invalid alignment coordinates")
    scale = 540 / height
    resized = (round(1280 * scale), round(720 * scale))
    offset = (round(308 - ax * scale), round(630 - ay * scale))
    if max(resized) > 8192:
        raise ValueError("Alignment scale is too large")
    frames, previous, union = [], None, [SIZE[0], SIZE[1], 0, 0]
    output.mkdir(parents=True)
    for index, path in enumerate(paths):
        with Image.open(path) as source:
            source_rgba = np.array(source.convert("RGBA"))
            box = bbox(source_rgba)
            transformed = (box[0] * scale + offset[0], box[1] * scale + offset[1],
                           box[2] * scale + offset[0], box[3] * scale + offset[1])
            if transformed[0] < 2 or transformed[1] < 2 or transformed[2] > 614 or transformed[3] > 654:
                raise ValueError(f"Action would be clipped on shared canvas: frame {index}")
            canvas = Image.new("RGBA", SIZE)
            canvas.paste(source.resize(resized, Image.Resampling.LANCZOS), offset)
        rgba = np.array(canvas)
        bound = bbox(rgba)
        union = [min(union[0], bound[0]), min(union[1], bound[1]), max(union[2], bound[2]), max(union[3], bound[3])]
        duration = round((index + 1) * 1000 / FPS) - round(index * 1000 / FPS)
        pixels = rgba.tobytes()
        if previous == pixels:
            frames[-1]["durationMs"] += duration
        else:
            filename = f"{len(frames):04d}.png"
            canvas.save(output / filename)
            frames.append({"path": f"animations/{output.name}/{filename}", "durationMs": duration})
            previous = pixels
    return frames, union


def manifest_actions(frame_map):
    result = []
    interrupt = ["click", "double_click", "drag_start", "notification", "idle_timeout"]
    for aid, name, category, loop, triggers, weight, nxt, _, _ in ACTIONS:
        if aid not in frame_map:
            continue
        action = {"id": aid, "displayName": name, "category": category, "frames": frame_map[aid],
                  "triggers": triggers, "weight": weight, "priority": 0 if aid == "idle" else 10,
                  "loop": loop, "cooldownMs": 8000, "interruptibleBy": interrupt}
        if aid == "idle":
            action["interruptibleBy"] = interrupt + ["random"]
        if nxt in frame_map:
            action["nextActionId"] = nxt
        if aid == "sleep_idle":
            action["showSleepIndicator"] = True
        result.append(action)
    return result


def build(jobs_path: Path, output: Path, pilot: bool, ffmpeg: str, ffprobe: str):
    build_root = (WORKSPACE / "build").resolve()
    if not output.resolve().is_relative_to(build_root):
        raise ValueError("Build outputs must stay inside the repository build directory")
    config = json.loads(jobs_path.read_text(encoding="utf-8-sig"))
    if config.get("referenceStatus") != "approved" or not config.get("referenceImage"):
        raise ValueError("Reference is pending: confirm candidate first and set approved referenceImage")
    reference = workspace_path(config["referenceImage"])
    if not reference.is_file():
        raise ValueError("Approved reference image is missing")
    requested = {str(job.get("id", "")) for job in config.get("jobs", [])}
    known = {action[0] for action in ACTIONS}
    if not {"idle", "wave"}.issubset(requested) or not requested.issubset(known):
        raise ValueError("Jobs must contain idle and wave and use known action ids")
    if not pilot:
        if requested & {"sleep_enter", "sleep_idle", "wake"}:
            for pose_id in ("stand_masked", "sit_masked"):
                pose = config.get("poseReferences", {}).get(pose_id, {})
                if pose.get("status") != "approved" or not pose.get("image") or not workspace_path(pose["image"]).is_file():
                    raise ValueError(f"Sleep pose reference pending or missing: {pose_id}")
    root = workspace_path(config["sourceRoot"])
    ids = requested
    jobs = [j for j in config["jobs"] if j["id"] in ids]
    if {j["id"] for j in jobs} != ids or len(jobs) != len(ids):
        raise ValueError("Missing or duplicate jobs")
    if output.exists():
        raise FileExistsError("Choose a new output directory; existing packs are never overwritten")
    for job in jobs:
        if not (root / job["input"]).is_file():
            raise ValueError(f"Missing input: {job['input']}")
    output.mkdir(parents=True)
    cache = WORKSPACE / "build/.cache/flat2d" / uuid.uuid4().hex
    cache.mkdir(parents=True)
    report = {"status": "processing", "visualAcceptance": "pending", "pilot": pilot,
              "sourceProject": SOURCE, "sourceRevision": SOURCE_REVISION,
              "referenceSHA256": hashlib.sha256(reference.read_bytes()).hexdigest(),
              "pendingActions": sorted({a[0] for a in ACTIONS} - ids), "actions": [], "cache": str(cache)}
    try:
        decoded = {}
        for job in jobs:
            decoded[job["id"]] = decode(job, root, cache, ffmpeg, ffprobe)
        first = decoded["idle"][1][0]
        alignment = config.get("alignment") or {"anchorX": (first[0] + first[2]) / 2,
                                               "anchorY": first[3], "standingHeight": first[3] - first[1]}
        report["alignment"] = alignment
        frame_map = {}
        for job in jobs:
            paths, _, detail = decoded[job["id"]]
            frames, union = normalize(paths, job.get("alignment") or alignment, output / "animations" / job["id"])
            frame_map[job["id"]] = frames
            report["actions"].append({"id": job["id"], **detail, "uniqueFrames": len(frames),
                                       "durationMs": sum(f["durationMs"] for f in frames), "contentBounds": union})
        (output / "masters").mkdir()
        shutil.copyfile(reference, output / "masters" / "approved-reference.png")
        write_json(output / "character.json", {
            "schemaVersion": 1, "id": "ellen-flat2d", "name": "艾莲布 · 2D手绘预览",
            "packVersion": "0.1.0", "minimumAppVersion": "0.3.1", "immediateClick": True,
            "defaultActionId": "idle", "defaultScale": .75, "canvasWidth": 308, "canvasHeight": 328,
            "anchorX": 154, "anchorY": 315, "randomIntervalMs": 12000,
            "sleepThresholdMs": 0 if pilot else 60000, "wakeActionId": None if pilot else "wake",
            "avoidRepeatedActions": True, "actions": manifest_actions(frame_map)})
        report["status"] = "mechanical_checks_passed_visual_pending"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = str(error)
        raise
    finally:
        write_json(output / "report.json", report)
    (output / "SOURCE.md").write_text(
        f"制作流程参考：{SOURCE}\n\n"
        "提示词、源视频与动画素材允许开放和非商业使用，禁止商业使用。只有基于上游成品制作衍生、改版或换皮作品时，"
        "才需按其许可在介绍、展示或分发处附原作者 GitHub 地址；独立角色和动作素材不适用该条件。\n"
        "视觉验收待完成，本包不自动安装或更改默认角色。\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    init = commands.add_parser("prepare")
    init.add_argument("--output", type=Path, required=True)
    export = commands.add_parser("build")
    export.add_argument("--jobs", type=Path, required=True)
    export.add_argument("--output", type=Path, required=True)
    export.add_argument("--pilot", action="store_true")
    export.add_argument("--ffmpeg", default="ffmpeg")
    export.add_argument("--ffprobe", default="ffprobe")
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.output.resolve())
    else:
        build(args.jobs.resolve(), args.output.resolve(), args.pilot, args.ffmpeg, args.ffprobe)


if __name__ == "__main__":
    main()
