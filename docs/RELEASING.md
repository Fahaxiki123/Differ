# GitHub 版本下载与发布

GitHub Releases 保存每个可下载版本，本机 `artifacts/publish/Differ-win-x64` 仍只保留一份当前程序。仓库维持私有，只有已有仓库权限的人可以访问安装包；发布不改变可见性，也不邀请其他人。

稳定基线是远端提交 `2cccc08a07838509e5980ccd1750ee8e9d446941` 的 **0.18.2**。若使用该提交重新构建的安装包，说明中必须写明“按稳定源码重建”，不能声称是历史原始二进制。新三方合并及交互 **0.20.0** 未经用户验收，属于 **Preview**，不得设为 Latest。后续测试版同样默认 Preview；只有用户明确认可版本并要求推广，才发布独立稳定版本，不在原测试版上替换附件。

## 版本包

项目外推荐目录：`E:\Differ-Releases\<version>`。发布脚本只上传以下四个明确名称的文件，其余本地材料不会上传：

```text
Differ-<version>-win-x64-Setup.exe
Differ-<version>-win-x64-Setup.exe.sha256
release-manifest.json
RELEASE-NOTES.txt
```

安装器必须从对应源码提交构建；文件自身的 PE 版本为 `<version>.0`。SHA-256 文件只有一行，使用真实安装包哈希和确切文件名，例如：

```text
<64位SHA-256>  Differ-0.20.0-win-x64-Setup.exe
```

`release-manifest.json` 使用 UTF-8，必需结构如下；`sha256` 为大写，`sourceCommit` 为小写完整 40 位提交号。`validation` 可添加真实检查记录和未验证项，不放私人文件路径、凭据或业务原文。

```json
{
  "repository": "Fahaxiki123/Differ",
  "tag": "v0.20.0",
  "version": "0.20.0",
  "channel": "Preview",
  "sourceCommit": "<实际构建提交的40位SHA>",
  "installer": {
    "fileName": "Differ-0.20.0-win-x64-Setup.exe",
    "sha256": "<真实安装包的64位大写SHA-256>",
    "fileVersion": "0.20.0.0"
  },
  "rebuilt": false,
  "createdUtc": "2026-11-08T00:00:00Z",
  "validation": {}
}
```

`RELEASE-NOTES.txt` 是 UTF-8 Markdown，同时作为附件及 Release 正文。应写明版本用途、源码提交、主要变化、验证范围、未验证条件、安装要求、未签名状态和已知限制。历史稳定版本重新构建时，`rebuilt` 为 `true` 并记录实际构建 SDK/运行时；重新构建不等于重做了所有历史 UI 验收。

## 发布步骤

发布脚本需要 PowerShell 7.4 或更新版本，以及已有的 Git Credential Manager 登录。

1. 完成对应源码的构建、测试、格式检查及必要实机验收，整理四个版本包文件。稳定代码留在稳定主分支，未验收功能使用独立预览分支。
2. 核对源码提交并创建、推送 `v<version>` 标签。发布脚本只接受已存在且指向 `ExpectedCommit` 的远端标签，不创建、移动或删除标签。
3. 运行只读预检。预检会检查当前 checkout 的 origin/push 目标、本地该提交中的应用版本、安装器 PE 版本、清单与 SHA-256，以及 GitHub 账号、写权限、私有可见性、标签和现有 Release。

```powershell
$commit = '<实际构建提交的40位SHA>'
pwsh -NoProfile -File scripts/publish-github-release.ps1 `
  -Repository Fahaxiki123/Differ -Tag v0.20.0 -Version 0.20.0 `
  -ExpectedCommit $commit -Channel Preview `
  -AssetDirectory E:\Differ-Releases\0.20.0 -DryRun
```

4. 不带 `-DryRun`、不带 `-Publish` 时，创建草稿并上传、验证四个附件，保留草稿供检查。加上 `-Publish` 才在全部验证成功后发布；可直接用同一命令的 `-Publish` 从头完成“草稿→上传→验证→发布”。Preview 设置 `prerelease=true`、`make_latest=false`；Stable 发布时设置 Latest。

```powershell
pwsh -NoProfile -File scripts/publish-github-release.ps1 `
  -Repository Fahaxiki123/Differ -Tag v0.20.0 -Version 0.20.0 `
  -ExpectedCommit $commit -Channel Preview `
  -AssetDirectory E:\Differ-Releases\0.20.0 -Publish
```

`-NotesFile` 可省略；若提供，必须指向版本包内同一份 `RELEASE-NOTES.txt`。`-ExpectedPrivate` 默认 `true`，实际可见性不同会停止，脚本从不修改可见性。`-Publish` 和 `-DryRun` 不能同时使用。

## 认证、重试与历史保留

- 使用本机已有 Git Credential Manager 登录，预期账号 `Fahaxiki123`；无需安装 `gh`。令牌由非交互 `git credential fill` 读取，仅保存在进程内存和请求头中，不写文件、命令行、日志或输出。不要在发布命令中手工加入令牌。
- 每个附件以流上传，单次最长 1800 秒。上传后验证名称、完整状态、大小和服务端 SHA-256；若 API 未提供摘要，则以流下载重新计算。下载跳转仅接受 GitHub 官方资产域名，签名地址不打印，认证头不会转发给下载域名。
- 中断后保留草稿及已上传附件。先重新运行 `-DryRun`，匹配的草稿可继续补齐缺失附件；已存在附件必须逐字节一致，不删除失败占位附件、不覆盖不同内容。出现不完整或冲突附件时停止并由人工检查。
- 完全相同且完整的已发布版本只返回验证结果，不重传附件、不修改正文、不改变旧版本的 Latest 状态。已发布版本有任何差异或缺失则拒绝修改，应使用新的版本号。
- 保留所有历史标签、Release 和附件。不要删除旧稳定版本，也不要拿本机最新安装包覆盖旧版本下载。

安装器采用同一应用身份原位替换，不承诺稳定版和测试版同时安装。降级到 0.18.2 后三方合并入口不可用；升级或降级不自动更改已有 SVN 合并工具绑定。下载者应先关闭 Differ，再运行所选版本安装器。

接口依据：[GitHub Releases REST API](https://docs.github.com/en/rest/releases/releases)、[Release Assets REST API](https://docs.github.com/en/rest/releases/assets)。项目发布边界以 `DEVELOPMENT_SPEC.md` 第 20.2.1 节为准。
