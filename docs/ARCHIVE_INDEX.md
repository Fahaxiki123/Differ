# 历史材料归档索引

2026-11-08 合并操作集中与连续处理：`E:\Differ-Review\2026-11-08-merge-interaction`，用户标注图、同尺寸前后运行图、布局与流程回归、发布验证见 [UI-0.20.0.md](UI-0.20.0.md)。

2026-11-08 合并基础窗格开关：`E:\Differ-Review\2026-11-08-merge-base-toggle`，同内容前后运行画面、状态记忆与并发偏好回归、宽窄窗/浅深/缩放验证，见 [UI-0.19.1.md](UI-0.19.1.md)。

2026-11-08 多处增删改真实冲突：`E:\SVN-Conflict-Test\Many-conflicts` 保留14处未解决冲突供用户取舍；验证及运行画面位于 `E:\Differ-Review\2026-11-08-svn-many-conflicts`。原有单冲突样例保留。

2026-11-08 真实本地SVN冲突端到端：证据 `E:\Differ-Review\2026-11-08-svn-e2e`；可直接试用的仓库/工作副本与快捷方式 `E:\SVN-Conflict-Test`。用户的A-me仍保留未解决冲突，Ui-verification已验证为Modified且无冲突，说明见 [UI-0.19.0.md](UI-0.19.0.md)。

2026-11-08 三方文本合并样板：`E:\Differ-Review\2026-11-08-merge-sample`。保留合成输入、同尺寸双栏前后桌面截图、三方样板桌面截图、长内容/窄窗/缩放运行证据、测试报告与发布记录。说明见 [UI-0.19.0.md](UI-0.19.0.md)。

2026-11-08 文本界面样板证据：`E:\Differ-Review\2026-11-08`。包括参考图、同输入前后截图、输入哈希、修改前源码、构建与测试记录；说明见 [UI-0.18.0.md](UI-0.18.0.md)。临时运行副本验证后删除，不作为第二套正式程序。

2026-10-24 用户授权整理后，历史材料统一迁至：

`E:\Differ-归档\2026-10-24-cleanup`

原 `E:\Differ\artifacts\<目录>\<文件>` 对应归档的 `artifacts\<目录>\<文件>`；完整目录结构与有价值的源文件、原始测量、截图、测试结果、故障备份均保留。迁移逐文件 SHA-256 核验通过。可重建临时运行副本、依赖探针下载/解包和编译缓存已清除，不作为历史证据保留。

| 原项目目录 | 用途 | 当前入口 |
|---|---|---|
| artifacts/repair-2026-10-24 | Excel/Word/编辑器修复、固定样例及性能基线 | [归档](E:/Differ-归档/2026-10-24-cleanup/artifacts/repair-2026-10-24) |
| artifacts/hotfix-2026-10-24 | Word 重叠、代码导航修复 | [归档](E:/Differ-归档/2026-10-24-cleanup/artifacts/hotfix-2026-10-24) |
| artifacts/ui-implementation-2026-10-24 | 0.17.0 实施、测试与性能对照 | [归档](E:/Differ-归档/2026-10-24-cleanup/artifacts/ui-implementation-2026-10-24) |
| artifacts/visual-validation | 各版本真实控件效果图 | [归档](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation) |
| artifacts/design、performance、audit-lab、audits、validation、backup、integration | 历史设计、性能与审计 | [归档根目录](E:/Differ-归档/2026-10-24-cleanup/artifacts) |
| artifacts/startup-repair-2026-10-24 | 依赖 JSON 损坏备份与恢复记录 | [恢复记录](E:/Differ-归档/2026-10-24-cleanup/artifacts/startup-repair-2026-10-24/README.md) |
| artifacts/packaging/2026-09-25 中的旧 before/测试/截图 | 历史安装验证 | [归档](E:/Differ-归档/2026-10-24-cleanup/artifacts/packaging/2026-09-25) |

正式软件仍位于 `artifacts/publish/Differ-win-x64`，最新安装器仍位于 `artifacts/installers`；打包使用的 NSIS 和微软离线 WebView2 安装源继续留在 `artifacts/packaging`，不能当作垃圾删除。

当前及后续任务的目录约束见 [维护规则](WORKSPACE_HYGIENE.md)。归档内的历史文档/原始命令保留当时上下文；执行时需按本表转换旧路径。归档根目录含对应 docs/README 快照，支持历史相对链接。
