# 代码/文本修复记录（2026-10-24）

原工程核对：0.15.0 的 TextDiffAdapter 使用 SideBySideDiffBuilder 后丢弃 SubPieces，主界面 TextDiffView 是双只读 DataGrid。实际路径在 src/Differ.App 根目录，而非任务书隐含的 Views 子目录。本轮保留原控件用于二进制只读视图；代码/纯文本的生产入口使用 CodeEditorView，全文件编辑只有这一套实现。

## 编辑器路线验证与最终选择

首先下载并运行了 Monaco Diff Editor 0.52.2 + WebView2 SDK 1.0.3537.50：真实 Windows Runtime 155.0.4283.62 上通过本地资产加载、块采用、撤销/重做、跨行中文文本/Tab、版本结果与保存自动探针。随后核对该锁定版 monaco.d.ts 发现公开 IDiffEditorOptions 不提供忽略大小写或自定义 diff provider；忽略空白仅为 ignoreTrimWhitespace，而既有语义需贯穿匹配和行内高亮。其公共 IDiffEditor 也不暴露 quitEarly。继续把其结果作为唯一权威会静默损失旧功能。

最终采用 **双 Monaco standalone 编辑器 + C# 宿主唯一差异结果**。没有调用 createDiffEditor，没有同时计算 Monaco 与 DiffPlex 两份全文差异，也没有引入 AvalonEdit。Monaco 只负责完整文档、原生编辑历史、语法高亮、字符选择/搜索、输入和虚拟视口；宿主 EditorDiffEngine 为块、行、统计、概览、搜索、导出、采纳范围提供同一版本数据。左右对齐由宿主映射产生公开 changeViewZones 和 deltaDecorations；字符细节仅在当前视口按需计算，缓存上限 512 项。中文输入法组合候选确认须另做 Windows 实测，自动脚本插入中文不能代替该项。

Monaco MIT 许可证在 src/Differ.App/Editor/MONACO-LICENSE.txt；官方 npm 0.52.2 tgz SHA-256：C280CDCF0B0C13D1A2BF01AF958D4387ED06D7F6C918401D00C4ADCAE1BC72B6。全部 JS、CSS、worker、字体随 Editor/vs 分发。WebView2 只映射本地虚拟主机，阻止外部导航/新窗口/下载/权限请求及非本地主机资源；程序不从网络加载编辑器。WebView2 x64 独立离线 Runtime 由安装器统一打包，具体签名/安装验收见总报告。未有干净断网 Windows VM，不能据本机已有 Runtime 声称干净机通过。

官方依据：
- https://github.com/microsoft/monaco-editor/tree/v0.52.2
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- 锁定包 monaco.d.ts 的 IDiffEditor、IStandaloneCodeEditor、changeViewZones、executeEdits、pushUndoStop、deltaDecorations 和 folding provider 公共定义。

## 关键实现

- `EditableTextSession` 分离 BASE、结果内存、版本、保存目标、来源、初始编码/BOM/换行、目标 SHA-256；支持捕获不可变比较快照。旧结果和旧块坐标均不能覆盖新版本。
- `EditorDiffEngine` 剥离连续相同前后段，以唯一行和最长递增序列找稳定锚点；只在既有 4M 矩阵预算及 1M 字符区间预算内调用 DiffPlex。大无锚点区域退为完整变更块并警告；循环检查取消，不提高矩阵限额。匹配所有分支使用同一空白/大小写规范化。
- 页面编辑以 100 ms 短批合并全文 IPC，先立即使旧结果失效；宿主沿用两 worker 调度。重算只替换投影，不 setValue，因此不重置撤销链、光标和选择。宿主 UI 应用期间不触发自动定位。
- 采用左侧块仅替换权威偏移范围，并在 executeEdits 前后 pushUndoStop；首尾空范围包括必要的行分隔符。保留右侧不改文本，移动到下一块。保存是独立动作。
- 显式 SVN 调用来源 + WC/WORKING 修订 + SharpSvn 本地只读 GetInfo 验证 + 快照内容哈希一致，才确认真实工作文件为保存目标。旧协议、历史文件与未知来源默认另存。用户主动选择另存目标后成为明确的普通本地目标。BASE 原始路径、两侧快照和其他会话保护路径不能被保存覆盖。
- 无编辑原位保存不重写字节。UTF-8 BOM、UTF-16 LE/BE、GBK/GB18030、CRLF/LF/CR、末尾换行保留。混合换行编辑后保存必须明确选择 CRLF/LF。不可编码字符抛明确错误，不替换为问号。
- 写入临时同目录文件后校验目标 SHA-256，最终校验期间持有拒绝写入的目标句柄，再 File.Replace；新目标使用拒绝覆盖的 File.Move。外部变化/失败保留内存及 dirty 标记。关闭/刷新/退出先同步浏览器文档并处理未保存内容。
- 已编辑文档不从旧快照重新解码；切换编码须保存后重开，产品有明确提示。未编辑初始会话可重新解码。
- Lua 函数提示是最多回看 500 行的启发式，不参与采用范围。折叠按可靠未改区生成配对范围；不宣称 AST 语义合并。

