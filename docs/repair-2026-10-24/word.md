# Word 修复与验收记录（2026-10-24）

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](../ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

范围：原始 `Differ.Formats.Word`、WPF Word 展示链路、共享字符细化。对应任务书 W01–W08；不写回 Word，不运行宏，不改用户原件。

## 已核实原因及实现

- 当前 0.15.0 源码确有 `WordBlockComparer`、`TextSimilarity.Jaccard` 与 `SequenceAligner.PairReplacements` 的任务书定位：中文无空格时整个句子成为一个 token，微改可能得到零相似度；未匹配段落按下标成对处理。
- 新增 Word 专属 `WordUnitAligner`，不改 Excel/CSV/二进制使用的通用 `SequenceAligner`/`TextSimilarity`。先以相同唯一标题划定章节，再用唯一段落的单调锚点分段；局部窗口采用中文字符二元片段、长度比及短段受限编辑距离。局部矩阵最多 16,384 候选，全篇最多 100,000 候选，字符样本最多 2,048。超预算保留两侧原顺序并明确“未确定对应”，不按下标假定修改。准备、匹配、节点和组生成边界均检查取消。
- `WordBlockComparer` 保留全部原始叶子节点、索引和元数据；连续无可靠对应的段落附同一 `word.changeGroup`。完整文本串相同的 1:N / N:1 拆合段标为 `split-merge`，用跨段流式比较避免复制两份全文。20→5 等重写共同分组，稳定锚点后重新对齐。先分组再过滤，隐藏未修改节点不会把远处变化误合成一组。
- `WordDisplayRowViewModel` 只对未确定对应组采用左右独立段落堆栈和共享组高度；已可靠配对的段落保持同排。长组默认共同预览 3 段，明确省略数量；展开后一次最多生成两侧各 24 段，可翻页。搜索定位可以切到目标窗口；完整内容仍留在搜索、复制、导出和细项统计中。导航以组锚点为单位，统计区另列段落/表格单元格细项。
- `WordParagraphPresenter` 用 WPF 只读 FlowDocument 的真实 `Paragraph.TextIndent` 表示首行/悬挂。普通左缩进、首行偏移、列表标记各自定位，负缩进在容器内预留空间以免裁切；极端显示缩进限幅不写入比较模型。沿用外层虚拟化，分组内部有窗口预算。
- 实测发现所有段落使用 RichTextBox 会让中型 Word 导航出现 1 秒尖峰，因此增加 `WordParagraphHost`：普通段落/无首行偏移的组内段落只创建原轻量 `WordInlineDiffTextBlock`；真实非零首行/悬挂或用户双击、F2、右键“选择段内文字”时才创建只读 RichTextBox。换段/回收释放字符选择文档，不用隐藏重控件假装快路径。
- Word 修改段落的字符差异改为两侧共用一个惰性结果，未显示内容不预计算字符矩阵；`IgnoreWhitespace`/`IgnoreCase` 贯穿匹配与细化。`WordInlineDiffTextBlock` 的种类/侧别更新只重设 Run 样式，不重新生成文字。新段落呈现合并同轮绑定带来的重建。
- 共享 `InlineTextDiffer` 对长行增加受限相同片段锚点，前后两处修改之间的共同长文本不再无条件整片高亮；预算降级返回 `IsApproximate`。Word 段落提示可显示“文字细化已达到预算”，没有宣称其为精确字符结果。
- 性能事件新增 `word.parse`、`word.match` 和 `word.match-bounded-fallback`，接入主程序既有 `performance.log` 监听；不记录正文。原矩阵上限没有移除或调高。

## 自动验证

| 编号 | 结果与证据 |
|---|---|
| W01 | 通过。`WordRepairRegressionTests.W01ChineseMicroEditIsOneModifiedParagraph` 的中文一字、数字和中英混排案例均修改 1 段、新增/删除 0；仅目标字符高亮。 |
| W02 | 通过。插入 X 后，B 3→5、C 10→20 均对应原段，X 单独新增。 |
| W03 | 通过受控算法场景。重复“注意”不跨稳定章节配对；5000 段无锚点区域走明确预算降级并无内容丢失；`WordCancellationTests` 覆盖预取消、5000 段准备中的取消、末节点生成取消。降级有文档警告及 `word.match-bounded-fallback` 事件。 |
| W04 | 通过。1:2、2:1、1:3 拆合均保留原顺序与全部共同文本，形成一个拆合组。 |
| W05 | 通过。20→5 重写前后锚点不变，一个双侧组；默认 3 段预览、展开恢复、复制完整；5000 段组定位第 4001 段时呈现窗口仍 ≤24 段，所有 5001 个叶子仍在搜索索引。 |
| W06 | Windows WPF 自动布局通过。`WordRepairPresentationTests` 实际建立只读段落并取得 TextPointer 字符矩形：正首行缩进的首行 x 大于后续行，悬挂缩进相反；列表标记与正文位置单独断言。窄窗 220 DIP 文本宽度有真实换行。 |
| W07 | 通过受控字符场景。2,500 个共同中文片段前后两处小改只突出目标数字；无共同内容的长段返回可诊断粗略高亮；惰性结果在展示构造时未求值。 |
| W08 | 通过。忽略空白/大小写保持原文但消除无关高亮；既有格式开关、表格结构和插行测试继续通过；共同折叠/展开不改变细项总数，IncludeUnchanged 过滤不跨锚点合组。 |

本子任务独立运行结果：Word 项目 **42/42**；Core 项目 **54/54**；新增 Word WPF 呈现测试最初 **7/7**。随后截图审查发现悬挂首字裁切风险，新增 `first.Left >= 0` 断言先复现失败（−19 DIP），改用 RichTextBox 自身 Padding 后正首行/悬挂两项均通过，并断言完整 TextRange。另补页尾搜索命中必须成为窗口首段的回归，交主代理统一最终运行；完整解决方案及最终 WPF 项数以主交付报告为准。后续加跑 App 组合测试曾遇主代理正在运行的源工程 EXE 文件占用，未将该次构建计为测试通过，也未强制终止窗口。

命令：

```powershell
dotnet test tests/Differ.Formats.Word.Tests/Differ.Formats.Word.Tests.csproj -c Release --no-restore
dotnet test tests/Differ.Core.Tests/Differ.Core.Tests.csproj -c Release --no-restore
dotnet test tests/Differ.App.Tests/Differ.App.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WordRepairPresentationTests
py -3 scripts/generate-word-repair-fixtures.py
```

## Windows 界面及样例

专属样例：`artifacts/repair-2026-10-24/word-fixtures/word-review.base.docx` 和 `word-review.mine.docx`。生成器为仓库内 `scripts/generate-word-repair-fixtures.py`（只用 Python 标准库）；含中文 3→5、首行缩进、悬挂缩进、编号列表、20→5 重写和后续稳定标题。性能样例继续使用主报告固定的 `samples/word-small`、`word-medium`、`word-large`、`word-rewrite`，没有为性能前后测量改换输入。

主代理已在当前 Windows 真窗口检查首行/悬挂、中文微改和 20→5 组展开/折叠，关键截图及最终验证范围由主交付报告统一汇总。WPF 自动矩形断言属于 Windows 自动验证，不等同全部 DPI 的人工验收。100%/125%/150%/200% 监视器缩放及真实用户原始 Word 案例未由本子任务独立验证，不宣称通过。

## 性能工具及边界

新增 `scripts/RepairUiBench` 独立 WPF 性能工具，`SourceRoot` 可指向保存的 baseline-source 或当前工程；两个目标均已编译验证。每项 100 次真实视图滚动、Excel DataGrid 单元格选择、Word 差异导航、切表和切文件，输出 p50/p95/max、分配、GC、工作集和 Dispatcher heartbeat；预算预设为 100 ms p95、16.67 ms 布局周期和 200 ms 无响应上限。主代理统一串行运行前后测量，具体数字以性能报告为准。离屏 WPF 布局/heartbeat 不能当作真实显示器 60 Hz 呈帧证明。

Word 普通段落快路径的局部复测（各 100 事件，固定 word-medium，非全套最终报告）：小步滚动 p95 **14.71 ms** / max **36.47 ms**，滚动条远跳 p95 **47.89 ms** / max **57.88 ms**，下一变更区域 p95 **44.49 ms** / max **52.73 ms**。全部低于 100 ms 操作预算，小步滚动 p95 低于 16.67 ms 布局预算。证据 `ui-word-fast-small-scroll.json`、`ui-word-fast-jump.json`、`ui-word-fast-navigation.json`；改前全段 RichTextBox 分支出现的 >1 秒尖峰在这次局部复测中未再出现。普通/远跳动作已明确分开，未降低原预算；完整前后结果由主代理最后同脚本复测汇总。

仍有明确能力边界：超过预算且无可靠锚点的改写区只做组级对应；不尝试跨章节语义理解；只对完整相同串证明拆合关系；复杂 Word 页面排版、分页、浮动对象不在本轮承诺范围。上述情形保留全部原文和可恢复导航，不写回源 Word 文件。
