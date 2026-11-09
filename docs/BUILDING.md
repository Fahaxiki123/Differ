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

## 源码边界

Git 保留源码、构建脚本、测试、规范、第三方许可、Monaco 离线 Editor 资源和经过校验的 Native/win-x64/vcruntime140.dll。这些是必要构建输入，不是可删除的重复产物。

Git 不包含 .svn、.codex、bin/obj、发布目录、安装器、安装工具缓存、日志、会话、个人偏好、SVN 配置备份及原始 `测试用例`。它们仍可按本地规范保留，排除上传不代表删除。

docs 中历史验收的 E:/D: 路径是开发机器的证据索引，不是构建依赖；新的验证输出默认写入仓库外。

## 发布与离线安装器

`pwsh -NoProfile -File scripts/publish.ps1` 生成唯一自包含目录 `artifacts/publish/Differ-win-x64`，不自动修改 SVN 配置。

界面第一阶段试用使用显式隔离模式。指定一个尚不存在的仓库外目录，例如 `pwsh -NoProfile -File scripts/publish.ps1 -PreviewDirectory D:\Differ-UI-Trial`。该模式产生 `Differ.Phase1.Preview.exe`，使用独立应用数据和比较进程管道，禁用写入 TortoiseSVN 绑定。Gallery 可用 `dotnet run --project samples/Differ.UI.Gallery/Differ.UI.Gallery.csproj -c Release` 启动。验收和试用入口见 [界面体系第一阶段](ui/PHASE1.md)。

离线安装器不是普通 build/test 的前置条件。生成安装器还需准备被 `.gitignore` 排除的大型打包输入：NSIS 3.12 编译器及 Microsoft WebView2 x64 Evergreen Standalone 安装程序。具体路径、官方来源和锁定校验要求见 [installer/README.md](../installer/README.md) 与 build-installer.ps1；缺少输入时应明确失败，不能用在线 Bootstrapper 替代离线运行库。

本仓库没有为用户自有代码自动新增开源许可证；第三方组件继续遵守各自许可。改变仓库可见性或对外授权前应单独确认。
