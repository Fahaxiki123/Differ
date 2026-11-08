# Differ 离线安装器工程

输入固定为 `artifacts/publish/Differ-win-x64`，输出固定为 `artifacts/installers/Differ-Setup.exe`。构建不会重新发布应用，不会执行安装或卸载。先完成 Release 验证和 `scripts/publish.ps1`，再执行：

```powershell
pwsh -NoProfile -File scripts/build-installer.ps1
```

构建脚本需要 PowerShell 7。默认使用 `artifacts/packaging/2026-09-25/tooling/nsis-3.12/Bin/makensis.exe`，也可以通过 `-CompilerPath` 指向同版本便携编译器。本工程不要求把 NSIS 安装到系统；安装包使用者不需要 PowerShell 7 或 NSIS。

NSIS 3.12 来源：[官方发布目录](https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/)，[便携 ZIP](https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download)。ZIP SHA-256：`56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F`。2026-09-25 下载并与发布页公布值核对，编译器报告 `v3.12`。

安装器采用 Unicode、MUI2 和 LZMA，未对 NSIS 做修改。许可原文保存为 `NSIS-COPYING.txt`，并随软件放入 `licenses`。NSIS 主体采用 zlib/libpng 许可，LZMA 模块采用带链接例外的 CPL；参见[官方许可说明](https://nsis.sourceforge.io/Docs/AppendixI.html)。本项目没有数字签名步骤，不把未签名安装包描述为已签名。

安装器仅写当前用户：默认 `%LocalAppData%\Programs\Differ`，固定卸载键 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Differ.Desktop.x64`。首次可选择独立可写目录；后续升级读取并保持 `InstallLocation`。开始菜单快捷方式始终创建，桌面快捷方式可选。安装前检查目标运行文件是否被占用，不强行结束进程。支持 64 位 Windows 10 1607 / 11 及更新版本；系统支持生命周期以微软实际政策为准。

SVN 绑定默认不勾选。只有明确选择后才运行已安装应用的 `--install-integration`。退出码为 0 时确认绑定成功，为 2 时提示尚未检测到 TortoiseSVN，为其他值时明确显示失败。安装器不直接写 TortoiseSVN 注册表，不复制开发机的偏好或备份。

卸载先运行 `--restore-integration`，只有退出 0 才继续删除。恢复错误会中止卸载并保留程序。应用服务负责验证本安装目录对绑定的所有权。卸载仅删除编译期清单中的文件及空目录，不递归删除自定义安装目录，也不删除 `%LocalAppData%\Differ` 中的偏好与备份。旧副本的卸载程序不会删除其他安装路径持有的卸载注册项和快捷方式。

静默安装使用 `/S`，默认不绑定 SVN、不创建桌面快捷方式；可显式传 `/INTEGRATE=1`、`/DESKTOP=1`。首次自定义目录通过 NSIS 标准 `/D=目录`（放在末尾，不加引号）传递，升级仍使用既有路径。退出码 0 表示请求完成；1 表示安装、卸载或绑定失败；2 表示应用已安装但显式请求的绑定因未找到 TortoiseSVN 而未完成。静默运行不会显示消息框或自动启动应用。

构建会核对当前应用版本、自包含运行时与第三方说明，剔除 PDB，拒绝未知发布文件；生成 `Differ.install-files.json` 供分发核查。临时文件及 NSIS include 均在任务 tooling/build 下，编译成功后才替换最终安装包。清理只面向已验证的暂存路径。

2026-10-24: 0.16.0 文本编辑器使用本地 Monaco 0.52.2 + WebView2 SDK 1.0.3537.50。安装器额外内含官方 x64 Evergreen Standalone 安装程序，官方获取链接 https://go.microsoft.com/fwlink/?LinkId=2124701 ，不使用2124703的在线Bootstrapper。经审核文件存放 artifacts/packaging/2026-10-24/webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe，长度214823632，SHA256 9B0142E22E8DAA92855D746AE4D1E9EEEF561CCAF67D1DD452165C4AF3DF3191，签名Microsoft Corporation/Valid。更新该组件时必须重新校验并更新脚本锁定哈希。检查官方HKCU/HKLM pv，无Runtime时运行/silent /install，不在线下载。共享Runtime不随Differ卸载删除。干净机断网验证不能由已有Runtime机器冒烟替代。
