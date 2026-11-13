# 0.21.18 主题与窗口按钮收尾

2026-11-13。用户要求接手 Gemini 未完成的 0.21.18，完成主题切换、窗口按钮、回归与前台验收，再更新唯一开发预览并清除旧运行副本。本文记录接续结果；[接手前审查](UI-0.21.18-review.md)保留当时 34 项失败和缺正文截图，不改写为已通过。

**已更新唯一开发版至0.21.18。**最终源码的Release构建、713项测试、格式检查和本机前台验收通过；固定入口已核对，旧预览已删除，项目清理检查通过。两处任务缓存（原约22.25 MiB）已由用户手动删除，代理随后核对缓存路径、唯一开发版及现用SVN安装和绑定，均通过。

2026-11-13，用户完成初步试用，反馈暂未发现问题，并要求将当前开发版本提交到GitHub。在确认仓库已为公开状态后，用户明确授权将源码提交 `2951bf5` 推送到 `Fahaxiki123/Differ` 的 `codex/ui-system-phase1` 分支。

## 修复内容

- 标题栏采用全宽不透明 WPF 底板，三个窗口按钮与背景、分隔线、雾光在同一视觉树绘制；取消右侧 156 DIP 透明区及动画中的逐帧 DWM 标题栏着色，解决原固定矩形边界。保留非客户区命中、Windows 11 贴靠菜单和系统窗口操作。
- 补齐按钮完整手势：按住期间捕获鼠标，移出暂不触发，移回原按钮释放只执行一次；外部释放、捕获丢失、失焦、取消、尺寸/DPI变化和关闭均清理状态。不会误释放另一个窗口持有的捕获。
- 主题继续使用同一准备握手、开始纪元、680 ms 曲线与主题入口。WPF 在本地绘制帧推进底色和雾光；浏览器调色板直接提交同规则 RGBA 插值，减少等待跨进程回传及暂停 CSS 动画寻位造成的相位差。
- 语法色以稳定 `mtkN` 类参与临时颜色桥接，覆盖 Monaco 重绘后新建的可见 span；快速反向从当前类色接续，结束、换文档与即时换色清理规则。业务差异色保持优先，不访问 Monaco 私有服务、不重建文档模型或改变撤销历史。
- 修正概览结果失效与旧异步任务取消的通知次序：先发布不可用状态再取消旧任务，避免旧任务同步完成时看到仍有效的差异块。
- 取证测试必须等待真实 WebView2、正文版本、字号与行高就绪。真实窗口状态测试采用显式前台开关，常规回归不把离屏窗口最大化到桌面。开发预览使用带完整清单、占用与重解析点检查、失败恢复的分阶段更新脚本。

## 证据与测试条件

证据根目录为 `D:\项目归档\Differ\2026-11-13-caption-finish`。固定合成输入 `fixtures\base.lua`、`fixtures\mine.lua` 共 180 行，包含中文超长行和 BASE 独有内容，输入校验值见 `input-hashes.json`。只在独立结果文件上验证保存，没有修改原始输入或 `测试用例`。

测试环境使用 .NET SDK 10.0.401 与 WebView2 Runtime 157.0.4341.46。本轮保留 NuGet 审计，仅在验证进程设置 `WarningsNotAsErrors=NU1902;NU1903`，使既有传递依赖告警继续显示而不阻断功能验证；工程配置、依赖版本和审计开关未改，不能称为默认无告警构建。

| 验证 | 当前结果与证据 |
|---|---|
| 最终 Release build/test/format | `verification-accepted\summary.json`各步骤退出0；11份TRX共713/713通过、0失败、0跳过，其中App345项 |
| 窗口按钮与结果失效专项 | 真实 HWND 手势、取消/捕获、编辑器就绪及异步失效次序断言均纳入最终全量通过 |
| 语法色真实浏览器回归 | 3/3通过并纳入最终全量；重新查询已连接的可见 `return` 和普通标识符，覆盖双向、快速反向、DOM 重绘、业务差异优先及模型/历史保持；独立记录为 `direct-palette\class-priority-results` |
| 预览更新脚本隔离回归 | 18/18，通过；见 `delivery-script-tests\run-04-payload\summary.json`，覆盖完整载荷身份和回滚边界 |
| 发布后目录检查 | `cleanup-final-apply.json`清理22个构建输出目标、约206.66 MiB；`cleanup-final-check.json`为Clean=true、Targets=0、Unclassified=0 |