## C01—C08 验收证据索引

| 编号 | 自动验证 | Windows 界面/用户原始案例边界 |
|---|---|---|
| C01 | EditableTextSessionTests 的空文件、首尾空范围、物理空行、EOF换行、块范围重建；CodeEditorRuntimeTests 块导航 | 原始代码案例未提供；实际窗口检查由总报告记录 |
| C02 | 实际本地 Monaco：采用→undo→redo，一次采用作为一个撤销单元；原始 BASE/MINE 不改 | 原始案例待验 |
| C03 | 实际 Monaco 跨行中文/Tab、原生撤销历史，结果投影不重建模型 | 自动插入中文不等于实际 IME；用户候选输入需单列 |
| C04 | Unknown 必须另存、原始BASE/快照保护、显式本地目标；SvnWorkingFileVerifier 独立来源测试 | Tortoise 提交窗口真实双击及历史对比由总报告区分，不拿纯协议测试代替 |
| C05 | UTF-8有无BOM、UTF-16LE/BE、GB18030/GBK、CRLF/LF、末尾换行、混合换行明确选择、不可编码字符失败 | 混合换行选择对话框属于人工验证项 |
| C06 | 外部改动拒绝覆盖、无目录写失败、dirty/内存保持、无编辑字节/时间不变 | 关闭/刷新/退出对话框、锁文件失效及安装路径另验 |
| C07 | 文档版本过期拒绝、不可变快照、会话隔离、ignoreCase/Whitespace/LineEndings、大小分支一致；实际编辑后CurrentDocument/块计数同步 | 全部快捷键与跨标签快速输入仍须窗口实测 |
| C08 | 50k稀疏变更稳定锚点、50k全重写有界退化、取消、旧块失效；行内比较使用已有受限长行回归 | 100次10k行输入+40次块/undo/redo公开编辑器事件测试另输出原始JSON；不是物理键盘延迟，也不是无界长期内存证明 |

复现：`dotnet test tests/Differ.Formats.Text.Tests/Differ.Formats.Text.Tests.csproj -c Release`；`dotnet test tests/Differ.App.Tests/Differ.App.Tests.csproj -c Release --filter FullyQualifiedName~CodeEditorRuntimeTests`。设置 `DIFFER_EDITOR_PERF=1`、`DIFFER_EDITOR_EVIDENCE=<目录>` 才启用 100 次中型样例交互采样并保存 JSON/编辑器截图；标准测试不反复跑重型性能案例。截图只作布局证据，不用来证明性能。

当前 targeted 验证：文本项目 51 项通过；真实 WebView2 standalone 集成探针通过（约 5 秒），包含上方纯新增 viewZone 时下方锚点连续输入 5 次，光标、选区与锚点屏幕 Y 位置保持在 1 DIP 内；3 轮第二会话快速 12 次输入、切换、跨会话 ID 拒绝、第一会话内容/版本不变，每次页面模型数为 2，关闭后控件已释放。完整解决方案及 100 次交互采样最终结果，以总验收报告及最新 TRX/JSON 为准。所有 fixture 均建于临时目录，没有改动 `测试用例` 原件。

补充回归：加载时保留解码探测（含 GB18030 fallback）、两侧编码与行尾/末尾换行提示；忽略行尾不抑制编码警告。50,015 字符单行两处小改通过宿主行内 API 断言，中间 30,000 个 b 保持未改，高亮仅原/3 → 变/5。先按 Unicode 文本元素剥离共同边界，再在原有预算内精化，不提升详细比较预算。主题资源与真实编辑器 targeted 共 6 项通过。可用 DIFFER_EDITOR_VISIBLE=1 将性能测试窗口置于前台；默认仍在屏幕外运行。

Windows 按钮焦点回归：采用、撤销、重做先将 WPF/native 焦点转回 WebView，再聚焦右编辑器并执行操作；Ctrl+S / Ctrl+Shift+S 在 WPF 外壳焦点与浏览器焦点之间分别路由，浏览器已处理时外壳不重复保存。快捷键映射与真实按钮→WebView 焦点/undo/redo/外壳保存 targeted 27 项通过。
