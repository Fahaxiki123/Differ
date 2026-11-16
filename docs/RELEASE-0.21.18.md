# 0.21.18 安装包发布记录

2026-11-13。用户在完成开发版初步试用后，明确要求构建安装包、发布到 GitHub，并在首页提供最新下载入口，同时保留现用 SVN 安装与绑定。现已发布 **0.21.18 Preview**，不替换稳定通道的 Latest，不删除历史版本。

[下载 Windows x64 安装包](https://github.com/Fahaxiki123/Differ/releases/download/v0.21.18/Differ-0.21.18-win-x64-Setup.exe) · [Release、校验值与说明](https://github.com/Fahaxiki123/Differ/releases/tag/v0.21.18) · [源码标签](https://github.com/Fahaxiki123/Differ/tree/v0.21.18)

## 产物身份

| 项目 | 结果 |
|---|---|
| 版本 / 通道 | 0.21.18 / Preview，GitHub Draft=false |
| 源码与标签 | `v0.21.18` → `735fea2218b8adfa25e3d1d597aada69002481a2` |
| 安装包文件名 | `Differ-0.21.18-win-x64-Setup.exe` |
| 文件版本 | 0.21.18.0 |
| 大小 | 277,451,784 字节，约 264.6 MiB |
| .NET 运行组件 | Microsoft.NETCore.App 与 Microsoft.WindowsDesktop.App 10.0.12，自包含 |
| 安装包数字签名 | 未签名 |

安装包 SHA-256：

```text
09EE1A5141D925C5F16928145BF143E49E83BFEAD06F69078E92466A84032DA7
```

Release 包含安装 EXE、同名 `.sha256`、`release-manifest.json`、`RELEASE-NOTES.txt` 四件附件。发布脚本逐项核对远端大小与 SHA-256 后解除草稿状态；日志结尾为 `Draft=False`、`AssetsVerified=4`。首页下载链接明确指向 `v0.21.18`，不使用仍属于稳定通道的 `/releases/latest/download`。无凭据公开下载探测也已通过：HEAD返回200、Content-Length为277,451,784，最终落在 `release-assets.githubusercontent.com`；见 `public-download-check.json`。

本版功能与前台画面见 [0.21.18 UI 交付记录](UI-0.21.18.md)：主题接缝和语法色残留修复、完整窗口按钮手势、精确查找与另存目标提示均包含在该源码标签中。

## 构建与包内验证

证据根目录为 `D:\项目归档\Differ\2026-11-13-release-0.21.18`。

| 验证 | 结果与证据 |
|---|---|
| Release 构建与格式 | 通过，`verification\summary.json` 的 build、format 退出码均为 0 |
| 本轮常规回归 | 711 通过、0 失败、2 项显式取证/前台测试未启用；见 `verification\test-summary.json` |
| 前次完整 UI 回归 | 713/713、0 跳过，两个显式开关均开启；仍以 `2026-11-13-caption-finish\verification-accepted` 为证，不把本轮常规结果冒充 713 项全运行 |
| 安装器编译 | NSIS 3.12 离线打包通过，清单和日志位于 `installer-logs` |
| 非执行包校验 | 7-Zip 归档测试通过；598 个归档文件的路径与完整布局通过，592 个运行文件逐项大小/SHA-256 与构建清单、发布目录一致；见 `package-verification-final.json` |
| 版本与源码身份 | 解包后的 EXE/DLL 均为 `0.21.18+735fea2218b8adfa25e3d1d597aada69002481a2`，AMD64 宿主及自包含 .NET/WPF 配置通过 |
| 离线依赖 | Editor 文件齐全；内含 WebView2 安装器及应用本地 VC Runtime 的锁定哈希与微软签名通过 |
| 只读启动探针 | 正式发布程序执行 `--integration-status`，0.27 秒返回 4，表示该新路径未绑定；没有请求安装或恢复，见 `runtime-status-probe.json` |
| GitHub 发布 | Preview 已公开发布，四件附件验证通过；见 `github-release-publish.log` |

本轮跳过的是 `CapturesComparisonWindowCaptionTransitionEvidenceWhenConfigured` 与 `ForegroundCaptionCommandsMaximizeRestoreAndMinimize`，不是功能失败。跳过数按 TRX 中实际 `UnitTestResult outcome=NotExecuted` 统计；TRX 的汇总 counters 在本轮错误显示未执行数为 0。

最初的包预检拒绝了 7-Zip 对 NSIS 生成的 `Uninstall.exe` 所报告的空大小，尚未开始解包；这是外部验收脚本的解析限制，不是安装包损坏。随后只对这一固定生成条目允许列表大小未知，解包后仍要求其大小大于 0 且小于 8 MiB、总解包量小于 4 GiB；实际为 424,221 字节。592 个运行文件的长度与 SHA-256 条件没有放宽。初次失败 `package-verification.json` 与最终通过记录均保留。

工程验证保留既有依赖审计，仅在进程内将 NU1902/NU1903 按警告处理。发布脚本的还原调用沿用既有 `NuGetAudit=false`；本轮没有更改依赖版本，也不把打包成功表述为默认无告警构建或相关漏洞已修复。

## 安装与验证边界

- 安装包内含 .NET 运行组件和 WebView2 离线安装程序，不要求预装 .NET SDK 或 Office。TortoiseSVN 单独提供。
- SVN 绑定默认不勾选，只有用户明确选择后才备份并修改配置。已有安装按登记的原目录升级，不能把 `/D` 当成与现用安装隔离的方式。
- 本轮没有在当前用户下运行 Setup 或卸载程序，没有执行真实安装、升级或卸载。只读状态探针与非执行解包不等于这些流程已验收。
- Windows 10、干净机、混合/高系统 DPI 和长期稳定性未完成本轮实机验证；前次 150% 阅读缩放不等同于系统 DPI 验证。
- 主题过渡的短时低对比及跨 WPF/WebView2 非零相位差仍按 UI 交付记录披露。

## 现用安装保护与清理

`final-protection.json` 为 Success=true：现用 `D:\Differ` 仍为 0.17.1，**592 个已安装文件的 SHA-256 全部未变**；SVN 全局与高级绑定、卸载注册信息和固定开发预览均未改变。安装器未被执行。

发布后清理删除 23 个可重建目标，共 637,261,120 字节，包括本轮编译输出和非执行解包暂存。`cleanup-final-check.json` 为 Clean=true、Targets=0、Unclassified=0。保留源码、原始用例、许可、打包工具和离线依赖、固定 `artifacts\publish\Differ-win-x64`、最新 `artifacts\installers` 及项目外的版本包与核验记录；这些分发产物不构成第二套开发仓库或隔离预览。
