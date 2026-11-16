# Design QA

> 2026-11-13：源码与唯一固定开发预览已更新至 0.21.18，本轮旧预览运行副本已删除。两处测试缓存已由用户手动删除并经代理复核；验证结果、限制及清理核验见 [0.21.18交付记录](docs/UI-0.21.18.md) 和 [项目状态](docs/PROJECT_STATUS.md)。下方各版记录保留原测试条件，不代表全部页面或环境已验收；历史证据按 [归档映射](docs/ARCHIVE_INDEX.md) 查找。

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](docs/ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

## 0.21.18 统一标题栏与主题收尾，2026-11-13

- 标题栏改为同一全宽 WPF 底板与自绘按钮，消除原来右侧 156 DIP 的 DWM/WPF 分界；补齐按钮捕获、拖出取消和回入释放。真实前台已验证最小化、最大化/还原、Snap Layouts、系统菜单及未保存关闭保护。新增取证门禁等待真实 Monaco 正文与字号就绪，初始化占位图不再算通过。
- WPF 使用共同起点的本地绘制时钟；浏览器调色板按同一插值规则直接提交。语法颜色采用稳定 token 类的临时规则，覆盖 Monaco 重建的可见节点；3项真实WebView2专项及最终前台复测通过，旧色残留已消除。原右侧接缝采样最大3个RGB色阶；跨WPF/浏览器仍非零相位，深→浅首个样本约6.02个百分点（约15个RGB色阶），中途仍有短时低对比，不宣称全过程零色差。
- 同内容 720×620、1500×860 前后自然播放截图、150%阅读缩放和120行中文长行查找均已补齐；实际最小化、最大化/还原、Snap Layouts、系统菜单和关闭保护通过。最新源码Release构建、713/713测试（0跳过）、格式及发布后Apply/Check通过；固定预览已替换为0.21.18，旧运行副本已清除。证据集中在 `D:\项目归档\Differ\2026-11-13-caption-finish`，截图链接与用户手动删除缓存后的核验见 [交付记录](docs/UI-0.21.18.md)。
- 本轮构建保留 NuGet 审计，仅在验证进程将既有 NU1902/NU1903 按警告处理。更高系统 DPI、跨屏与 Windows 10 未实测，不将应用内阅读缩放算作系统 DPI 验收。

## 0.21.15 方案 2 真实播放同步，2026-11-11

- 正常工具栏入口复现了 0.21.14 的正文落后。此前按指定进度冻结两边的截图检查漏掉了自然启动偏差。按用户选定 HTML 的 `runDuskSolution` 统一为 EaseInOutCubic，底色与流光半径使用同一进度，单层幽蓝径向流光从零透明度显现再消散。 [改前自然点击记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk-sync/before/actual-window-report.json)
- 正文先准备配色，窗口收到就绪后统一发布起点。浏览器每帧发送实际进度，WPF 配色和雾光按该帧更新。实际工具栏入口、应用级资源、1500×860 与 720×620、150% 阅读缩放、浅深两向自然播放验证通过。逐帧对应的背景 RGB 差值均为 0。F9、连续反向切换、窗口变化取消及文档/选区/滚动保持通过。 [逐帧对照](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk-sync/frame-color-comparison.json) · [完整运行记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk-sync/frame-driven/actual-window-report.json)
- 空间画面从已完成的一帧暂停后截取，时序数据独立自然采集。运行窗口位于屏幕外，活动状态仅作测试模拟。前台物理点击、高系统 DPI 和 Windows 10 待实机验证。Release 构建、678 项测试和格式通过，验证进程临时关闭现有依赖的 NuGet 审计。 [实际窗口中段](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk-sync/delivery/1500-dark-matched-frame.png) · [工程验证](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk-sync/validation-release/summary.json) · [打开当前开发版](D:/Diff/打开开发版.cmd)

## 0.21.14 暮色夜降方案 B，2026-11-11

- 按用户选定的 `theme_seamless_transition.html` 方案 2，整体配色在 680 ms 内平滑变化，叠加低透明度暮蓝与月青流光。WPF、原生标题栏和 Monaco 使用同一目标与时序。连续切换从当前显示颜色接续，取消时收束到最新主题。
- 夜雾采用用户提供的 XAML 最终 RGBA，并按同一透明度同步到 JavaScript，修正示例中重复叠乘和颜色字符串的问题。主层最大原始透明度为 58/255，亮度包络再按 0.85×(1−progress)^1.8 衰减。
- 实际比较窗口的宽窄两种尺寸、150% 阅读缩放及浅深两向通过。切深色首帧 WPF 和 Monaco 背景均为 `#FFFFFF`，100 ms 均为 `#919193`，306 ms 均为 `#2D2E31`，最终到 `#17181C`。自然播放取样未出现亮度回升，模型、选区和双侧滚动保持，F9、快速反向和窗口变化取消通过。 [实际窗口记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk/actual-window/actual-window-report.json) · [首帧](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk/actual-window/1500-dark-0-window.png) · [中段](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk/actual-window/1500-dark-100-window.png)
- Release 构建、678 项测试通过，格式和清理分别留有记录。验证进程临时关闭现有依赖的 NuGet 审计。运行验证采用离屏实际窗口，前台物理点击、高系统 DPI 和 Windows 10 待实机复核。 [测试日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk/validation-final/test.log) · [格式检查](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/dusk/format-final.log) · [打开当前开发版](D:/Diff/打开开发版.cmd)

## 0.21.13 夜间持续收暗，2026-11-11

- 切深色时，夜雾从换色首帧开始按单调曲线衰减，并沿原圆心收拢。WPF 在资源换色前准备雾层，Monaco 在同一主题消息内同步画出首帧。切浅色继续使用晨光扩散节奏。
- 使用同一实际比较窗口、文件与滚动位置对照 0.21.12 和 0.21.13。1500×860、720×620 与 150% 阅读缩放下，WPF 和浏览器各采样 12 个时刻，另核对自然播放。旧版夜间先从零透明度升高，新版取样持续下降，自然播放的背景 RGB 回升从 36/38 个色阶降到 0。浏览器在主题消息处理完成时已有雾光像素。 [亮度对照](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/brightness-comparison.json) · [实际窗口记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/after/actual-window-report.json)
- 快速反向切换、F9、窗口变化取消、模型与选区及滚动保持均通过。验证使用实际 WPF/WebView2 的离屏窗口，100% 系统 DPI，实际前台操作与高系统 DPI 待验证。 [首帧](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/after/1500-dark-0-window.png) · [100 ms](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/after/1500-dark-100-window.png) · [306 ms](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/after/1500-dark-306-window.png) · [打开当前开发版](D:/Diff/打开开发版.cmd)
- Release 构建、676 项测试、格式与工作区清理通过。新增回归按 20 ms 步进检查夜雾取样像素的亮度持续下降及结束清空。验证进程临时关闭已有依赖的 NuGet 审计。 [验证记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/night-timing/validation/summary.json)

## 0.21.12 标题栏雾光连续性复查，2026-11-11

- 重新检查发现，逐帧 DWM 纯色、矩形补色和渐变雾层在空间及呈现时序上各自变化。前几轮固定时刻的边缘色差检查不足以证明整个区域连续。现改为同一半透明雾层覆盖完整 WPF 客户区，原生按钮继续由系统绘制和命中，图标在过渡中接受同样的光色叠加。清除标题栏专用遮罩、纯色采样和补色绘制。
- 用实际 `DiffWindow`、真实模板和离线 Monaco 加载同一 `LongThemeFixture.cs`，定位到 129 行。1500×860、720×620 与后者 150% 阅读缩放，浅深两向各检查 11 个时刻。检查整块区域的空间变化、选区与双侧滚动，工具栏事件、浏览器 F9、快速反向切换和窗口尺寸变化取消均通过。 [实际窗口后半段](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-root-review/actual-window/1500-dark-500-window.png) · [工具栏入口验证](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-root-review/actual-window/actual-window-report.json)
- 实际窗口离屏自然播放采到 83 帧，右上区域跨边界差值为 0～2 色阶，结束后 WPF 与浏览器雾层均清空。原生命中返回最小化、最大化和关闭。自然播放测试直接驱动实际主题服务和视图桥接，窗口保持非活动，原生标题栏活动外观通过消息模拟。它验证运行时绘制，实际前台鼠标操作、Windows 10 和高系统 DPI 仍待验证。 [自然播放与恢复](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-root-review/actual-window-live/actual-window-report.json)
- Release 构建、674 项测试、格式和工作区清理通过。验证进程临时关闭已有依赖的 NuGet 审计。 [验证结果](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-root-review/validation/summary.json) · [打开当前开发版](D:/Diff/打开开发版.cmd)

## 0.21.11 标签栏右侧分隔线，2026-11-11

- 用户截图中，标签栏底部右侧连续 155 个像素为深色分隔线原色 `#272930`，左段随雾光变化。保护区以标签栏内容区的实际底边为准，整条 WPF 分隔线参与过渡。 [用户截图](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-divider/evidence/reported-0.21.10.png)
- 离屏探针使用真实 1 DIP 分隔线、实际 DWM 按钮及同内容合成窗口。1500×860 切深色 306 ms 时，分隔线跨界色差由 110 降到 1 个 RGB 色阶，720×620 浅深两向均降到 1。原生按钮命中和过渡结束后的底色恢复已复核。测试为 100% 系统 DPI，实际桌面点击、跨屏及高 DPI 待验证。 [改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-divider/evidence/divider-before-dark-1500-306.png) · [改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-divider/evidence/divider-after-dark-1500-306.png) · [像素记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-divider/evidence/divider-dark-1500-306.txt) · [打开当前开发版](D:/Diff/打开开发版.cmd)
- Release 构建、673 项测试和格式检查通过。实际窗口模板的 720、980、1500 DIP 布局回归均检查原生按钮保护区和分隔线的边界。验证进程临时关闭现有依赖的 NuGet 审计。 [验证记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-divider/validation/summary.json)

## 0.21.10 原生按钮区跟随雾光，2026-11-11

- 0.21.9 虽已柔化裁切边缘，切深色时原生按钮区仍提前显示深色底。0.21.10 从同一帧的雾光计算 Windows 标题栏底色，用邻近 WPF 雾层与之衔接。支持系统着色时应用这一路径，取消或完成时恢复主题底色。 [用户复核截图](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-continuity/evidence/reported-0.21.9.png)
- 1500×860 切深色 306 ms 时，旧版按钮区为 `#111215`，新版为 `#8B8D8F`，紧邻 WPF 像素为 `#898C8E`。离屏探针同时复核深色 500 ms 与浅色 720×620，完成后原生按钮区像素恢复，三种原生命中均有效。探针使用同内容合成 WPF 窗口和实际 DWM 按钮。桌面实点、实际前台逐帧同步和更高系统 DPI 待验证。 [切深色改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-continuity/evidence/v0219-dark-1500-306.png) · [切深色改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-continuity/evidence/sampled-dark-1500-306.png) · [像素记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-continuity/evidence/continuity-dark-1500-306.txt) · [打开当前开发版](D:/Diff/打开开发版.cmd)

## 0.21.9 标题栏按钮区雾光，2026-11-11

- 用平滑透明度渐变取代右上按钮区的矩形硬裁切。系统按钮区域保持透明，雾光在其左侧和下方渐隐。用户截图中可见的左侧和下方突变在 1500×860 同内容 WPF 离屏对照中消失。原生按钮图标像素一致，命中结果分别为最小化、最大化、关闭。 [用户截图](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/evidence/reported-0.21.8.png) · [离屏改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/evidence/caption-hard-clip.png) · [离屏改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/evidence/caption-feathered.png) · [像素与命中记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/evidence/caption-probe.txt)
- WPF 回归覆盖 1500×860 和 720×620 两种尺寸的雾光渐隐，720×620 离屏复核浅深两向。Release 构建、670 项测试及格式检查通过，验证进程临时关闭现有依赖的 NuGet 审计。离屏探针使用合成文本，桌面物理点击、系统贴靠菜单、125% 及更高系统 DPI 和跨屏待实机确认。 [浅色窄窗](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/evidence/caption-feathered-720-light.png) · [测试日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/caption-fix/validation/test.log)

## 0.21.8 晨光与月雾，2026-11-11

- 双栏文本比较手动换色时，晨雾扩散、月雾收拢，中心固定于主题按钮。WPF 客户区和 Monaco 各自绘制低透明度雾光，共用 680 ms 时间轴。窗口变化和活动页签切换会清理当前雾光。
- Monaco 隔离浏览器验证覆盖双向雾光、F9、连续切换、长行、选区和滚动位置、720 CSS px 与模拟 125% 缩放。桌面电脑操作由用户停止，跨 WPF/WebView2 接缝、真实系统 DPI、原生按键和同条件桌面前后截图待实机复核。 [Monaco 验证](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/screenshots/monaco/editor-theme-mist-qa.json)
- Release 构建、668 项测试及格式检查通过。验证进程单独关闭现有传递依赖的 NuGet 审计，项目配置保留原有审计行为。 [测试日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/validation-final/test.log) · [格式日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/format-final.log)
- 同日按用户要求清理旧 HTML、历史合成输入和重复预览构建。以下历史验收中的合成输入与部分中间构建已清理，运行记录和最终预览可供回查。 [清理清单](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Mist-QA-20261111/legacy-cleanup.csv)

## 0.21.7 主题切换响应，2026-11-10

- 用户录屏为 30 fps，四次切换均在正文更新后 8 帧才更新工具栏与状态栏。比较窗口将 WPF 布局、原生标题栏和当前 Monaco 的换色提交靠拢，缓存编辑器随后刷新。 [录屏帧索引](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/recording-theme-frames.json)
- Windows 11 桌面用同一 1500×860 窗口、七个混合页签和相同代码位置，单次抓取一帧内的正文及外壳像素。0.21.6 三次相差 438–464 ms，0.21.7 两次落在同一采样帧、一次相差 29 ms。采样间隔限制了可辨认的时间差。 [改前采样](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/baseline-code-frames.csv) · [改后采样](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/candidate-code-frames.csv)
- 万行 Excel 的单变量离屏对照中，去掉未使用的原生网格线画刷更新后，布局稳定耗时中位数从 534 ms 降到 70 ms，九次切换的 360 个行容器引用全部保留。桌面复核压力样例的两侧选格、103 处差异与浅深主题。 [离屏测量](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/wpf/MEASUREMENTS.md) · [打开当前开发版](D:/Diff/打开开发版.cmd)
- 宽窗与 720×620 DIP、应用内 150% 阅读缩放均保存同内容前后截图。窄窗实测切换后保留 F8 当前块和双侧位置。自动测试覆盖 130% 缩放、背景页签更新、Monaco 撤销链与冻结表头容器。Windows 10、更高系统 DPI、跨屏及系统自动主题切换未桌面实测。 [窄窗深色改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/screenshots/baseline-code-narrow-dark-150.jpg) · [改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/screenshots/candidate-code-narrow-dark-150.jpg)
- Release 构建、668 项串行测试、格式与项目目录清理通过。验证进程按隔离预览流程单独关闭 NuGet 审计。 [验证日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Theme-QA-20261110/validation-serial)

## 0.21.6 标题栏文件页签，2026-11-10

- 顶部使用 38 DIP 文件页签标题栏和 40 DIP 工具栏。右侧由 Windows 绘制窗口按钮，标题栏空白区保留系统命中行为。溢出页签通过文件列表、横向滚动和键盘访问。筛选时弹层固定在打开位置。
- 以 0.21.5 工作区快照为基线，同一份合成文件、当前页签和正文位置复拍。Windows 捕获的可见窗口尺寸一致。 [1500×860 浅色改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-TitleTabs-QA-20261110/screenshots/before-wide-light-1500x860.jpg) · [改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-TitleTabs-QA-20261110/screenshots/delivery-wide-light-1500x860.jpg) · [720×620 窄窗改前](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-TitleTabs-QA-20261110/screenshots/before-narrow-150-720x620.jpg) · [改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-TitleTabs-QA-20261110/screenshots/delivery-narrow-150-720x620.jpg)
- 八个文件的同尺寸截图中，比较区起点上移 50 像素。窄窗使用长中文名和应用内 150% 阅读缩放，溢出裁切会隐藏不完整的页签及其关闭按钮。桌面实测溢出选文件、独立关闭、F8、Ctrl+Tab、拖动、最大化与还原、最小化及系统菜单。16 份样例文件校验值一致。更高系统 DPI、跨屏、Windows 10、悬停贴靠布局和真实 SVN 调用未实测。
- Release 构建、串行全量 667 项测试、格式和工作区清理检查均通过。常规 NuGet 审计因基线传递依赖告警阻断，验证进程单独关闭审计。工作表切换计时在首轮全量验证中波动，基线与候选单测复跑均通过。 [打开当前开发版](D:/Diff/打开开发版.cmd) · [工程验证日志](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-TitleTabs-QA-20261110/validation-serial)

## 0.21.3 Excel 滑块桌面验证，2026-11-10

- 使用 10,000 行 × 40 列样本，保持同输入、同窗口和同起点，通过 Windows 桌面实际拖动右侧滑块。纵向首次双侧对齐绘制从 178–272 ms 降到约 16 ms，横向从 251 ms 降到约 17 ms。最终程序从左侧发起时也确认双栏在结束前更新。
- 用三张各 12,000 行 × 20 列的合成工作簿验证 720×620 DIP、应用内 150% 缩放和冻结首行首列。横纵拖动期间两侧都显示同一位置，松手后实际网格偏移对齐。 [按住期间横拖画面](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Excel-Drag-20261110/desktop/narrow-frozen/gesture-02-held-01.png) · [同条件改前改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Excel-Drag-20261110/narrow-frozen-before-after.png)
- 桌面工具每个手势产生一个移动事件，记录的是应用在拖动结束前的首个对齐绘制，不是持续帧率。多次中间偏移由 WPF 回归验证。大跨度松手后原生网格接回仍有可测延迟，过渡期持续显示对齐后的真实数据。 [验收记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Excel-Drag-20261110/验收记录.md)

## 0.21.2 Excel 性能复测，2026-11-10

- 三张各 12,000 行 × 20 列的合成工作簿，后两张首次切换由 662–773 ms 降至 560–592 ms。三张各 3,000 行 × 40 列的样本经倒序复测，热切表 p95 从 32–34 ms 降至 18–23 ms。
- 同一 10,000 行 × 40 列样本连续大跨度跳滚的中位耗时下降，p95 两轮结果波动，仍有明显的长尾。真实鼠标连续拖动未完成桌面复核。
- 同尺寸同数据 WPF 运行时截图核对双侧行列、选择框、展开详情和底栏。桌面捕获遇到会话不可用，采用离屏 WPF 证据，未将其计为原生桌面截图。 [720 DIP 改前改后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Excel-Perf-20261110/excel-720-before-after-wpf.png) · [验证与原始数据](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-Excel-Perf-20261110/验收记录.md)

## 0.21.1 隔离样板，2026-11-10

视觉基准为用户提供的四张 V3 效果图。对照实际桌面的文本浅色、编辑态、深色和 Excel，基准为 1440×900 像素，1440×900 DIP 的 Windows 窗口实际截取为 1426×893 像素。并排图保留原比例，窗口边框外的像素不计入。窄窗改前改后均使用同一份工作簿、同一单元格和 720×620 DIP 窗口。 [视觉基准与并排图](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/comparisons)

- 外框、底板、标题和底栏按同一间距与浅深色体系调整。窗口使用原生标题栏和现有矢量图标，正文使用系统字体与等宽字体。保留 24 DIP 文本行高。测试样例的真实路径、修订和保存权限与效果图占位文案分别呈现。
- 首轮实测长列占满宽度，遮住 Excel 的 D 列标题，短值与地址之间空白过大。限制长列宽度并收紧对照组后复拍，A–D 四列可见，720 DIP 窄窗仍保留两个长值预览、复制和展开按钮。相同值回到正文色。 [Excel 复拍](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/after-excel-1440-light-C3-collapsed.jpg) · [窄窗前后](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/comparisons/excel-720-C5-before-after.png)
- 文本浅红、浅绿差异与斜纹对齐空位按参考图调整。浅深主题差异正文颜色通过 4.5∶1 对比度校验。已核实只读与编辑按钮文案、圆角画布、定位、忽略空白与真实另存。 [浅色](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/comparisons/text-light-reference-and-final.png) · [编辑](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/comparisons/text-edit-reference-and-final.png) · [深色](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/comparisons/text-dark-reference-and-final.png)
- Excel 测试样例存在解析提示，因此底部另显示紧凑状态。继续保留真实工作簿长值、列滚动条、完整路径和状态提示。系统 DPI 与混合显示器未复测。 [验收记录](D:/项目归档/Differ/2026-11-12-consolidation/history/Differ-UI-V3-Fix-20261110/验收记录.md)

final result: passed

## 0.15.0 — 2026-09-25: application icon and offline installer

Released `artifacts/installers/Differ-Setup.exe` (52,942,228 bytes) and fixed self-contained application **0.15.0.0**. Specification **3.13.0** covers explicit optional SVN integration, current-user installation, safe uninstall ownership and Win10/11 x64 compatibility. The original dual-pane comparison UI/workflow is unchanged.

- Original graphite/blue paired-document icon generated with the built-in image tool, retained as PNG plus a nine-frame ICO (16 through 256 px). WPF decoder and small-size light/dark previews checked; EXE, both windows, Setup/Uninstall and installed shortcuts use the icon. Win10 toolbar glyph fallback added.
- Build: zero warnings/errors. **419 tests passed**, including installer maintenance and non-default TortoiseSVN discovery tests. Format verification passed. Standard publish and NSIS 3.12 `/WX` packaging passed.
- Actual Windows 11 x64 build 28000 lifecycle: first install to a previously nonexistent Chinese/space path, 462 file hashes checked, desktop shortcut target/icon confirmed; running-app upgrade rejected without closing it; closed-app upgrade succeeded in the same directory; uninstall removed owned binaries/registration/shortcuts and preserved a personal-file sentinel.
- Live installed integration window displayed the new icon and actual installation path. Status and invalid maintenance commands exited without opening windows. Binding writes are tested using isolated configuration; real smoke installation did not enable binding. Existing real global Diff, advanced DiffTools, backup-file hash and all five source-fixture hashes remain unchanged.
- Fixed a discovered first-install issue: NSIS GetFullPathName rejected nonexistent parent paths; replaced with GetFullPathNameW normalization and validation before directory creation. Final installer includes the fix.
- Package is **unsigned**. Win10 compatibility has not been tested on a clean Win10 VM or the user's company machine. No assurance is made about enterprise allowlist policy. Target users need their own TortoiseSVN for commit-window invocation, but no separate Office, .NET SDK/runtime or svn.exe.

SHA-256: `df7c3163ee5a055c4ca576cfaa30e7644ee514ccc9891f4f9f979bb183e120fe`. Full evidence: `artifacts/packaging/2026-09-25/交付记录.md` and `docs/INSTALLATION.md`.

## 0.14.2 — 2026-09-25: visual polish

Released at the fixed `artifacts/publish/Differ-win-x64/Differ.App.exe` path, version **0.14.2.0**. Development specification is **3.12.2**. The Excel comparison workflow, semantic model, source files, grid alignment, virtualization and character-level detail selection remain intact.

- Unified toolbar icon/button sizes, grouping, role-based radii and typography; navigation uses a single shallow surface.
- Softer light/dark semantic fills and grid lines, 3 DIP row markers and unchanged 2 DIP four-sided selection frames.
- Quieter minimap strokes and 25% hover expansion, preserving real preview and continuous drag navigation.
- Excel first-use details total height is 104 DIP (28 header + 76 content); persisted 72/124/180 values retain their total-height meaning. This machine's saved 180 DIP was verified unchanged.
- Fixed pre-existing horizontal-overflow tab clipping: file/sheet strips retain 30/28 DIP minimum heights and make room for the scrollbar only when necessary. Four actual-template regressions cover long labels and narrow windows.

Validation:

- `dotnet build -c Release`: passed, zero warnings/errors.
- `dotnet test -c Release`: **381 passed, zero failed**, including 63 Excel adapter and 166 App tests. Final TRX files are in `artifacts/design/2026-09-25-ui-polish/test-results/final*.trx`.
- `dotnet format --verify-no-changes --no-restore`: passed.
- Standard staging-first `scripts/publish.ps1`: passed. Publish root contains only `Differ-win-x64`, no ZIP. Published App DLL SHA-256: `3F9B8CB563B477B1BE92DE28EB34ADBFC7AB2DECF957D65AAF175663B7225E05`.
- Before/after real WPF rendering: 64 PNGs each, light/dark, 1440×900 and 980×680, 100/125/150/200% offscreen raster scaling, mixed added/deleted/modified cells, long Chinese content, formulas, collapsed details and multiple tabs. Both versions preserve 47 differences in the controlled fixture.
- Both baseline and candidate pass **119/121 probe checks**. The same two pre-existing edge cases remain: after narrowing to 980 DIP, selected E4 on the second sheet is partially outside the horizontal viewport. This does not represent a new regression; horizontal scrolling and complete details remain available. Do not report all probe checks as passed.
- Published executable was inspected live: light/dark switch, partial character selection in details, minimap drag to a different difference, synchronized dual selection and real hover preview. Native screenshots are `published-*.png` in the same artifact folder.
- All **5 original test-case file hashes unchanged**. Only isolated generated fixtures were used; no SVN configuration or write operation was performed.

Limits: offscreen raster scaling is not an OS per-monitor DPI transition test. Native clipboard contents, screen readers and a new large-workbook performance benchmark were not checked in this polish pass. No claim of complete accessibility or full native DPI QA is made.

Evidence and delivery summary: `artifacts/design/2026-09-25-ui-polish/交付记录.md`; detailed probe observations: `visual-probe-result.md` in the same folder.

## 0.14.1 — 2026-09-25: pressure workbook and cell selection

final result: passed

Scope: the user's actual 3-sheet / 12000-row workbook, compared against the BASE supplied by TortoiseSVN. Detailed measurements, raw JSON and source hashes are in `artifacts/performance/2026-09-24/fix-report.md` and its linked probe reports.

### Changes verified

- Selected spreadsheet cells now have a complete 2 DIP rectangle on both sides. The fixed overlay does not affect row height, text position, semantic background, mouse input or virtualization.
- Plain cells avoid creating rich text containers. Lightweight spreadsheet columns reuse a single cell presenter with one value binding instead of nested templates and six state bindings.
- Background preparation reuses only fully equal immutable values, stops saturated column measurement, avoids per-grapheme strings, and reduces address/metadata/node allocations. It does not omit rows, cells, unchanged context or diff nodes.
- Busy animation is tied to `IsBusy`. No claim is made that this resolves unrelated idle CPU observations; sampled accessibility-provider activity can affect such observations.

### Real input and equivalence

The real pair retains **997 differences**: 462 added, 481 deleted, 54 modified; worksheet counts 342 / 360 / 295. It retains all 504523 model nodes and aligned row counts 12023 / 12025 / 12020. Complete DiffDocument and complete presentation semantic digests match 0.14.0. The user's original working-file SHA-256 remains `5C3B1B46339607B67A03EE902E00B387FACDFB4EC18880869C9343394CFA05D3` after delivery.

### Controlled paired UI measurements

Same true BASE/MINE, window size and production WPF resources, using a screen-external probe and time to completed layout/dispatcher work. These are local measurements, not universal performance promises or screenshot-tool timings.

| Operation | 0.14.0 | 0.14.1 |
|---|---:|---:|
| Cold visit to the other worksheets | 740 / 776 ms | 431 / 449 ms |
| Cached worksheet switch p95 | 8.37 ms | 7.64 ms |
| Small vertical scroll p95 | 67.83 ms | 44.33 ms |
| Far vertical scroll p95 | 243.45 ms | 173.69 ms |
| Full-range horizontal jump p95 | 391.23 ms | 201.75 ms |
| Far cell selection p95 | 229.78 ms | 174.07 ms |

Nearby selection did not improve (median 3.15 → 4.19 ms). Rendering is improved but distant jumps are not instantaneous. Background allocations fall by approximately 30.5% (about 993 MiB per measured preparation); first parsing still takes seconds. The newly launched native staging run logged approximately 6.6 s comparison, 1.0 s presentation and 0.5 s first worksheet; do not describe this as instant loading.

### Tests and visible evidence

- Release build: zero warnings/errors. Full format verification passed.
- **370 tests passed, 0 failed, 0 skipped**, including 155 app tests and 63 Excel adapter tests. Final TRX set: `artifacts/performance/2026-09-24/passed-tests/`.
- Regression coverage includes full semantic equivalence, Unicode widths, shifted addresses, formula/cache details, rich/plain/empty recycling, both themes and four output DPI values for all four selection edges.
- The cached-switch benchmark now drains through ApplicationIdle during warm-up and measurement: Render alone stops before lower-priority Loaded callbacks and can count deferred cold column setup as cached switching. The original 100 ms p95 / 180 ms maximum budgets remain unchanged.
- Native 12000-row view visibly showed complete corresponding C121 rectangles and matching details. A later fresh capture showed complete A2591 rectangles after the view moved to a distant part of the table. No stale row-height or colored-background changes were observed.
- Native synthetic keyboard input was not completed because the window was actively receiving user input. Automated native cell selection/dual-pane synchronization and recycling passed; no manual arrow-key claim is made.
- Windows system DPI changes were not performed; four output-DPI render checks are separate from real system scaling.

### Delivery

- Fixed path updated to `E:\Differ\artifacts\publish\Differ-win-x64\Differ.App.exe`, version **0.14.1.0**.
- Published DLL SHA-256 matches the verified live staging DLL: `7681B397299CD928BAF64263224A5747496980D6B8D1B8CA833517CEB23C4584`.
- Old 0.14.0 retained at `artifacts/performance/2026-09-24/before/Published-0.14.0`.
- The user's active 0.14.1 preview in `artifacts/Differ-win-x64-staging` was preserved; publishing copied the identical build rather than closing or moving the active preview. The next normal start uses the fixed published path.
- The publish directory still contains only `Differ-win-x64`. No SVN configuration, source workbook or submission state was changed. The baseline diagnostic process opened for this task was ended before staging verification; source snapshots had already been preserved.

![Complete selected cell borders in the true pressure workbook](E:/Differ-归档/2026-10-24-cleanup/artifacts/performance/2026-09-24/selection-after-0141-light.jpg)

![Selection after moving far down the actual table](E:/Differ-归档/2026-10-24-cleanup/artifacts/performance/2026-09-24/selection-after-scrolled-0141.jpg)

---

## 0.14.0 — 2026-09-24

final result: passed

Scope: approved native WPF visual redesign, with existing diff functionality retained. This result covers the rendering and interaction checks below; Windows-wide switching between display scale factors was not performed.

### Visual target and comparison

- Approved plan: `artifacts/design/2026-09-24-ui-proposal/GDDiff-UI设计方案.md`.
- Original native Excel: `artifacts/design/2026-09-24-ui-proposal/03-current-excel-light.jpg`.
- Final native Excel: `artifacts/design/2026-09-24-ui-refresh/08-published-excel-light.jpg`.
- Original/final captures use the same supplied workbook pair, E9 selection, two file tabs, 1440×900 saved window geometry and 100% reading scale. Both Computer Use captures are 1427×894 pixels. These were opened together in the same comparison input; no screenshot content was repainted or reconstructed.
- ImageGen direction reference: `artifacts/design/2026-09-24-ui-refresh/visual-reference.png`. It is an art-direction aid, not a pixel-accurate contract: generated extra worksheet controls, altered sample text, header letters, type controls and decorative frames were deliberately not implemented. The approved specification and real data take precedence.
- Built-in ImageGen prompt and asset trace: `artifacts/design/2026-09-24-ui-refresh/visual-reference-prompt.md`.

### Findings and resolved iterations

- [P2, fixed] New detail header initially added 28 DIP to legacy saved heights. `DetailHeight` now retains its historical meaning of the whole expanded panel; saved 72/180 DIP values remain 72/180. New defaults are 124 overall, including 28 header and 96 content. Collapsing uses 28 without overwriting the saved height. Legacy preference and binding tests cover this.
- [P1, fixed] Word rows retained a previous horizontal extent after narrowing from 1500 to 980 DIP, clipping the current-side text. Rows now bind to the actual scroll viewport width while retaining virtualization. New tests enforce equal columns, no overflow, and alignment with the bottom side summary. The failure evidence is `test-results/layout-word-bounds.trx`; corrected narrow Word renders are in `artifacts/visual-validation/0.14.0/reading-docx-*-980x680-*.png`.
- [P2, fixed] Side headers repeated the current filename/display name. Text/Excel headers now show BASE / 工作副本 and an available revision only; complete original names, paths and revisions remain in tooltips.
- No unresolved P0/P1/P2 visual issue was observed in the inspected states. Native system caption color follows Windows and is an intentional platform constraint.

### Required visual surfaces

| Surface | Result |
|---|---|
| Typography | Text 13.5 DIP / 24 DIP rows, no decorative horizontal rules; Excel retains grid and alignment; Chinese and monospace fallback inspected in light/dark wide/narrow renders |
| Spacing | Toolbar 38, tabs 30, text/Excel header 28, minimap 44, status 24 DIP; layout rounding allows at most one physical pixel in numeric assertions |
| Colors | Neutral light/graphite dark; opaque reading surface; red/green/amber retained as diff semantics; selected states do not replace diff backgrounds |
| Assets | Existing native Windows icon font and vector/drawn UI; no generated bitmap used as interactive UI; minimap displays actual nearby content |
| Copy | Zero-count statistics omitted, comparison rule changes/pending state remain visible, warning entry retained, full-detail action named and accessible |

### Interaction checks

1. Published self-contained executable starts and two external comparison requests join one window — passed.
2. Text starts collapsed; expanding exposes BASE/current selectable detail documents, collapsing returns to the compact strip — passed in native UI and regression tests.
3. F8 changes 1/2 to 2/2 and updates the selected line and detail — passed in native UI.
4. Clicking the narrowed minimap returns to the first changed line and displays real two-sided preview content — passed in native UI (`05-minimap-preview.jpg`).
5. Switching text/Excel tabs retains the active comparison and expected detail state — passed in native UI; persistent file/worksheet view reuse also asserted automatically.
6. Light/dark toggle repaints the live Excel grid, minimap, toolbar and detail — passed; light appearance restored.
7. Esc exits the staging comparison, then the same tested build was installed at the stable path — passed; no comparison process remained before replacement.
8. Native installed process reports `Responding=True` and the verified publish path; final sample window remains open for review — passed.

### Verification and release

- `dotnet format Differ.sln --verify-no-changes --no-restore --verbosity quiet` — passed.
- `dotnet build Differ.sln -c Release --no-restore` — passed, zero warnings/errors.
- `dotnet test Differ.sln -c Release --no-build --no-restore` — **357 passed, 0 failed, 0 skipped**, including 142 application tests.
- TRX files: `artifacts/design/2026-09-24-ui-refresh/final-tests/`; aggregate output: `full-tests-final.log`.
- 46 render captures cover native WPF templates for code, Excel, Word and binary fallback, light/dark, wide/narrow, and 96/120/144/192 output DPI. `DPI_METHOD.md` explicitly distinguishes RenderTargetBitmap output DPI from real Windows display scaling and from reading zoom.
- Actual desktop interaction was checked at the current host display scale. Other native Windows scaling modes, high-contrast mode, remote desktop and a complete assistive-technology audit were not exercised.
- Version **0.14.0.0**, fixed executable `E:\Differ\artifacts\publish\Differ-win-x64\Differ.App.exe`.
- Published `Differ.App.dll` SHA-256: `261FFEF28E0910C67149993351902B4136F514510775E587A3D65239360C040D`, identical to the staging build exercised above.
- Prior published directory retained at `artifacts/design/2026-09-24-ui-refresh/before/Published-0.13.0`; original source, preferences and QA report also backed up under `before`.
- The publish root still contains only `Differ-win-x64`; no ZIP or parallel version directory was added there. Replacement used checked absolute paths and a rollback move instead of deleting the old version first.
- No SVN operation, registry write, source fixture edit, file-content algorithm change, cloud upload, file tree or history feature was introduced. Full SVN commit-dialog end-to-end interaction was not repeated; equivalent external arguments and the existing configured path were checked.
- Read-only registry verification confirms the global Diff command points to the fixed published executable. The 13 remaining advanced overrides are for other extensions (.docm/.dot/.dotx/.ods/.odt/.nb/.ppt/.pptm/.pptx/.sxw/.xlam/.xlsb/.zip); existing core text/Word/Excel bindings were not changed. Evidence: `svn-path-check.json` and `svn-overrides-check.json`.

### Evidence previews

![Published light Excel](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/08-published-excel-light.jpg)

![Dark Excel](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/07-final-excel-dark.jpg)

![Compact text](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/02-final-text-collapsed.jpg)

---

## 0.13.0 current verification — 2026-09-19

Implemented the review-efficiency roadmap and reference-driven minimap update. The supplied 216×364, 3.92-second recording was inspected as visual evidence. Its narrow marks, local hover emphasis and floating preview were adapted to Differ's light/dark themes and real BASE/current content.

Release: zero warnings/errors; 340 tests passed, zero failed/skipped; format verification passed. Executable 0.13.0.0 is in the same fixed publish directory. The live SVN command still points there; no registry value was rewritten this turn.

Native checks on disposable inputs:

1. Minimap — passed: light/dark real previews, local enlargement, centered navigation and viewport tracking. Unchanged selection retains the total difference count.
2. Search — passed: Ctrl+F and Hero50 navigation to original line 53; local Escape leaves the comparison window open.
3. Context — passed: three surrounding lines, original line numbers, expandable blocks. The ordinary button minimum was overridden to prevent folded-row text clipping, with a real template regression.
4. Key alignment — passed: shuffled CSV records produced 12 default differences; key A reduced them to the single value change, preserving both row numbers.
5. Refresh — passed: F5 changed working value 120 to 125 while BASE stayed 100 and its hash was unchanged.
6. Multiple files/cleanup — passed: a second external request joined the existing window and exited 0; closing test windows left zero session snapshots.

Automated coverage also includes cross-sheet Go To, invalid-key fallback, pending/applied presets, atomic settings, export encoding/protected paths, refresh rollback/cancellation, zoom, local keyboard scopes, minimap fold-expansion reentrancy and bounded hover processing. Reports describe applied rules rather than pending menu edits.

Real WPF tests cover 980-DIP layout and 16 simulated 100/125/150/200% raster outputs. This is not a full native multi-monitor DPI/accessibility audit. UI latency tests are isolated from concurrent DPI rendering; the existing 80/100 ms p95 and 150/180 ms maximum thresholds were not relaxed.

Evidence: [delivery and usage](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/2026-09-19-0.13.0-delivery.md), [verification](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.13.0-verification.json), [screenshots](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.13.0).

The sections below are historical records.

## 0.12.0 current verification — 2026-09-19

Release build and format verification passed with zero warnings/errors. All 263 tests passed (153 before this work). The fixed self-contained publish directory contains 0.12.0.0.

Native Windows checks used disposable inputs and an isolated copy of the SVN working-copy metadata. The real TortoiseSVN commit dialog launched Differ when its README row was double-clicked. A subsequent TortoiseSVN comparison opened another tab in the same comparison window. No commit, update, revert, add or delete operation was performed.

1. Integration page: healthy. Installed the fixed executable through the normal backed-up integration service. The live global Diff command resolves, all supported extension overrides are empty, and original settings have recovery backups.
2. Excel review: healthy. Real aligned grids, per-cell navigation, worksheet switching and hidden-column details were inspected. Light/dark theme button switching remained readable. The final hidden-column detail uses Excel letters rather than internal numeric ranges.
3. Word review: healthy within the logical-document scope. Native rendering showed the changed content-control text inside a table and the changed body paragraph.
4. Binary fallback: healthy. A corrupt workbook selected the text/hex template on the same view model and showed the changed byte plus a compact warning.
5. Comparison options: healthy. The native menu exposes relevant options, retains check states, and applies a new comparison without replacing the file tab. Automated tests cover cancellation, stale results, original text preservation and ignored inline changes.
6. Closing: healthy. Ctrl+W reduced five snapshot directories to four; closing the comparison window left zero session snapshots. The SVN commit selection remained unchanged, then the dialog was cancelled.

Evidence:

- [Machine-readable release verification](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.12.0-verification.json)
- [Performance measurements from published DLLs](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.12.0-performance.json)
- [Repair details and recommendations](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/2026-09-19-fixes-and-roadmap.md)
- [Native screenshots and simulated DPI output](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.12.0)

Eight real WPF runtime cases cover xlsx/docx, light/dark, and both truncated ZIP and non-ZIP payloads. They verify template replacement, resource inheritance and a 980-DIP toolbar with visible, non-overlapping controls. Sixteen RenderTargetBitmap outputs cover 100/125/150/200% output DPI. These are simulated DPI renderings, not a claim that Windows per-monitor scaling was changed or completely tested. Keyboard/screen-reader accessibility has not received a full compliance audit.

The earlier 0.8.0 record below remains historical evidence. Temporary SVN verification-copy cleanup was rejected by automatic approval review ("blocked by policy"), so that disposable copy remains in the temporary directory; application session snapshots were successfully cleaned by the app itself.

## Historical 0.8.0 record

## Current scope

This record covers Differ 0.8.0 theme completion and comparison-window
keyboard access. The previous 0.5.0 screenshots under
`artifacts/visual-validation` remain historical evidence only and are not used
to claim that the current build passed.

Validation date: 2026-08-14.

## Automated guards

- Light and dark theme dictionaries load through the same WPF pack URI used by
  production.
- Both dictionaries define exactly the same resource keys.
- Every production `DynamicResource` and code-side theme-key reference exists
  in both dictionaries.
- Production XAML outside the theme dictionaries contains no literal UI color.
- Production C# controls do not construct literal color brushes.
- Theme services use independent resource dictionaries and remain isolated when
  tests run concurrently.
- System preference changes and manual toggles replace one existing dictionary
  instead of stacking resources.
- The shortcut resolver covers F7, F8, F9, Ctrl+U, Alt+Left, Alt+Right, Ctrl+W,
  and Escape without intercepting copy or modified variants.

## Native WPF checks

The Release build was opened directly and checked in its real WPF windows:

- The no-argument TortoiseSVN integration page opened in the Windows-selected
  light theme with a visible and accessible theme button.
- Switching that page to dark mode updated cards, banners, fields, buttons,
  labels, and ordinary inherited text. A low-contrast inherited heading found
  during this pass was corrected with a dynamic implicit text style and then
  rechecked.
- A text comparison opened with aligned rows, line numbers, inline changes,
  detail panes, minimap, file tab, and toolbar intact in both themes.
- `Ctrl+U` hid unchanged rows, `F8` moved the counter from difference 1/2 to
  2/2, and `Alt+Left` / `Alt+Right` moved keyboard focus to `BaseTextGrid` /
  `MineTextGrid`.
- An Excel comparison opened in dark mode with worksheet grid context, row and
  column headers, selected-cell detail, worksheet tabs, inline change emphasis,
  and synchronized two-pane layout readable.
- A Word document opened in dark mode and switched back to light without
  recreating the tab; body text, headings, table/document surfaces, minimap,
  separators, and BASE/current footer summaries all updated.
- Theme changes did not alter row height, pane width, padding, or tab layout.

## Repository and release checks

- The temporary theme diagnostic writer was removed; the build no longer
  creates `theme-diagnostics.log`.
- Three unused DiffWindow styles and the unused literal-color converter were
  removed.
- Real-machine TortoiseSVN backup artifacts were removed from version control;
  runtime backups continue to live outside the repository through the existing
  backup store.
- Word and Excel formatting-only changes are ignored by default and detected
  only when formatting comparison is explicitly enabled; tests modify copies,
  never files under `测试用例`.
- The fixed publish destination remains
  `artifacts/publish/Differ-win-x64` with no ZIP or parallel version directory.

## Result

Passed for the 0.8.0 theme, keyboard, Office-default, and repository-hygiene
scope. Historical 0.5.0 TortoiseSVN registry screenshots are not treated as a
fresh integration reconfiguration test; this pass did not modify the user's
current TortoiseSVN settings.
