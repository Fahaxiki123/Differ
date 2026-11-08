# Differ 0.16.1：Word 重叠与代码定位反馈

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](../ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

日期：2026-10-24。修复用户实际反馈的两个问题，保留现有比较结果、编辑保存、安全边界和虚拟化。

## Word 文字重叠

已在项目内找到同名 `F-商业化付费型.docx`，使用只读副本复现。原件与副本 SHA-256 均为 `8D713DA61F7470932EF8DB45591BC46F04B5E4099F206FAE1BC60DF81E02818B`，原件未改。

根因是列表编号/项目符号与正文叠放在同一个 Grid，只靠缩进定位；缺少悬挂缩进或使用正首行缩进时，标记会压到段首文字。旧测试实测标记占据 x=27.47–37.39 DIP，正文却从 x=18 DIP 开始。另有编号层缩进遗漏及 `w:numFmt/@val` 读取错误，使部分项目符号显示为方框。

修复后使用按实际标记宽度测量的独立布局区域，普通段落与变更组共用；继承编号层缩进，保留直接段落覆盖规则；正确识别 bullet/none。长编号、窄窗、极端缩进及字体切换都给正文保留可读空间。

真实 `WordDiffAdapter → DiffWindow → WordDiffView` 在宽/窄窗口中遍历检查，标记与正文无相交，最小间隔5.6 DIP。截图左右是同一只读文件，验证排版；不冒充用户完整 BASE/MINE 差异复现。使用阅读缩放和渲染DPI测试，不声称操作系统所有DPI档位均已手工切换。

- [真实模板宽窗口](E:/Differ-归档/2026-10-24-cleanup/artifacts/hotfix-2026-10-24/word-render/word-real-wide.png)
- [真实模板窄窗口](E:/Differ-归档/2026-10-24-cleanup/artifacts/hotfix-2026-10-24/word-render/word-real-narrow.png)
- [用户原截图](E:/Differ-归档/2026-10-24-cleanup/artifacts/hotfix-2026-10-24/user-word-before.png)

## 代码块切换没有定位反馈

真实 WebView2 验证显示，旧边框有实际宽高，但与差异背景处于相同绘制层，后绘制的底色把细边框遮住了。

现在当前块在两侧都有独立覆盖层边框与行号旁标记，跳转时短暂提示；现有底栏明确显示第几块、变化类型和 BASE/结果的原始行范围。纯新增/删除显示对应空区，并定位到有内容的一侧；同屏切换也能看到框的位置变化。编辑立即清除旧块反馈，快速跳转的最后一次选择获胜，反馈不改变行高、文字或撤销历史。

- [代码定位修复前](E:/Differ-归档/2026-10-24-cleanup/artifacts/repair-2026-10-24/navigation-before/navigation.png)
- [代码定位修复后：浅色](E:/Differ-归档/2026-10-24-cleanup/artifacts/repair-2026-10-24/navigation-after/navigation-Light.png)
- [代码定位修复后：深色](E:/Differ-归档/2026-10-24-cleanup/artifacts/repair-2026-10-24/navigation-after/navigation-Dark.png)

## 验证与交付

- Release 构建通过，末次构建0错误、0警告；格式检查通过。
- 完整自动测试 **536/536通过**（上版517项）。本轮增加Word解析3项、Word布局9项、真实WebView2导航7项。
- 新导航测试检查实际截图中的边框像素，避免仅有DOM框而屏幕不可见；覆盖纯增删、空文件、同屏切换、折叠、搜索/定位、窗口缩放和异步失效。
- 1,000段固定Word样本100次普通滚动：p50 7.88ms、p95 10.77ms、最大27.52ms；此次为目标性能回归，不作为全场景提速声明。
- 标准日志与TRX：`artifacts/hotfix-2026-10-24`。真实文档渲染工具：`scripts/WordRenderProbe`。
- 程序及安装包更新为0.16.1；固定运行目录 `artifacts/publish/Differ-win-x64`，独立离线安装器 `artifacts/installers/Differ-Setup.exe`。
- 安装包277,346,459字节，SHA-256：`1319FAB8CE316879A3B91F15BDD31F434D0E0734D5CA05E35479706299BC43FE`；校验文件已同步。发布DLL和编辑器JS/CSS/HTML均与本次构建/源码哈希核对一致。
- 本轮未改变SVN注册配置、未执行SVN写操作，未改变原始Office文件。桌面自动化窗口归属缓存异常，实际视觉验证使用真实WPF窗口/视图和WebView2捕获，未伪称完成桌面手工点击验收。

上一轮未验证的原生IME、全部系统DPI、真实SVN历史调用和缺Runtime断网干净机条件继续保留在总交付报告；本修复不把它们自动改为通过。
