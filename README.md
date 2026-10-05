# ZZZ-Pet

Windows 桌面 Q 宠，当前便携版本为 **0.3.1-beta.3**。它使用用户选定的二维手绘角色和 11 组透明 PNG 帧，支持待机、挥手、入睡、坐睡、醒来、开心、被抚摸、喂食、伸懒腰、拖起和放下。

本仓库包含程序源码、用户选定的二维角色帧包、生成这些动作所用的 11 段视频、参考图、逐动作提示词和帧处理工具。ZIP 和源码均只收录当前二维角色。

## 下载与运行

从 [0.3.1-beta.3 ZIP 发布页](https://github.com/youyuaeiou-netizen/ZZZ-Pet/releases/tag/v0.3.1-beta.3) 下载 `DesktopPet-0.3.1-beta.3-win-x64.zip`，退出旧桌宠，解压到一个新文件夹后运行 `DesktopPet.exe` 或 `Start-2D-Pet.cmd`。这是自包含包，适合在 Windows 上直接解压使用。发布页附 SHA-256 校验文件。

便携包只包含批准的 2D 角色。请完整解压到一个新文件夹；程序会保留已有用户数据。

## 从源码构建

需要 Windows、.NET 10 SDK。发布 ZIP 已包含当前角色帧；要从视频重新生成帧，另需 Python 3、NumPy、Pillow、FFmpeg 和 ffprobe。

```powershell
dotnet run --project tests/DesktopPet.Smoke/DesktopPet.Smoke.csproj -c Release
dotnet publish src/DesktopPet/DesktopPet.csproj -c Release -r win-x64 --self-contained true --output build/publish
```

制作只包含批准 2D 角色的便携 ZIP：`pwsh -NoLogo -NoProfile -File tools/publish_portable.ps1`。对外分发使用便携打包脚本，并运行包内启动验收。

动画处理器的合约测试：`python tests/test_flat2d_pipeline.py`。监控与搜索专项测试需要各自的隔离目录，命令见 `workflow/制作工作流.md`。

按本仓库素材重建 11 动作帧包：

```powershell
python -m pip install -r workflow/requirements-animation.txt
pwsh -NoProfile -File workflow/rebuild-flat2d.ps1
```

完整流程、视频生成提示词、文件映射、裁切时长和验收步骤见 [制作工作流记录](workflow/制作工作流.md)。动作提示词及来源视频允许非商业使用；商用限制和二创署名条件见 [素材许可说明](workflow/LICENSE-ASSETS.md)。
`workflow/source-materials.sha256` 列出提示词、参考图和源视频的校验值；可用 `python tools/create_source_manifest.py --check` 验证。

根目录 [LICENSE](LICENSE) 的 MIT 条款仅适用于程序代码；动画、美术参考、提示词和源视频按各自的素材许可说明使用。

## 验收边界

本次发布使用用户选定的二维角色帧，默认启动即显示该角色。角色包机械校验与动作配置可复核；真实桌面 DPI 下的最终观感仍需使用者确认。重新向生成式视频服务提交同样提示词可能得到不同画面；仓库保留源视频和现有帧包，便于重建与对照。
