# 从干净源码构建

## 环境

- Windows 10/11 x64，.NET 10 SDK。
- 完整验证/发布/清理脚本需要 PowerShell 7。
- 文本编辑器运行和实际浏览器回归测试需要 Microsoft Edge WebView2 Runtime。
- 首次 NuGet 还原需要访问 NuGet；版本集中锁定在 Directory.Packages.props。

在仓库根目录执行：

```powershell
dotnet restore Differ.sln
dotnet build Differ.sln -c Release
dotnet test Differ.sln -c Release --no-build
dotnet format Differ.sln --verify-no-changes --no-restore
```

推荐完整验证和自动清理入口：

```powershell
pwsh -NoProfile -File scripts/verify-and-clean.ps1
```

自动测试自行生成 DOCX/XLSX 等合成样本，或使用 tests 内明确保留的最小测试数据，不依赖开发电脑的 `测试用例` 业务原件。原始业务文件的人工验收属于单独范围。

### 本轮验证条件与前台取证

2026-11-13 的 0.21.18 验证保留 NuGet 审计。既有传递依赖（ImageSharp、System.Security.Cryptography.Xml/Pkcs）触发 NU1902/NU1903，在默认 `TreatWarningsAsErrors` 下可能阻断冷构建。本轮仅在验证进程设置 `WarningsNotAsErrors=NU1902;NU1903`，没有关闭审计、更改工程配置或升级依赖；结果不能称为默认无告警构建。最终日志与测试计数见 [0.21.18交付记录](UI-0.21.18.md)。

常规自动回归不实际最大化离屏窗口。需要窗口最小化/最大化回归时显式设置 `DIFFER_CAPTION_FOREGROUND_TESTS=1`，它会操作测试窗口，应在可用桌面环境运行。需要生成标题栏证据时，同时设置项目外的 `DIFFER_CAPTION_EVIDENCE_DIR` 和阶段名 `DIFFER_CAPTION_EVIDENCE_STAGE`；未显式启用的对应测试会报告跳过。包含正文的取证必须等待真实 WebView2、已加载文档版本、字体和行高就绪，不能用初始化占位图代替正文验收。

取证测试生成的受控进度截图仅验证空间绘制；真实前台自然播放、Snap Layouts、系统菜单与保存/关闭操作另行记录。阅读缩放不代替 Windows 系统 DPI 测试。

## 源码边界

Git 保留源码、构建脚本、测试、规范、第三方许可、Monaco 离线 Editor 资源和经过校验的 Native/win-x64/vcruntime140.dll。这些是必要构建输入，不是可删除的重复产物。

Git 不包含 .svn、.codex、bin/obj、发布目录、安装器、安装工具缓存、日志、会话、个人偏好、SVN 配置备份及原始 `测试用例`。它们仍可按本地规范保留，排除上传不代表删除。

docs 中历史验收的 E:/D: 路径是开发机器的证据索引，不是构建依赖；新的验证输出默认写入仓库外。

## 发布与离线安装器

`pwsh -NoProfile -File scripts/publish.ps1` 生成唯一自包含目录 `artifacts/publish/Differ-win-x64`，不自动修改 SVN 配置。

界面试用使用显式隔离模式。本机唯一开发预览为 `D:\项目归档\Differ\开发预览`，从仓库根目录 `打开开发版.cmd` 启动。首次发布可向该尚不存在的目录传入 `scripts/publish.ps1 -PreviewDirectory`；更新时先在同级受控临时目录发布，验证成功后再替换并清理旧副本，不在磁盘根目录新增日期版本。该模式产生 `Differ.Phase1.Preview.exe`，使用独立应用数据和比较进程管道，禁用写入 TortoiseSVN 绑定。Gallery 仅按需构建，任务结束清理。当前状态见 [项目状态](PROJECT_STATUS.md)，历史验收见 [界面体系第一阶段](ui/PHASE1.md)。

已有开发预览使用 `scripts/update-dev-preview.ps1`，同一次更新的各阶段必须复用一个新的项目外 `-EvidenceDirectory`：

1. `-Stage Prepare` 发布并核验受控 `.开发预览-next`，保持当前入口可运行。
2. 完成候选测试和前台验收，正常关闭候选及固定预览，再运行 `-Stage Promote`。脚本核对完整产物清单、源码身份、占用、路径和重解析点；替换失败恢复旧版。
3. 核对固定入口实际运行版本后，运行 `-Stage Finalize -AcceptanceReport <绝对JSON路径>`。报告必须含 `Accepted: true`，以及与该次候选状态一致的 `Version`、`AppDllSha256`、`PayloadSha256`。它记录授权任务已完成的验证，不表示额外用户审批。完整载荷摘要包含 Editor，不能只凭版本号或 App.dll 接受不同 JavaScript。
4. `Finalize` 删除已核验的旧运行副本；异常需要恢复时使用 `-Stage Rollback`。未推广的候选使用 `-Stage DiscardCandidate` 清理。任一阶段不强杀用户进程、不改 SVN 设置。

脚本隔离回归入口为 `scripts/test-update-dev-preview.ps1`，仅使用合成目录。发布产生的工程 `bin/obj` 仍须在任务末尾执行 `clean-workspace.ps1 -Apply` 和 `-Check`；发布事务自身完成不代替工作区清理门禁。

离线安装器不是普通 build/test 的前置条件。生成安装器还需准备被 `.gitignore` 排除的大型打包输入：NSIS 3.12 编译器及 Microsoft WebView2 x64 Evergreen Standalone 安装程序。具体路径、官方来源和锁定校验要求见 [installer/README.md](../installer/README.md) 与 build-installer.ps1；缺少输入时应明确失败，不能用在线 Bootstrapper 替代离线运行库。

本仓库没有为用户自有代码自动新增开源许可证；第三方组件继续遵守各自许可。改变仓库可见性或对外授权前应单独确认。
