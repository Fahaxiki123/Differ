# 0.21.18 未完成工作核查

> 本文保留 2026-11-12 接手前的审查结果，包含当时的失败和未验证项。2026-11-13 接续修复、前台验证与固定开发预览状态见 [0.21.18交付记录](UI-0.21.18.md)；下文不作为当前完成状态。

2026-11-12，用户要求检查 Gemini 未完成的标题栏修复。本轮只审查源码与既有证据，没有修改生产代码、运行新的界面测试或替换程序。

## 当前状态

核心改造已写入源码0.21.18，规范已到4.8.5、第31.8节。固定开发预览仍为0.21.17，正在打开的窗口也来自该固定目录；0.21.18尚未发布到这个入口。

| 项目 | 已核实结果 |
|---|---|
| 标题栏实现 | 全宽WPF底板、3个自绘窗口按钮、非客户区命中及悬停/按下状态、最大化/还原字形和UIA名称已接线 |
| 主题实现 | 取消过渡期间逐帧DWMWA_CAPTION_COLOR；原生深浅属性在初始化及过渡结束后同步 |
| Release构建 | 0错误、424警告；仅验证进程设置WarningsNotAsErrors=NU1902;NU1903，既有依赖审计仍保留 |
| 标题栏/主题专项 | 39/39通过，其中6项来自CaptionUnifiedBarTests |
| 完整App回归 | 328项：294通过、34失败，0跳过；已结束。不能将专项通过写成全量通过 |
| 格式 | 没有找到完成证据；新增CaptionUnifiedBarTests.cs使用LF，与仓库CRLF规则不符 |
| 前台验收 | 未完成；现有部分截图只显示“正在初始化离线文本编辑器...” |
| 发布 | 只有替换脚本，无本轮发布日志或preview-replace.json；固定入口未替换 |

证据目录：`D:\项目归档\Differ\2026-11-12-caption-unified`。`codex-audit-summary.json` 保留本次核对时的版本、计数和0.21.18构建程序集哈希，原始日志/TRX不改写。

## 需要处理的问题

1. **完整App回归失败。** 大量用例因WebView2初始化/就绪超时失败，与Gemini的verification-context.json中所述沙箱限制一致；这不是全部失败的唯一解释。`EditorOverviewContinuityTests.SameVersionOptionsChangeCancelsQueuedInlineBeforePublishingNewHighlightsAsync`还出现独立ViewModel断言：期望CurrentHunk=-1，实际0，应在可用环境单独复核。
2. **取证测试会静默缺正文。** `CaptionUnifiedBarTests.cs:441`只等待ViewModel完成，未等待CodeEditorView.IsEditorReady；CoreWebView2为空时CaptureComposite直接返回WPF位图。已检查after-720x620-light及zoom150-light，两者均停在初始化提示。因此这些图只能说明WPF外壳，不能证明真实正文、150%阅读缩放和完整换色链路通过。
3. **自绘按钮的拖出/回入状态需要修复或验证。** 按下时吞掉非客户区消息并自行记录按下状态，但没有捕获鼠标；进入客户区或离开非客户区立即清零。持续按住、拖出、移回同一按钮再释放时，自己的命令执行条件可能不再成立。这是静态路径发现，尚未做本次前台复现。
4. **真实窗口行为仍未验收。** 新测试主要使用SendMessage和UIA，工作区为空，不能证明真实贴靠菜单、系统菜单及有未保存文本时的关闭流程。测试还会实际最大化原本移到屏幕外的窗口，不能将其视为始终不干扰桌面的测试。
5. **替换脚本缺少保护。** publish-and-swap-preview.ps1删除next前没有完整祖先/重解析点校验，current→previous和next→current之间没有失败回滚。当前运行中的0.21.17会先使脚本拒绝替换；不应直接绕过该检查。

## 接续顺序

先补编辑器就绪门禁、独立复核ViewModel失败与按钮手势，再在可正常启动WebView2的环境跑回归和格式检查。完成真实前台双向换色、窗口按钮/贴靠/关闭保护、窄窗和缩放验收后，使用有回滚的流程替换固定开发预览，清除旧运行副本并检查工程目录。继续只维护D:\Diff这一套源码，不修改现用SVN安装或绑定。

本次等待外部App测试自行结束后，仅清理其可重建bin/obj；源代码、原始用例、39项专项和34项失败的证据、当前0.21.17预览与回退副本保留。清理结果见同目录的codex-audit-clean-apply.json、codex-audit-clean-check.json。
