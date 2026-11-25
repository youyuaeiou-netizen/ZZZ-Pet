# 人物版视频动画

源视频：`C:\Users\h\Desktop\视频\人物`，8 段原视频只读保留。

使用本地现有 Python/OpenCV/Pillow 环境运行：

```powershell
& 'C:\Users\h\AppData\Local\Codex\local-mp4-analysis\.venv\Scripts\python.exe' tools/build_dragon_human_video.py --source 'C:\Users\h\Desktop\视频\人物' --output build/generated/dragon-human-video-new
```

输出目录必须为新的 build 子目录。该工具针对本套正面人物素材：24 FPS 透明 PNG，900×1000 像素画布，逻辑画布 450×500 DIP，脚底锚点 (225,480)，站立可见高度约408 DIP。扩大透明留白容纳坐睡展开的裙摆与翅膀，不逐帧改变坐下与站起的高度。

处理包括：抠绿与去绿边；以待机的前景 RGB 分位数曲线校正颜色与亮度；纠正拖起视频的镜头放大；循环选择相近首尾帧；动作首尾使用6帧预乘 Alpha 过渡；裙摆空隙使用单独的连续轮廓遮罩步骤，避免逐帧颜色判断带来的闪烁。

入睡、醒来、比心、喂食和放下保留5秒。待机、拖起与睡眠使用素材中选出的循环区间。原视频的服饰线条与形状存在小幅生成变化，调色与过渡不能重建缺失细节；也不将透明帧报告视为真人交互验收。

输出含角色清单、逐帧报告、深浅背景联系图和连续播放视频。人物版包含8个真实视频动作；Q版沿用原姿势素材，只补齐“放下”“入睡”的动作映射，以满足同一角色两种形态的动作ID兼容要求。

## 裙摆与双脚空隙

对新生成的基准帧包运行：

```powershell
& 'C:\Users\h\AppData\Local\Codex\local-mp4-analysis\.venv\Scripts\python.exe' tools/refine_dragon_human_matte.py build/generated/dragon-human-video-new
```

工具基于明确的站立、悬空与落地参考轮廓，使用反向光流跟踪遮罩。仅在参考帧划分需透明的区域，并保护粉色腿部、鞋子和浅色前裙；相邻遮罩投影到当前帧后平滑，再清除小于80像素的孤立碎片。输出 refined-matte/animations，不直接覆盖基准帧。检查生成的 refined-matte-review.jpg 和连续播放后，再将其中的 animations 覆盖角色包的 animations。坐姿不使用站立空隙遮罩。循环和相邻动作端点保持一致。

2026-10-12 修订包为 `build/generated/dragon-human-clean-live-20261012/gpt-dragon`，清单版本2.0.1，806个人物帧，已同步到仓库角色目录与 `C:\pet\DesktopPet\characters\gpt-dragon`。修订包的 review.mp4 对应当前资源；早期 dragon-human-video-20261012 与 dragon-human-live-20261012 仍含已被否定的透明区域处理，不用于验收。838项角色规则与资源检查通过。完整桌面拖拽与下落交互的视觉验收仍需复核。
