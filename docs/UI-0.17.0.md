# Differ 0.17.0：紧凑工具栏与编辑模式

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

日期：2026-10-24。按用户确认的 [设计方案](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-10-24-editor-header/DESIGN_PLAN.md) 在原始 C#/.NET 10/WPF 工程中实施。沿用现有 Monaco 编辑器、SVN 外部比较入口、多标签、格式适配器、后台调度和虚拟化。规范同步为 `DEVELOPMENT_SPEC.md` 4.1.0，第 27.7 节。

## 可运行交付

- [Windows x64 程序](../artifacts/publish/Differ-win-x64/Differ.App.exe)，0.17.0.0，自包含 .NET 10.0.12；保持固定发布路径，须完整保留同目录依赖。
- [离线安装包](../artifacts/installers/Differ-Setup.exe)，0.17.0.0，277,362,632 字节，包含本地 Monaco 资源与微软签名的 WebView2 Standalone Runtime。
- [安装包 SHA-256](../artifacts/installers/Differ-Setup.exe.sha256)：`0E892C23B57355B878BD25AEFF4E308382AD909EBD09A5D4823028DB0B9C09E2`。
- [安装说明](INSTALLATION.md)。Differ 本体和安装器尚无商业数字签名。

已从正式发布路径启动文本样例，进程响应、比较窗口和 WebView2 子进程正常建立。发布 App/格式程序集及编辑器资源与本轮构建/源码哈希一致；安装器载荷 588 个文件，587 个清单条目逐项大小及哈希均一致，另一个文件为清单本身。发布目录仍只有 `Differ-win-x64`，没有 ZIP 或带版本号并列目录。

本轮未重新执行安装/升级/卸载的全部人工流程；安装器实现和依赖方案未变，既有生命周期结果见历史修复报告。新安装器的编译、版本、完整载荷、离线依赖门禁及哈希已重新验证。

## 实际变化

- 全局工具栏保留一组“全部 / 差异 / 上下文”互斥按钮，选中状态持续可见；移除常驻“比较选项”。原有编码、忽略规则、Excel 匹配规则保留在右侧更多菜单的“高级比较规则…”中。
- 保存为分裂按钮，撤销/重做统一放在全局工具栏。删除编辑器内部按钮行及 WebView 底部重复说明行；两侧标题高 28 DIP，仅显示来源、权限和保存状态，完整标题/路径可查看提示。
- 新文本标签默认关闭“编辑模式”。开启后右侧可输入、粘贴、采纳左侧块、撤销和重做；左侧 BASE 始终只读。每个标签独立保留开关状态；Excel、Word、CSV、二进制继续只读审阅。
- 关闭编辑时先冻结输入，再同步最后一批文本并确认。保留当前内容、未保存状态、光标/选区及撤销历史；关闭后已有未保存内容仍能保存。保存中显示“保存中…”，不会因关闭模式而自动写盘。
- 保存权限继续独立确认：未知或历史来源显示“结果副本”，使用另存；不能通过开启编辑模式获得 BASE、快照或未经确认工作文件的写权限。保留外部变更指纹校验、编码/换行保护和失败时保留内容。
- 当前差异块有双侧轮廓及准确行范围；中缝箭头采纳当前块，支持 Ctrl+Alt+→。纯新增/删除使用另一侧空范围，不把上下文写入覆盖区域。
- 底部只保留一条全局状态栏。编码拒绝、重算失败和模式同步失败在此可见，不被后续光标消息覆盖。刷新时旧会话的高亮任务被取消，迟到结果再校验会话身份，避免新旧会话都从 v1 开始时串用旧结果。

主要源码：`DiffWindow.xaml/.cs`、`CodeEditorView.xaml/.cs`、`DiffWindowViewModel.Chrome.cs`、`DiffWindowViewModel.Editing.cs`、`Editor/editor.js/.css/index.html`、主题与本地化资源。`Loading.cs` 仅在成功回退到非文本格式后清理旧编辑能力。未改变 Excel/Word 比较算法。

## 本轮验证范围

修改前已保存源码快照及哈希，Release 基线构建通过、536 项测试通过，位于 `artifacts/ui-implementation-2026-10-24`。

