# 项目目录维护

用户要求：每次任务结束，`E:\Differ` 只留下正式工程、必要输入和当前交付，不遗留无意义的重复产物。

## 固定保留

- `src`、`tests`、`samples`、`scripts`、`docs`、`installer`、解决方案/包版本/开发规范等原始文件。Gallery 项目的 `bin/obj` 按同一项目白名单清理。
- `测试用例` 原件与 `.svn` 工作副本管理数据。
- `artifacts/publish/Differ-win-x64`：唯一正式软件入口。
- `artifacts/installers`：最新安装器、校验值、说明。
- `artifacts/packaging`：锁定的 NSIS 编译器、工具来源清单、微软签名离线 WebView2 Runtime。新增依赖需同步清理脚本的分类白名单。

## 任务结束顺序

默认入口：`pwsh -NoProfile -File scripts/verify-and-clean.ps1`。统一运行 Release build/test、格式检查，日志与 TRX 写项目外；即使验证失败，仍在 finally 中清理白名单输出并检查。任何步骤失败都会返回失败，须修复后重新执行。可用 `-LogDirectory` 指定项目外日志目录。

1. 完成必要构建、测试、格式检查和本次交付验证。
2. 测试证据、源码快照和有价值基线放项目外，记录入口；大临时运行副本和可重建缓存不归档为永久材料。
3. 执行 `pwsh -NoProfile -File scripts/clean-workspace.ps1 -Apply`。
4. 执行 `pwsh -NoProfile -File scripts/clean-workspace.ps1 -Check`，退出码必须为 0。

默认不传参数时只检查；`-Apply` 只删除有明确边界的编译输出、Python 字节码缓存与已知打包暂存；未知目录只报告，不自动推断为垃圾。`-Check` 有残留返回 1，安全拒绝或执行异常返回 2。不要强杀占用中的用户进程。

清理日志默认保存在 `%LocalAppData%\Differ\WorkspaceMaintenance`，可用 `-ReportPath <项目外JSON路径>` 指定。截图使用 `DIFFER_VISUAL_EVIDENCE` 或项目外默认路径；Python 样例与性能工具显式接收输入/输出参数。不要将新日志、TRX、截图、探针下载目录写回项目历史树。

安装器构建会在成功/失败后清除 payload，发布替换失败会恢复原正式目录；两者日志写项目外。若回滚失败留下 `Differ-win-x64-previous`，应先恢复正式版，不能将备份自动删掉。

清理不是 SVN 提交：不自动 add/delete/revert/commit，不手工删 `.svn/pristine`，也不按哈希批量删除正式目录中的 DLL。需要归档的历史目录见 [索引](ARCHIVE_INDEX.md)。
