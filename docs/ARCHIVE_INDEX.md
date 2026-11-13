# 历史材料归档索引

2026-11-12：唯一开发仓库为 `D:\Diff`，唯一开发预览为 `D:\项目归档\Differ\开发预览`，从仓库根目录 `打开开发版.cmd` 启动。现状和未完成项见 [项目状态](PROJECT_STATUS.md)。

2026-11-13，0.21.18 Preview 安装包发布证据：`D:\项目归档\Differ\2026-11-13-release-0.21.18`。`assets` 保留带版本号的安装 EXE、SHA-256、发布清单和说明；`verification` 为本轮 711 通过、0 失败、2 项未启用的常规构建/回归/格式记录。`package-verification-final.json` 保留 598 个归档条目与 592 个运行文件的非执行核验，首次解析生成卸载器空大小的失败留在 `package-verification.json`，未放宽运行文件校验。`runtime-status-probe.json` 是只读启动探针；`github-release-publish.log` 确认四件附件验证后已公开发布 Preview、不替换稳定 Latest。`final-protection.json` 确认现用安装全部文件、绑定、卸载项和开发预览未变；`cleanup-final-apply.json`、`cleanup-final-check.json` 记录发布后清理通过。详见 [安装包发布记录](RELEASE-0.21.18.md)及 [GitHub Release](https://github.com/Fahaxiki123/Differ/releases/tag/v0.21.18)。

2026-11-13，0.21.18 接续修复证据：`D:\项目归档\Differ\2026-11-13-caption-finish`。保留同内容前后自然播放帧、真实窗口按钮与关闭保护结果、合成输入及哈希、工程验证、发布事务与清理清单，详见 [0.21.18交付记录](UI-0.21.18.md)。最终源码验证为 `verification-accepted`，前台截图为 `accepted-*`，安全更新状态为 `preview-update-accepted\preview-state.json`（Finalized）；固定预览为0.21.18，旧next/previous/retired运行副本均已清除。发布后清理见 `cleanup-final-apply.json`、`cleanup-final-check.json`；两处测试缓存现已由用户手动删除，代理复核见 `manual-cache-cleanup-confirmation.json`，再次工作区检查见 `manual-cache-workspace-check.json`；此前 `cleanup-remaining.json` 和重试拒绝记录保留为历史事实。目录中的 `after-*`、`final-*`、`direct-*` 是中间实验，文件名含 final 不代表最终验收；证据归档不是第二个开发入口。

2026-11-12，精确查找与另存目标提示：`D:\项目归档\Differ\2026-11-12-find-save`，实现与前台过程见 [0.21.17](UI-0.21.17.md)。同日标题栏问题调查与接手前审查：`D:\项目归档\Differ\2026-11-12-caption-unified`，原始失败记录见 [0.21.18审查](UI-0.21.18-review.md)，本轮没有改写历史证据。

本轮历史资料根目录为 `D:\项目归档\Differ\2026-11-12-consolidation`：原 `D:\Differ-*` 的设计、源码快照、截图、样例和测试记录对应 `history\原文件夹名`；原 `D:\Differ\UIReview` 对应 `history\Differ-installed-UIReview`。旧运行副本和可重建缓存已清理，历史启动器不再是有效试用入口。保留文件的迁移 SHA-256 清单见根目录 `preserved-files-sha256.csv`。

历史 0.21.0 安装包及校验值位于 `history\Differ-Releases\0.21.0\assets`，离线打包输入位于同级 `packaging-inputs`。保留它们用于回退与重建，没有删除或替换远端历史版本。下方 E 盘入口是更早记录，本轮没有核实或迁移那些历史位置。

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