本次最终全量同时开启受控截图取证（`DIFFER_CAPTION_EVIDENCE_DIR`、`DIFFER_CAPTION_EVIDENCE_STAGE`）与实际窗口状态测试（`DIFFER_CAPTION_FOREGROUND_TESTS=1`），因此713项中无跳过。机器可读汇总为 `verification-accepted\test-summary.json`。常规未启用时，对应专项按设计跳过；设置及入口见 [构建说明](BUILDING.md)。

## 前台操作与同条件画面

自然播放图来自实际前台窗口捕获。720×620 和 1500×860 使用相同输入、初始行位置和 100% 阅读缩放；每次点击后连续取样。`observedMs` 是输入工具返回后的观察时间，不能当作动画的精确 elapsed，也不能把不同播放的同编号图片视为同一动画帧。

| 场景 | 基线与本轮证据 |
|---|---|
| 720×620 浅深稳定态 | [浅色改前](D:/项目归档/Differ/2026-11-13-caption-finish/before-720-light.jpg) / [改后](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-720-light.jpg)，[深色改前](D:/项目归档/Differ/2026-11-13-caption-finish/before-720-dark.jpg) / [改后](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-720-dark.jpg)；改后来自最终固定入口 |
| 1500×860 浅深稳定态 | [浅色改前](D:/项目归档/Differ/2026-11-13-caption-finish/before-1500-light.jpg) / [改后](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-1500-light.jpg)，[深色改前](D:/项目归档/Differ/2026-11-13-caption-finish/before-1500-dark.jpg) / [改后](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-1500-dark.jpg)；改后来自与固定入口完整载荷相同的候选 |
| 双向自然播放 | 基线 `before-720/1500-l2d-*`、`before-720/1500-d2l-*`；最终 `accepted-720-l2d/d2l-0..5.jpg`、`accepted-1500-l2d/d2l-0..6.jpg`；帧清单为 `baseline-frames.json`、`accepted-frames.json` |
| 150% 阅读缩放与长行 | [浅色](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-720-zoom150-light.jpg)、[深色](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-720-zoom150-dark.jpg)及双向0..5帧已前台验证；[120行长行查找](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-720-zoom150-long-find.jpg)显示TARGET_732命中、1/1计数、水平可见及左右对齐 |
| 窗口按钮 | 最终候选再次实点最大化/还原、最小化/恢复并正常关闭；[最大化](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-maximized.jpg)、[还原](D:/项目归档/Differ/2026-11-13-caption-finish/accepted-restored.jpg)。固定入口启动、主题操作后亦正常关闭 |
| Windows 11 贴靠与系统菜单 | 悬停最大化出现 Snap Layouts；Alt+Space 打开系统菜单；`after-snap-menu.jpg`、`after-system-menu.jpg` |
| 拖出取消 | 按住关闭按钮拖入正文再释放，窗口继续保留；`after-close-drag-cancel.jpg`。按住移回原按钮释放由真实 HWND 消息回归覆盖，未声称做过物理多段拖动 |
| 未保存关闭保护 | 取消关闭保留窗口与脏文本；再次关闭并保存后正常退出；`after-close-prompt.jpg`、`after-close-cancel.jpg`、`close-guard-result.json` |

关闭保护测试仅写独立 `close-result.lua`，结果与预期内容一致、原始 BASE/MINE 哈希保持不变。0.21.17 的精确查找、另存后再次保存及 F5 来源流程证据仍在 `D:\项目归档\Differ\2026-11-12-find-save`；本轮窗口关闭验证沿用同一保存安全边界。

150%前台验收通过关闭程序后将任务隔离偏好的 `ReadingScale` 设为1.5，再启动真实窗口完成；并非菜单点击测试，也不等同于系统DPI测试。固定开发入口最终恢复原100%阅读设置。

