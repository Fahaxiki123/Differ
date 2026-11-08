# Differ 离线安装与公司电脑使用

## 带到另一台电脑

将 `Differ-Setup.exe` 带到目标电脑即可安装，不需要复制开发目录，也不要复制原电脑的 Preferences、Sessions、Logs 或 SVN 配置备份。安装包内含 .NET 10、Monaco 本地资源及 Microsoft WebView2 x64 离线安装程序，无需额外安装 .NET SDK、Word 或 Excel；运行与比较本地文件不需要联网。

目标平台为 64 位 Windows 10 / Windows 11（Intel / AMD x64）。安装器会拒绝 32 位 Windows、Windows 7/8 及早于 Windows 10 1607 的系统。优先使用已经安装系统更新的版本；“可以运行”不代表老旧 Windows 版本仍在微软支持期内。具体公司系统与安全策略需以目标电脑实际情况为准。

## 安装与 SVN 绑定

1. 使用今后操作 TortoiseSVN 的那个 Windows 账户运行安装包。默认只安装到当前用户，不需要为了 Differ 请求管理员权限。
2. 默认安装到 `%LocalAppData%\Programs\Differ`，也可首次选择其他可写目录。升级时保持原目录，以免已配置的外部差异路径失效。
3. 如果希望在 TortoiseSVN 提交窗口双击文件时使用 Differ，勾选安装器中的“设为 TortoiseSVN 差异查看器”。此项默认不勾选。
4. 未勾选也能安装。以后从开始菜单打开 Differ，点击“安装到 TortoiseSVN”即可绑定。
5. 如果提示未发现 TortoiseSVN，先由你或公司 IT 安装 TortoiseSVN，再打开 Differ 绑定。Differ 安装包不附带 TortoiseSVN，也不需要其可选的 svn.exe 命令行组件。
6. 在 `TortoiseSVN → 设置 → 差异查看器` 确认路径指向目标电脑实际安装的 `Differ.App.exe`。从提交窗口选一份测试文件查看差异即可，不需要提交。

绑定是“当前用户”的设置，换另一个 Windows 账户需要在那个账户下单独绑定。公司账户、用户名、磁盘盘符、中文或空格路径不同都不会沿用开发电脑的路径；安装后的程序会生成自己的完整调用命令。

## 文本审阅、编辑与保存（0.18.0）

Excel、Word、CSV和二进制继续只读审阅。代码和普通文本新标签默认两侧只读；打开右侧标题的编辑开关后才可修改右侧工作副本，左侧BASE始终只读。关闭编辑模式会保留当前文字、未保存状态和撤销历史；已有修改仍可保存，再次开启可继续编辑。

“全部 / 差异 / 上下文”是常驻选中按钮。保存、撤销与重做集中在右侧标题，另存为在保存下拉中；当前块中缝的向右箭头用于采用左侧块，只修改右侧内存，可以撤销。Ctrl+S独立保存，未保存时关闭/刷新会提示。保存前检查目标是否被外部修改，发生冲突保留内存结果。编码、忽略规则和工作表规则可在“更多”中的高级规则入口调整。

SVN来源只有在新注册命令含 `--source tortoisesvn`、当前修订是 `WC`、公开Subversion API确认是版本化工作文件、当前文件与加载快照哈希相同时才自动确定保存目标。旧注册命令仍能比较，但默认另存为；可在集成页主动重新绑定以更新协议。历史版本、未知来源和Sessions快照默认另存为，不能回写BASE或快照。也可由用户明确指定普通本地保存目标。

文本默认保持编码、BOM及换行；混合换行保存前要求明确选择统一CRLF/LF，不可编码的字符会报错而不替换为问号。没有编辑的保存不会重写原文件。

WebView2不在系统中时由安装器离线补齐；不依赖首次启动下载。复制便携目录到缺Runtime的电脑时请先运行完整安装器。共享WebView2 Runtime不会随Differ卸载删除。

## 原有工具与高级配置

TortoiseSVN 的“差异查看器 → 高级”按扩展名设置会覆盖全局设置。Differ 在明确绑定时先备份全局差异工具与所有受支持扩展名的高级绑定，再清除这些高级覆盖，让全局配置真正生效；不支持的扩展名和合并工具不受影响。

备份保存在当前用户的 `%LocalAppData%\Differ\TortoiseDiffToolsBackup.json`。不要从原电脑复制该文件到公司电脑；否则会混入另一台机器的工具路径。

需要撤销绑定时，在 Differ 集成页点击“恢复原差异工具”。程序会尊重你安装后另外配置的工具，不直接覆盖后续人工设置。

## 升级和卸载

- 升级前关闭 Differ 的比较窗口。安装器会检查占用，不会无提示强制关闭你的比较会话。
- 同目录升级保留本机设置和原有配置备份。
- 正常卸载会先尝试恢复本安装位置持有的 SVN 绑定，再删除安装文件；恢复失败会阻止删除，避免留下指向已删除程序的路径。
- 旧目录的卸载不会撤销另一个新目录的 Differ 绑定。若你在安装后改用了其他工具，卸载也不会覆盖它。
- 个人偏好、仍有价值的配置备份默认保留；放在安装目录中的其他个人文件不会递归删除。
- 不要在卸载前手工删除 `Differ.App.exe`。若程序或备份损坏，先修复/重装到同一目录，再恢复原工具并卸载。

## 公司电脑上需要留意

- 必须有正常安装、可使用的 TortoiseSVN；程序也会从安装注册信息、Windows App Paths、PATH 和常见目录探测非默认安装位置。
- 安装目录必须可写，绑定需要当前用户的 TortoiseSVN 配置项可写。公司策略如果锁定了该配置，应交由 IT 处理。
- 目前安装包和程序未使用商业代码签名。若公司应用白名单或安全策略阻止未签名软件，请让 IT 审核安装包和 SHA-256 校验值，不要关闭或绕过安全策略。
- 绑定只影响外部差异查看器，不更改 SVN 账户、仓库地址、工作副本或提交选择；不会执行 update、revert、add、delete 或 commit。

## 构建与测试说明

开发机先运行 `scripts/publish.ps1`，再运行 `scripts/build-installer.ps1`。正式软件仍位于固定的 `artifacts/publish/Differ-win-x64`，安装包单独输出到 `artifacts/installers/Differ-Setup.exe`。

安装/卸载服务的自动测试使用隔离配置。开发机上的安装生命周期测试不等同于所有 Windows 10/11 版本的干净机测试；交付记录会列出实际检查项目和未覆盖环境。

参考：[TortoiseSVN 外部工具设置](https://tortoisesvn.net/docs/release/TortoiseSVN_en/tsvn-dug-settings.html)、[.NET 10 平台支持表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)、[Windows 应用信誉与签名](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)。


0.16.0 还包含微软签名的应用本地 Visual C++ x64 运行组件，用于本地工作文件身份校验；无需安装 Visual Studio 或单独请求管理员安装 VC++ 运行库。此私有组件由后续 Differ 版本维护更新。WebView2与VC组件的实际干净机验证情况见本轮交付报告。
