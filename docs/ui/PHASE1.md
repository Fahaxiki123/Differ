# Differ 界面体系第一阶段

基线为 `origin/codex/preview-0.20.0` 的 `13e1a210961ae60b604b93ecf1d26b25372117d6`。本地实施分支为 `codex/ui-system-phase1`。检查时远端与本地预览引用相同，原工作区干净。编译环境为 Windows x64 与 .NET SDK 10.0.401，仓库继续使用 WPF、WebView2、Monaco、CommunityToolkit.Mvvm 和 DiffPlex。

## P1-01 资产清点

| 位置 | 第一阶段用途 |
|---|---|
| `Differ.App/Themes`、`App.xaml`、`ThemeService` | 保留 App 单一主题根，组合新通用令牌与现有差异、兼容资源 |
| `DiffWindow`、`CodeEditorView` | 迁移公共工具栏、文件标签、侧标题、查找/定位和状态区。保留原命令、同步与保存入口 |
| `PersistentContentHost`、`DiffOverviewCanvas`、`SpreadsheetCellPresenter` | 保留缓存、绘制和虚拟化。概览与单元格自绘通过动态画刷依赖属性接收主题更新 |
| `Editor/index.html`、`Editor/merge.js` 与本地 `Editor/vs` | 保留现有协议与离线依赖。文本比较通过原 palette 消息更新呈现 |
| `ComparisonTemplateRuntimeTests`、`CodeEditorRuntimeTests`、`CodeNavigationFeedbackTests`、`TextMergeRuntimeTests` | 真实 WPF/WebView2 测试、布局、导航、输入与三方合并。现有捕获类型与本轮桌面证据分开记录 |

旧键按实际消费方保留。`Brush`、`Color` 和布局类型各自保持一致。

| 旧资源 | 新角色 | 类型 | 消费页与移除条件 |
|---|---|---|---|
| `Sample.ToolbarBrush`、`Sample.PaneHeaderBrush`、`Sample.SurfaceBrush` | `Dui.Surface.Toolbar`、`Dui.Surface.Content` | Brush | 文本样板迁移，保留尚依赖旧字典的页面和测试 |
| `Sample.AccentBrush`、`Sample.BorderBrush`、`Sample.InkBrush`、`Sample.MutedInkBrush` | `Dui.Accent`、`Dui.Border.Subtle`、`Dui.Text.Primary`、`Dui.Text.Secondary` | Brush | App 兼容资源保留，待三方与回退页面迁移后扫描 |
| `Sample.ToolbarButton`、`Sample.PrimaryButton`、`Sample.SelectedButton`、`Sample.EditModeToggle` | 工具按钮、主要操作、单选分段、模式切换 | Style | 新双栏只使用 `Dui.*`，其他调用方完成迁移后再删除旧样式 |
| `Ui.ToolbarRowHeight`、`Ui.FileTabHeight`、`Sample.PaneHeaderRowHeight`、`Ui.StatusBarRowHeight` | `Dui.Metrics.*` 对应外壳度量 | GridLength / Double | 旧高度继续服务未迁移页面。移除前检查全部 XAML |
| `Sample.BodyFontSize`、`Sample.MissingBrush`、`Sample.LineNumberBrush`、`Sample.IndentBrush` | `Editor.*` 文档字号、占位、行号与缩进 | Double / Brush | 文本比较迁移后保留旧合并 palette 兼容入口 |
| `Diff.*`、`Overview.*`、`Banner.*`、`Badge.*` | 业务差异、警告与概览 | Brush / Color | 继续在 App 管理，迁移外观时保持语义 |

## 后续页面迁移顺序

| 顺序 | 页面 | 当前范围 | 下一阶段边界 |
|---|---|---|---|
| 1 | `MergeWindow`、本地合并编辑器 | 保留原正文和合并工具 | 命令靠近结果。保留采用、确认、自动下一处和保存退出语义 |
| 2 | `SpreadsheetDiffView`、`SpreadsheetWorksheetDiffView`、底部详情 | 接入公共比较外壳 | 保留网格、冻结、双侧选择、虚拟化、片段高亮和底部工作表标签 |
| 3 | `WordDiffView`、段落和表格控件 | 接入公共比较外壳 | 保留文档排版、段落及表格复制 |
| 4 | `TextDiffView` 及比较模板选择器承载的回退视图 | 接入公共比较外壳 | 保留字节偏移、状态解释与双侧导航 |
| 5 | `MainWindow`、文件选择、规则/预设、导出、诊断与确认 | 保留原入口 | 延续本地操作、参数校验和 SVN 集成边界 |
| 6 | 全局菜单、空/错/加载、历史资源 | 迁移文本样板相关入口 | 逐个核对消费方、双主题及真实交互，再移除旧资源 |

以上页面的视觉认可待用户验收样板后分别确认。

## P1-02 至 P1-04 实施结果

