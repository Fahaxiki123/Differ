# 0.15.0 → 0.16.0 性能验证

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](../ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

验收日期：2026-10-24。目标在任务开始即采用任务书口径：中型样本常用操作反馈 ≤100ms，滚动目标接近 60Hz；未达目标不算通过。原规范文件/工作表切换 p95 80/100ms、最大150/180ms继续有效。

## 环境与可重复方法

- Windows 11 专业版 10.0.28000，AMD Ryzen 7 9800X3D（8核16线程），物理内存约47.66 GiB。
- .NET SDK 10.0.401 / runtime 10.0.12，Release x64，同一机器、相同输入文件、无文件内容上传。
- 修前完整源码与 SHA-256：`artifacts/repair-2026-10-24/baseline-source`、`baseline-source-sha256.json`。未修改该基线源码，基准项目通过 SourceRoot 切换引用。
- 固定数据由 `scripts/RepairBench/Program.cs` 生成；文件一旦存在不重新生成。Excel 1000×30、10000×40、30000×30、1000×30插列、5Sheet；Word 100/1000/5000段与20→5重写；Lua 1000/10000/50000行与单行约50000字符两处小改。每份JSON包含文件大小、哈希、行列规模、差异数、GC及进程峰值工作集。
- 每样本4次：首次调用（含该格式首次JIT/缓存成本）、3次暖运行；报告暖中位数和范围。这里的首次调用不是清空系统文件缓存后的真正冷启动，不能冒充冷磁盘测量。
- 适配器耗时包含双侧解析及比较；展示模型单独计时。托管分配使用进程级 `GC.GetTotalAllocatedBytes`，GC计数也为进程级，峰值工作集是运行到当前的累计峰值而非某一个阶段专属峰值。
- 新增 `Differ-Comparison` EventSource 在 prepare/parse/match/presentation/UI/search/editor阶段输出不含正文的耗时、规模、分配和GC，应用桥接至 `%LOCALAPPDATA%/Differ/Logs/performance.log`。只保留两份有界日志，排队写入上限256。

修前命令：

```powershell
dotnet run --project scripts/RepairBench/RepairBench.csproj -c Release -p:SourceRoot=E:\Differ\artifacts\repair-2026-10-24\baseline-source -- artifacts/repair-2026-10-24/samples artifacts/repair-2026-10-24/before.json
```

修后命令相同，移除 SourceRoot 参数，输出 after.json。数据见同目录 JSON/log，最终结果表见 [PERFORMANCE_RESULTS.md](PERFORMANCE_RESULTS.md)。

## 已知比较边界

- 结构事件改变了统计口径，插列样例不以“节点数必须相同”作为性能正确性依据，而断言1列新增+1真实单元格修改、其他逻辑字段值不变。
- UI性能必须使用Windows实际窗口测量；静态截图、RenderTargetBitmap模拟DPI和算法计时均不能证明60Hz滚动。
- 文本编辑器改变渲染路线，需另测实际WebView2初始化、连续输入及host权威结果就绪，不能拿适配器耗时当作首次可操作画面耗时。
- 首次启动、OS四档DPI、真实IME组合输入、无Runtime干净机仍需按最终验收报告逐项确认，不能从本机有Runtime推断干净机通过。