真实 WPF/WebView2 自动验证包括：默认只读、原生编辑器输入/粘贴门禁、关闭后尾输入同步、开启关闭不重建模型、关闭后保存、撤销历史、多标签隔离、来源保护、刷新旧结果失效、纯增删导航及中缝采纳、全局快捷键焦点分流、三选按钮和更多菜单。

布局检查覆盖 720 / 960 / 1500 DIP 宽度、浅深主题、100 / 125 / 150 / 200% 模拟输出 DPI。发现并修复了独立控件资源缺失及 125% 输出下撤销/重做间距问题。

最终 `dotnet build -c Release --no-restore` 通过，0 错误、0 警告；`dotnet test -c Release --no-build` **551/551 通过，无跳过**；`dotnet format Differ.sln --verify-no-changes --no-restore` 通过。App 测试由 233 增至 248，其余格式/核心/SVN 回归全部通过。构建、测试、格式日志和 TRX 位于 `artifacts/ui-implementation-2026-10-24`。

## 同环境性能对照

复用不变的 `RepairUiBench`，直接引用改前 0.16.1 发布 DLL 与改后 0.17.0 Release DLL。相同 Windows/.NET 10.0.12、相同输入哈希、1400×820 DIP、相同浅色主题，每版三个独立进程，每轮预热后 100 次 Word small-scroll；测量期间无其他 Differ 构建或测试。

| 指标（三轮中位数） | 0.16.1 | 0.17.0 |
|---|---:|---:|
| 首次可操作布局 | 187.901 ms | 188.269 ms |
| 滚动 p50 | 7.169 ms | 7.343 ms |
| 滚动 p95 | 8.751 ms | 8.630 ms |
| 100 次操作累计分配 | 67.829 MiB | 67.696 MiB |
| 进程峰值工作集 | 225.012 MiB | 221.219 MiB |
| 每轮 GC Gen0 / 1 / 2 | 1 / 0 / 0 | 1 / 0 / 0 |

数据支持该案例无明显退化，不声称普遍加速。此次测量不等价于屏幕实际帧率、物理键盘/IME 延迟或编辑器专项性能。完整范围、哈希、原始事件及复现命令见 [性能记录](E:/Differ-归档/2026-10-24-cleanup/artifacts/ui-implementation-2026-10-24/performance/PERFORMANCE_COMPARISON.txt)。

## 效果截图

以下由真实生产 WPF 控件渲染。代码截图将 WPF RenderTargetBitmap 与相同窗口 WebView2 CapturePreview 按实际子窗口边界组合；不是设计稿，也不是桌面人工截图。Office 截图使用真实适配器和可重复的中文样例。

- [代码：编辑模式、当前块和中缝采纳](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.17.0/editing-cs-light-960x680-100.png)
- [代码：深色主题](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.17.0/editing-cs-dark-960x680-100.png)
- [Word：中文正文及当前区域](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.17.0/reading-docx-light-1500x860-100.png)
- [Excel：对齐网格及底部内容详情](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.17.0/reading-xlsx-light-1500x860-100.png)
- [渲染方法与 DPI 边界](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.17.0/DPI_METHOD.md)

## 验收边界与历史记录

本次为已批准 UI 方案的实际落地，原任务书 E01—E10、W01—W08、C01—C08、I01—I05 的逐项记录继续见 [修复验收报告](repair-2026-10-24/DELIVERY.md)。该报告中的 0.16.0 产物版本、大小与哈希仅是历史记录；当前产物以本报告和同目录校验文件为准。

本轮新增验证加强 C01—C07（导航、采纳、编辑、保存及会话一致性）和 I02/I04（布局及异步生命周期）。没有把旧报告未验项目升级为通过：原始 BASE/工作副本配对案例、真实 TortoiseSVN 双击完整流程、原生中文 IME 候选确认、四档操作系统/多显示器 DPI、缺 WebView2 Runtime 的断网干净机、长期压力运行仍需相应环境验证。既有 Excel 远距离跳转性能不足也没有因本次 UI 改造而宣称解决。

本机桌面自动化接口在绑定 Differ 窗口时返回窗口归属错误，因此本轮交互证据采用真实控件/编辑器自动测试，并明确不称为 Windows 人工验收。源文件、测试用例目录和真实 SVN 配置未用于写入测试；未执行 SVN update/revert/add/delete/commit 或自动重新绑定。