- App 与 Gallery 引用同一个 `Differ.UI` 程序集。浅、深色字典提供同键同类型的令牌，显式样式保留 WPF 原控件语义。用途、状态、代码示例见 [组件目录](COMPONENTS.md)。
- App 通过原 `ThemeService` 替换单一主题根，并在同一字典下更新 `Theme.Revision`。活动和缓存的 Monaco 视图复用 `theme` 消息刷新颜色。资源通知与待执行更新随视图释放。原编辑、保存和差异计算入口照常使用。
- 实际双栏使用中性外壳、32 DIP 文件标签、矢量操作图标和清晰的只读状态。当前块为靛蓝轮廓，普通修改组的圆头提示为琥珀色。长文件名可查完整提示，选中的溢出标签自动滚入视口。720 DIP 保留导航、权限与保存入口。
- 真实验收发现深色菜单把菜单项样式误用于分隔线。修订后保留原生菜单角色、子菜单、分隔线、复选与快捷键，并补充 STA 运行回归。

## P1-05 验收记录

证据根目录为 `D:\Differ-UI-Phase1`。原样 0.20.0 的还原、构建和格式检查通过。隔离基线全量测试 641 通过、1 失败。失败用例原先要求关闭标签后立即删除快照，但生产服务允许 Windows 文件占用时有限重试。本轮仅让断言等待重试，保留对原件、保留会话和已关闭会话的全部断言。

最终还原、Release 构建、全量测试及格式检查退出码均为 0，649 项测试通过。完整日志位于 `validation`，包括普通配置 Release 构建和隔离配置测试。结构测试核对双主题键和类型、合并顺序、对比度、无业务反向依赖及菜单分隔线。WebView2 专项检查缓存模型、版本、光标、选区、滚动与撤销保持。运行时捕获单独保存，不计入桌面截图。

| 场景 | 证据与结果 |
|---|---|
| 同条件桌面截图 | 三种尺寸、双主题各一组。起点 1440×900、1024×720、720×620 DIP，最小高度沿用 620 DIP。固定中文长路径、首行、初始全量模式和 7 个差异块。基线与最终预览均用自包含 .NET 10.0.10。窗口捕获 JPG、像素尺寸、EXE 路径和输入哈希见 `screenshots` |
| 文本比较 | 桌面实测 F8、全部/差异/上下文、长行双侧水平同步、查找定位及 Esc、深色高级规则子菜单和正文缩放。文本只读打开，右侧编辑、撤销重做、多标签保留草稿，关闭编辑后另存成功，输出逐字节符合预期 |
| Excel 与 Word | 真实桌面加载合成文件，检查双侧对应单元格、完整详情、两张工作表切换，以及 Word 段落和表格。复制与虚拟化由现有回归覆盖 |
| 三方合并 | 真实桌面验证基础显隐、采用来源、双击仅处理一处、自动下一处、撤销和保存并完成。三份输入哈希一致，另存结果逐字节符合预期 |
| 空、相同和错误输入 | 桌面验证空文件、无差异、缺失文件反馈和损坏 Office 回退十六进制比较 |
| Gallery | 最终独立窗口实测浅深主题、720 DIP 溢出、焦点轮廓、Tab/Space、鼠标确认与回焦。额外 7 项离屏运行断言覆盖资源加载和输入校验 |

性能报告见 `performance/PERFORMANCE.md`。同机离屏重复测试中，热标签切换 p95 +2.1%，主题切换 +8.9%，文本同步滚动 +2.1%，输入至权威结果渲染 −0.7%，含 WebView2 的采样峰值工作集 +1.5%。启动和 Excel 滚动首轮有波动，四组交错复测的成对变化中位数分别为 +1.7% 和 −9.9%。保留每轮原始数据、波动范围及测量边界。

未验证 125%/150%/200% 系统 DPI、跨显示器切换、中文 IME 组合输入、Windows 10/干净机断网、强制 WebView2 启动故障及长时内存稳定性。Gallery 的对话框采用原生默认确认/取消属性，自动化键盘没有稳定送达模态窗口，原生 Enter/Esc 仍需实机复核。性能探针未单独重测最后的菜单和标签滚入视口补丁。Excel、Word、三方合并沿用专用正文，尚待第二阶段视觉迁移。

## P1-06 隔离试用与安全校验

- 本机验收程序 `D:\Differ-UI-Phase1\preview-final\Differ.Phase1.Preview.exe`，产品版本 `0.20.0-ui-phase1`，信息版本后缀为基线 SHA。哈希和版本清单见 `artifact-manifest.json`。同一阶段改动的 GitHub 源码版本为 `v0.21.0`，在 `codex/ui-system-phase1` 分支维护。
- Gallery `D:\Differ-UI-Phase1\gallery\Differ.UI.Gallery.exe`。两个交付目录中的 `Differ.UI.dll` SHA-256 一致。可在 PowerShell 执行 `pwsh -NoProfile -File D:\Differ-UI-Phase1\Run-Fixture.ps1 -Case Text` 直接打开合成双栏，再运行 `-Case Second` 测同窗口标签，或 `-Case Merge` 打开合成三方冲突。
- 预览通过 `scripts/publish.ps1 -PreviewDirectory <新的仓库外绝对路径>` 发布到空目标，验证依赖和许可后再移动暂存。保留清单校验，恢复六份许可的原始字节并核对全部 37 项。预览使用独立管道、偏好、快照、日志和 WebView2 配置，预览配置目录受保存路径保护。
- 21 个合成输入重新生成后字节一致。真实 SVN 全局和高级 Diff/Merge 绑定前后哈希一致。试用只在项目外写入，源码、预览、测试输入和证据位置见交付清单。
- 清理脚本专项 11 项通过。工程 `Apply` 只清理可重建输出，随后的 `Check` 通过，交付和证据目录留在工程外。
