# Differ

面向 Windows 的文件差异查看工具，支持代码、文本、Word、Excel 和三方文本合并，可直接打开本地文件或从 TortoiseSVN 调用。

## 最新开发版：0.21.18

更新日期：2026-11-13。

**[下载 0.21.18 Windows x64 安装包](https://github.com/Fahaxiki123/Differ/releases/download/v0.21.18/Differ-0.21.18-win-x64-Setup.exe)** · [版本说明与校验值](https://github.com/Fahaxiki123/Differ/releases/tag/v0.21.18) · [对应源码](https://github.com/Fahaxiki123/Differ/tree/v0.21.18) · [历史版本](https://github.com/Fahaxiki123/Differ/releases)

0.21.18 开发预览安装包已发布，约 265 MiB，内含 .NET 10.0.12 和 WebView2 离线运行组件。安装包未数字签名；已有安装会在原目录升级，“设为 TortoiseSVN 差异查看器”默认不勾选。程序测试、安装包校验及未验证范围见[安装包交付记录](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/RELEASE-0.21.18.md)。开发源码位于 `codex/ui-system-phase1`，默认 `main` 保留原稳定应用代码。

### 本次更新

- **主题切换**：修复右上角窗口按钮区域的矩形接缝，以及语法文字残留旧主题颜色的问题；支持连续和快速反向切换。
- **窗口交互**：完善最小化、最大化、还原和关闭按钮的按住、拖出、取消与恢复行为，保留 Windows 11 贴靠菜单及未保存关闭保护。
- **精确查找**：正确定位 BASE 或右侧的实际命中范围，长行结果自动进入可见区域，连续查找保持输入焦点。
- **另存提示**：显示实际保存目标，区分原始比较来源，并说明 F5 刷新读取的文件。
- **比较状态**：修复差异概览更新时的异步状态问题，主题切换保留文档内容、选区和撤销历史。

713 项自动测试全部通过；本机实测覆盖双向主题切换、窗口按钮、贴靠菜单、保存与关闭保护、窄窗和 150% 阅读缩放。构建保留既有依赖审计告警，测试条件及未验证范围见完整版本说明。

## 使用方式

真实使用流程：

```text
打开 TortoiseSVN 提交窗口
    → 双击一个已修改文件
    → TortoiseSVN 把 BASE 和当前工作文件传给 Differ
    → Differ 打开或复用主窗口，并新增一个顶部文件标签
    → 继续双击其他文件时，新对比合并到同一窗口
    → 关闭 Differ 后回到原提交窗口
```

Differ 不扫描工作副本、不维护提交文件列表，也不执行提交。主流程不需要 `svn.exe`。

## 当前支持

- 三方文本合并核心样板：共同基础/我的/对方只读，独立结果；自动合并不冲突修改、逐块采纳、手工确认、撤销重做、安全保存。合并使用独立进程等待用户完成，普通双栏多标签不变。
- 直接启动后可选择两个本地文件比较，或选择三方文本与结果路径。当前 SVN 合并绑定保持原状，集成页提供可复制命令模板。
- 常见代码及纯文本的行级差异、编码和换行提示。
- 代码及纯文本使用双侧完整 Monaco 文档，默认只读；开启编辑模式后右侧可连续编辑，BASE始终只读。支持语法、搜索、按块导航、中缝采用左侧块、撤销重做与独立安全保存；关闭模式保留未保存内容和历史。差异由宿主有界 DiffPlex 统一计算。
- `.docx` 的段落、表格、页眉页脚、图片哈希和修订提示。
- `.doc`（Word 97，正文段落与表格单元格文本）与 `.rtf` 复用 Word 文档视图；`.xls`、`.xlsm` 与 `.csv` 复用工作表网格视图。
- 无法结构化解析的任意文件回退到左右十六进制字节级比较，不存在“不支持”的空洞格式。
- `.xlsx` 使用左右原表网格显示完整工作表上下文，保留真实行号、列名和工作表标签。
- Excel 修改只高亮发生变化的单元格，并在单元格内继续标出具体增删文字；公式、批注、合并区域及隐藏状态仍参与比较。
- Excel 选中单元格使用完整四边轮廓，左右对应格联动；轮廓不改变行高或覆盖差异底色。大表使用轻量、可回收的单元格呈现，避免普通文本反复构建富文本控件。
- Excel 在底部显示当前选中单元格的 BASE/当前完整内容，长文本和公式不再依赖主表格列宽；详情可折叠，展开状态与高度按格式记忆。
- 多个文件在同一个主窗口中通过顶部标签切换，不再重复打开多个对比窗口。
- Excel 工作表在内容区底部通过工作表标签切换，一次只显示当前工作表。
- 修改、新增、删除、移动和冲突使用语义色；Excel 通过单元格底色、行号状态条和文字片段标记避免整行染色。
- 浅色/深色主题覆盖文本、Word、Excel、集成页和自绘缩略概览；启动与运行期间默认跟随 Windows，也可用比较工具栏按钮手动切换。
- Word/Excel 默认只比较内容、值与公式，单纯格式变化不计为内容差异。
- 左右表格严格共用列宽和行高，并同步横向、纵向滚动；支持显示/隐藏未修改项和上一个/下一个差异。
- BASE/当前文件信息压缩为单行表头，提示收纳到状态栏，尽可能把空间留给比较内容。
- 界面使用中性灰/石墨双主题、紧凑标签与工具栏、右侧差异导航轨；文本详情首次默认折叠，Excel 首次紧凑展开，用户选择与旧详情高度会保留。
- TortoiseSVN 两侧显示名称和修订信息。

后续 TortoiseSVN 调用会先保存独立的 BASE/MINE 会话快照，再把请求交给已经运行的主窗口，避免 TortoiseSVN 清理临时 BASE 后导致已打开标签失效。

## 快捷键

右侧差异导航轨可悬停查看双侧内容，单击或拖动定位，滚轮滚动正文；可视范围会随正文滚动变化。显示范围提供“全部内容 / 仅差异 / 差异上下文”，上下文模式保留变化前后 3 行，点击折叠提示可展开。

Excel/CSV 可在更多菜单的高级规则中指定关键列，例如 `A` 或 `A,C`。Excel 空键/重复键只局部降级，已确认的唯一键保持对应；CSV继续兼容原有对齐。Excel可在工作表规则中配置字段行、辅助行、数据起始、人工列对应、组合键与冻结列。当前规则与尚未应用的修改会明确显示；可在菜单中保存为该格式的预设，或恢复默认。

“更多操作”提供字体缩放、标签管理、当前差异 HTML 报告和选中内容导出。缩放、窗口位置尺寸和底部详情高度保存在本机；导出不会覆盖原件或活动快照。

更多菜单的高级规则可调整文本编码、忽略空白/大小写/行尾提示，以及Word/Excel格式比较。修改后选择应用并重新比较：文本编辑会话基于当前内存文档，其他格式基于固定快照，不自行改写原文件。

Excel 的单元格和结构变化共用差异导航：隐藏列、合并范围、工作表名称或顺序变化会在底部完整显示双侧详情。工作表有效区域中的空白行列会保留。

结构化解析失败时会尝试只读十六进制比较，并保留警告。资源限制、权限和文件丢失错误不会被掩盖：文本限制为 64 MiB / 100 万行，表格受 10 万行与 100 万展开单元格预算保护，二进制限制为 8 MiB。

- `F7` / `F8`：上一个 / 下一个差异。
- `Ctrl+F`：查找，`Enter` / `Shift+Enter` 跳转下一处 / 上一处匹配。
- `Ctrl+G`：定位原始行号、`A123` 或 `工作表!A123`。
- `F5`：保留当前 BASE，只刷新工作文件快照；失败保留旧结果。
- `F9`：切换浅色/深色主题。
- `Ctrl+U`：显示或隐藏未修改项。
- `Alt+Left` / `Alt+Right`：聚焦 BASE / 当前侧内容。
- `Ctrl+W`：关闭当前文件标签。
- `Ctrl+Tab` / `Ctrl+Shift+Tab`：切换下一个 / 上一个文件标签。
- `Ctrl+滚轮`：同步缩放正文，缩略预览保持固定宽度。
- `Esc`：先关闭查找/定位/标签筛选界面；没有局部界面时关闭比较窗口。

## TortoiseSVN 调用协议

推荐的全局差异查看器命令为：

```text
"<安装目录>\Differ.App.exe" --base %base --mine %mine --base-name %bname --mine-name %yname --base-revision %brev --mine-revision %yrev --file-name %fname --source tortoisesvn
```

TortoiseSVN 的 `%base` 是原始文件，`%mine` 是当前工作文件。开发时也可以直接传入两个文件：

安装按钮把以上命令写入当前用户的
`HKEY_CURRENT_USER\Software\TortoiseSVN\Diff`，因此可以直接在
TortoiseSVN“设置 → 差异查看器”主页面确认或手动修改当前生效的程序。

```powershell
.\Differ.App.exe "C:\temp\base.xlsx" "C:\work\current.xlsx"
```

直接无参数运行时，可选择本地双文件比较或三方文本合并，并查看集成状态与故障排查信息。

集成页底部可展开“版本与诊断信息”。运行日志只记录受控的事件信息和异常类型，不保存文件正文、单元格值或完整路径。

集成页提供“安装到 TortoiseSVN”和“恢复原差异工具”。TortoiseSVN 的高级扩展名绑定优先于全局 `Diff`，因此安装前会备份全局路径及全部受支持格式的高级绑定，随后清除这些覆盖，让设置页显示的 Differ 全局路径真正接管代码、文本、Word、Excel 和 CSV。恢复时只写回 Differ 仍持有的全局值及仍为空的高级项；安装后重新配置的工具会被安全保留。

## 发布

### 离线安装包

可构建带正式应用图标的当前用户离线安装包 `artifacts/installers/Differ-Setup.exe`，面向 Windows 10/11 x64。内含 .NET 运行组件；无需 Office 或开发环境。TortoiseSVN 仍由用户或公司 IT 单独安装。

SVN 绑定是默认不勾选的可选项，按目标电脑的实际安装路径生成配置，先备份其原有工具及支持扩展名的高级覆盖。卸载仅恢复该安装位置持有的绑定，并保护安装后的人为修改。公司电脑的安装、绑定、升级和卸载步骤见 [离线安装说明](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/INSTALLATION.md)。

生成安装包前先运行下面的发布脚本，再运行 `scripts/build-installer.ps1`。安装器与 SHA-256 文件放在独立的 `artifacts/installers`，不混入软件发布目录。当前安装包未数字签名，企业白名单策略需由公司 IT 审核。

### 固定目录软件

项目只保留一套固定名称的发布副本：

```text
artifacts\publish\Differ-win-x64\
```

后续功能修改完成后运行：

```powershell
pwsh -NoProfile -File .\scripts\publish.ps1
```

脚本先还原 `win-x64` 依赖，再生成暂存副本，验证可执行文件后才覆盖这套目录；依赖还原或发布失败时会保留当前可用版本。它不生成 ZIP，也不创建 `Differ-0.5.0-win-x64` 一类带版本号的并列目录。程序内部版本号仍可正常递增，TortoiseSVN 调用路径则始终保持稳定。

## 开发验证

从GitHub克隆后的依赖、源码边界与构建步骤见 [干净源码构建说明](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/BUILDING.md)。默认测试不需要原始业务文档。

```powershell
dotnet restore
dotnet build .\Differ.sln -c Release
dotnet test .\Differ.sln -c Release
dotnet format .\Differ.sln --verify-no-changes
```

项目长期规范见 [`docs/DEVELOPMENT_SPEC.md`](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/DEVELOPMENT_SPEC.md)。

## 每次任务结束的目录收尾

完整验证与清理使用 PowerShell 7：`pwsh -NoProfile -File scripts/verify-and-clean.ps1`。它运行 Release build/test、格式检查，无论验证是否成功都清理有明确白名单的输出，并执行目录检查；日志与测试结果保存在项目外。失败时先修问题，再重跑，不能将未通过的清理当作完成。

单独检查使用 `scripts/clean-workspace.ps1 -Check`，清理使用 `-Apply`。历史设计、源码快照、性能基线和截图已迁往项目外，见 [归档索引](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/ARCHIVE_INDEX.md)。正式程序与安装包仍在原固定路径。详细保留/清理边界见 [目录维护规则](https://github.com/Fahaxiki123/Differ/blob/codex/ui-system-phase1/docs/WORKSPACE_HYGIENE.md)。
