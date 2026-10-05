# 固定硬件依赖来源

以下组件未修改二进制。运行包中的 `third_party/dependency-notices/packages.json` 记录实际版本、NuGet 原始元数据、版权、许可表达式与仓库提交。自包含 .NET 运行时也单独记录，原始许可与第三方声明不省略。

| 组件 | 版本 / 许可 | 对应源码 |
|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 / MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 |
| BlackSharp.Core | 1.0.7 / MPL-2.0 | https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0 |
| DiskInfoToolkit | 1.1.2 / MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea |
| RAMSPDToolkit-NDD | 1.4.2 / MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce |
| HidSharp | 2.6.4 / Apache-2.0 | https://www.nuget.org/packages/HidSharp/2.6.4 |
| Mono.Posix.NETStandard | 1.0.0 / NuGet 链接指向 Mono LICENSE | https://www.nuget.org/packages/Mono.Posix.NETStandard/1.0.0 |

`MPL-2.0.txt` 为固定 LHM 提交的原始 LICENSE。`HidSharp-2.6.4-LICENSE.txt` 来自实际 NuGet 包，包含原始版权与完整 Apache 2.0 文本。

`PawnIo-modules-LGPL-2.1.txt` 来自该 LHM 提交的 `LibreHardwareMonitorLib/Resources/PawnIo/COPYING`。这覆盖库内置硬件模块的许可文本，不能替代对应模块源码、构建和可替换性核验。Pet 没有附带或安装 PawnIO 驱动，不能因此忽略库内嵌模块的分发义务。

固定 LiteMonitor README 的 MIT 声明和 Diorser 作者归属已核验，保留原文和标准 MIT 正文，不因没有独立 LICENSE 文件而否认该声明。Mono 包原始 NuGet 版权与其许可链接所指全文已保存；不声称知道其精确历史构建提交。

`sources/` 随包提供五套对应源码归档，固定提交、原始归档哈希、筛除的工具二进制以及生成归档哈希写入 `source-receipt.json`。LHM 的 12 个内嵌模块与官方 PawnIO.Modules **0.2.2** 发布文件逐一一致，对应源码固定为 `e12a858d952461ee2e919897cacbff7f905fe370`。实际部署 DLL 的资源哈希也已比对；原始 LHM README 中的 0.1.6 是过时说明。源归档保留全部模块源文件、include、COPYING 和构建工作流，不附带驱动、安装器或编译器 RPM。

RAMSPD 使用不带驱动的 NDD 包，实际 DLL 无驱动资源；源码中也排除 WinRing0 二进制及压缩资源，重建必须使用 Release_NDD。修改和替换步骤见 `BUILDING.md`。离线核验脚本 `tools/verify_distribution.py` 在打包前后检查声明、固定源码和哈希；失败拒绝生成分享包。分发核验与跨机、硬件和视觉验收分别记录。
