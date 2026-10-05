# 对应源码、修改与替换

本目录 `source-receipt.json` 固定下载地址、提交、归档 SHA-256 和筛除的工具二进制。五套源码归档随安装包、便携包和 Pet 源码包提供；并非要求使用者另行取得源码。无需为了正常使用 Pet 安装这些构建工具。

- `sources/LibreHardwareMonitor-source.zip`：实际 NuGet 0.9.6 的仓库提交，包含库源码、构建项目、原始 MPL 文本及内嵌模块。去除上游 PawnIO 安装器，不改动源码与模块。
- `sources/BlackSharp-source.zip`、`DiskInfoToolkit-source.zip`、`RAMSPDToolkit-source.zip`：实际包元数据对应的 MPL 源码提交与构建文件；保留其中原始版权声明和许可。RAMSPD 使用 **Release_NDD** 配置，与实际不含驱动的 `RAMSPDToolkit-NDD` 包一致；归档不含 WinRing0 文件及压缩驱动／模块，禁止改用会包含它们的普通 Release 配置构建分享包。
- `sources/PawnIO.Modules-source.zip`：全部 12 个内嵌模块对应的官方 0.2.2 源码、include 文件、COPYING 与构建工作流。每个发布文件与实际 LHM 资源的 SHA-256 都已比对。LHM 的 README 仍写 0.1.6，不能据此确定实际二进制版本。

## 修改库和 Pet

Pet 自有代码 MIT 开源；允许为修改库而调试、修改和重新构建，不附加禁止逆向调试这类修改的条款。硬件库以单独 `monitor/LibreHardwareMonitorLib.dll` 加载，未合并进 Pet 主程序。保留原始组件声明与源码，不把 LGPL 或 MPL 组件改标为 MIT。

解压 Pet 源码和 LHM 源码，使用对应源码项目构建修改后的库。库有自己的 NuGet 依赖与目标框架要求，按其项目文件恢复并构建 `LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj`。将 Pet 后台项目 `src/DesktopPet.MonitorWorker/DesktopPet.MonitorWorker.csproj` 中 `LibreHardwareMonitorLib` 的 PackageReference 换为修改库项目的 ProjectReference，然后依照 Pet [开源与构建](../../docs/开源与构建.md) 编译后台及主程序。保持所需依赖可用；不要直接替换用户正在运行的库。正式发布脚本核验固定二进制和源码，本地修改版应独立记录新的来源、哈希和测试结果。

## 构建内嵌模块

源码归档保留 `.github/workflows/ci.yml` 的原始构建命令：

```sh
for f in ./*.p; do
  [ -f "$f" ] && pawncc "$f" '-iinclude' '-C64' '-;+' '-(+' '-p'
done
```

上游使用 Oracle Linux 9 与仓库 `_pawn/` 下的 Pawn 编译器 RPM。为了不捆绑另一个未经分发核验的工具二进制，源码归档不带两个 RPM；开发者可从记录的同一上游提交取得编译工具并依其许可使用。Pet 不下载、安装或执行它。编译产物需按上游模块格式及驱动要求使用，不能声称自行编译的未签名模块可直接由官方签名校验驱动加载，也不能伪造签名。用户可以修改并重建加载模块的 LHM 库；Pet 的只读基础采集不依赖这些模块。

## Mono.Posix 与运行时

`Mono-Posix-LICENSE.txt` 是实际 NuGet 包许可链接重定向到的 Mono LICENSE 全文的固定快照。原包声明作者 Microsoft、版权 `© Microsoft Corporation. All rights reserved.`；实际原始 nuspec 随运行包保存。该快照不是关于 2018 年二进制构建提交的声明；其对应源码的精确历史提交未作为分发条件或已核验事实使用。Mono 文本规定运行时和类库通常采用 MIT，部分第三方代码使用 BSD；全文保留。

实际 .NETCore 与 WindowsDesktop 自包含运行时的原始许可、第三方声明以及全部 NuGet 元数据也随运行包提供。无需用户另装 .NET、驱动或 SDK。