中间阶段的 `after-*`、`final-*`、`direct-*` 与各自帧清单保留用于回溯，**文件名含 final 不表示最终验收**。`runtime-evidence`、`final-runtime-evidence`、`accepted-runtime-evidence` 是等待真实正文后截取的受控进度图，用于空间检查，不是前台自然播放证明。

最终独立像素复核见 [codex-accepted-frame-analysis.json](D:/项目归档/Differ/2026-11-13-caption-finish/codex-accepted-frame-analysis.json)：原 `x = 窗口宽度 - 156` 接缝相邻像素最大通道差为3，固定矩形跳色已消除；普通标识符不再长时间保留旧主题色。WPF与浏览器的呈现仍非严格锁帧，深→浅首个采样的归一化进度差约6.02个百分点（约15个RGB色阶）；中段黑白反相仍有短时低对比。不能把本轮结果表述为全过程零色差或每帧同等可读。分析使用同一图片内的表面端点归一化，雾光与JPEG误差分别记录，没有按跨次截图编号强行对齐。此前 `codex-direct-frame-analysis.json` 保留为语法色桥之前的实验对照。

## 唯一开发版与清理

唯一源码仍为 `D:\Diff`，固定启动入口为 `D:\Diff\打开开发版.cmd`，程序为 `D:\项目归档\Differ\开发预览\Differ.Phase1.Preview.exe`，已更新至0.21.18。Prepare → Promote → 固定入口复核 → Finalize已完成；`preview-update-accepted\preview-state.json`为Finalized，`acceptance.json`同时核对版本、App.dll及完整载荷SHA-256，避免同版本但Editor不一致。

旧0.21.17预览和0.21.16回退副本已删除，`.开发预览-next`、`.开发预览-previous`、`.开发预览-retired`均不存在，只保留固定开发预览。`delivery-integrity.json`确认固定完整载荷与源码身份一致、安装与SVN绑定及BASE/MINE输入哈希未变、无本轮Differ/testhost进程残留。本轮前台与测试产生的有价值截图、JSON、日志和源码恢复材料保留在证据目录，不能当作第二套日常开发工程。

用户已手动删除以下两处任务缓存（原合计约22.25 MiB），代理随后确认两路径均不存在：

- `D:\项目归档\Differ\2026-11-13-caption-finish\clock-calibration\webview-profile`：本地时钟诊断缓存，9,213,848字节。
- `C:\Users\Administrator\AppData\Local\Differ.UIPhase1\Profiles\E39204975617D8F5B6FF863B`：本轮候选预览专用偏好/WebView数据，14,121,502字节，归属记录为 `candidate-profile.json`。

此前代理的自动删除被审批以 `blocked by policy` 拒绝，命令未执行；`cleanup-remaining.json`、`cache-cleanup-retry-result.json`保留该历史事实。2026-11-13，用户手动删除后，代理只执行核验，没有将手工删除记为代理执行成功。最新[核验记录](D:/项目归档/Differ/2026-11-13-caption-finish/manual-cache-cleanup-confirmation.json)确认缓存不存在、唯一0.21.18开发版完整载荷与源码身份一致、固定开发偏好仍存在、现用0.17.1 SVN安装主程序校验值及Diff/Merge绑定未变。再次Apply/Check均Clean=true、Targets=0、Unclassified=0，见 `manual-cache-workspace-apply.json`、`manual-cache-workspace-check.json`；本轮两处缓存清理已收口。

`D:\Differ\Differ.App.exe` 仍为现用 0.17.1 SVN 安装；本轮不替换它、不更改真实 Diff/Merge 绑定，不运行 SVN 写操作。源码同步遵循本报告开头的公开推送授权，本轮不创建新的Release或安装包。

## 未验证范围

更高 Windows 系统 DPI、混合 DPI/跨屏、Windows 10、干净机、中文 IME 和长期稳定性未在本轮前台实测。普通黑白文字与背景连续反相时中途会降低对比度，本轮没有改动已选的 680 ms 视觉方案，也不声称全过程达到稳定态的对比度标准。Word、Excel、三方合并及启动页的其他视觉迁移仍按样板范围分别推进。
