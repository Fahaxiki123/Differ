# 0.19.0 三方文本合并核心样板

2026-11-08 用户批准按 Beyond Compare 功能差距建议开发。先交付可审阅、可运行的三方文本合并核心样板及本地选文件入口；精细规则/人工对齐、图片/PDF、目录只读比较后续分阶段实施。

## 使用入口

直接启动已安装 Differ，点击“三方文本合并…”，选择共同基础、我的、对方以及独立结果路径。三个输入只读，输出只在主动保存时写入。普通“比较两个文件…”入口也已提供。

三方窗口上部为三个来源，下部为完整结果文档。非冲突修改自动进入结果；橙色冲突暂保留基础文本，可以采用我的/基础/对方，也可编辑结果后确认。全部冲突解决后才能保存。“保存”继续工作，“保存并完成”成功写入后返回退出码0；取消或普通关闭返回1，启动失败返回2。不执行 SVN resolve 或提交。

```text
Differ.App.exe --merge --base "base.txt" --mine "mine.txt" --theirs "theirs.txt" --merged "result.txt"
```

合并进程保留到用户结束，避免普通差异转交后立即退出的机制影响 TortoiseSVN 等待结果。原双栏仍单实例多标签。集成页只提供合并命令模板，本轮保持真实 Diff/Merge 及高级扩展名绑定不变，继续由现有 Beyond Compare 承接真实 SVN 冲突。

## 交互与安全

- 三个来源与结果使用本地 Monaco，WPF/MVVM 宿主负责权威合并状态；输入不执行脚本、宏或远程请求。
- 冲突导航、来源采纳、手工确认、撤销/重做、查找、主题和阅读缩放可见。720 DIP 窄窗切换来源标签，结果/解决/保存保持可用；阅读缩放不放大工具按钮。
- 输出采用打开时指纹检查、同目录临时写入和原子替换；不覆盖外部变更，不写来源及 Sessions。实际 NTFS 硬链接、符号链接及短路径别名保护有测试。
- 保留 MINE 编码/BOM与换行；原样选择 MINE 可逐字节保存。混合换行的改写、不可编码字符、未解决冲突均明确拒绝。
- 编辑、采纳和保存按版本串行同步。手工编辑跨块边界时禁用可能覆盖上下文的块采纳，改用手工确认；撤销同步恢复解决状态。
- 不支持 Office、CSV 表格或二进制原格式合并。大范围差异超预算时保守扩大待审阅区并提示，不猜测结果。

## 验证与证据

证据目录：`E:\Differ-Review\2026-11-08-merge-sample`。

- `fixtures`：合成战斗配置，包含两处双方冲突和各自独立修改。BASE/MINE/THEIRS 原件用于读取，输出独立。
- `before-two-way.png`：0.18.2 已安装程序的原双栏桌面基线，2048×1104。
- `after-two-way.png`：0.19.0 已安装程序，同BASE/MINE、窗口尺寸、全部范围与文档起始位置的双栏回归；旧版截图有残留文字选区，新版无选区，正文及差异范围一致。
- `local-file-picker.png`：已安装程序的新入口与独立结果路径选择。
- `runtime`：真实 WebView2 CapturePreview，500行、中文长行、1500 DIP 宽窗、720 DIP 窄窗、浅深色及150%阅读缩放。文件名前缀 before 表示未解决状态，resolved 表示已解决；不代表旧版软件。此组为实际运行的 WebView 表面捕获，不是桌面截图或真实系统 DPI 切换。
- 自动化覆盖严格行合并、插入/删除/空文件/末尾换行、相同和独立修改、750组随机重建/对称不变量、版本拒绝、保存失败和输出外部变化。
- 真实编辑器回归覆盖来源只读、采纳/撤销/重做、手工输入与立即确认、撤销恢复未解决、未提交输入的保存同步、旧版本编辑拒绝保留草稿及保存完成。

全量 Release build、626项测试（无失败/跳过）、format、清理 Apply/Check 已通过；记录见 `verification-accepted/summary.json` 与 TRX。此前两次全量入口在编译分析阶段发现取消令牌未传递，已修复；保留失败日志供追溯，不计为通过证据。

首轮安装版原生实测：第一处采用我的speed=12/attack=30，第二处采用对方gold=200，独立timeout=60自动保留；主动保存前输出不存在，保存并完成后输出正确且进程返回0。三份来源SHA-256保持不变。记录见 `native-completion.json`、`input-hashes-after.json`。安装器以 `/INTEGRATE=0` 原位升级D盘，退出0，真实Diff/Merge值不变。

该轮原生实测发现多行冲突标记按行圆角，已修为块首/中/尾连续轮廓与圆头竖条；追加四窗格三行冲突浅深色几何断言与运行截图。`merge-before-rail-fix.png` 保留修复前桌面证据；`runtime/merge-multiline-*-wide.png` 为修复后实际运行捕获。修正后全量626项、build/format及清理再次通过，见 `verification-continuous-rails/summary.json`。

最终包重新发布并原位安装到 `D:\Differ`，安装退出0；EXE版本0.19.0.0，安装版与固定发布目录的merge.js一致。`after-merge.png` 是最终安装版2048×1104原生桌面截图，与 `merge-before-rail-fix.png` 采用同输入、同尺寸、同一首个冲突，可直接检查连续轮廓。最终安装版取消未解决会话返回1，原有结果文件哈希不变，见 `native-cancel.json`。真实Diff/Merge绑定再次核对不变，见 `svn-bindings-final.json`。

交付：固定程序 `E:\Differ\artifacts\publish\Differ-win-x64\Differ.App.exe`；本机开始菜单仍指向已升级的 `D:\Differ\Differ.App.exe`；离线安装包 `artifacts/installers/Differ-Setup.exe`，SHA-256 `07205D1546D5A484D108085323181086B54965DFEBB801C3AC6A9A9B0246DA48`。发布与打包日志在 `release-final`。样板等待用户体验反馈，再推进其余推荐能力。

## 未验证范围

未切换 Windows 系统到全部100%/125%/150%/200% DPI；阅读字号缩放不等同于系统 DPI。未做实体中文 IME 组合输入、干净机离线安装及公司远程仓库测试。初次交付时只验证等价参数调用；后续已按用户明确授权完成以下真实本地 SVN 端到端。

## 真实 SVN 冲突端到端（用户随后授权）

测试根为 `E:\SVN-Conflict-Test`，本地仓库 `file:///E:/SVN-Conflict-Test/repo`。r1共同基础value=1；本地工作副本改为2，同事副本改为3并提交r2，双方另有独立修改。建库、提交使用已安装SharpSvn的隔离工具与配置，不访问公司仓库。

`Ui-verification` 从r1本地修改状态，通过实际TortoiseSVN 1.14.9的Update更新至r2，界面明确显示冲突1。实际conflicteditor入口按公开参数调起D盘Differ0.19.0：base为.r1、mine为.mine、theirs为.r2、merged为工作文件。选择我的并保存完成后，结果为value=2、local_note=mine、remote_timeout=60，未留下冲突标记，三个来源哈希不变。此时SVN仍是Conflicted；通过TortoiseSVN“解决”界面确认后变为Modified/Conflicted=false，辅助冲突文件由SVN清理，未执行正式项目提交。

为用户保留 `A-me\demo.differ-test` 的真实未解决冲突，以及“开始冲突测试.lnk”和“保存后标记已解决.lnk”直接入口。只新增此前不存在的`.differ-test`高级合并绑定；原全局Diff/Merge及全部原有高级值均核验不变。备份及仅按配置所有权恢复的脚本已提供。

证据位于 `E:\Differ-Review\2026-11-08-svn-e2e`：01更新冲突截图、03实际启动命令、04/05实际合并画面、06保存后状态、07标记解决后状态、08完成截图、09绑定核验、10留给用户的冲突状态。此次未修改程序代码，无需重新发布。

## 多处增删改冲突样例

用户要求扩大差异量并逐个取舍，新增独立本地仓库和工作副本 `E:\SVN-Conflict-Test\Many-conflicts`，保留原样例及用户内容。新文件 `mine\多处增删改冲突.differ-test` 经真实r1/r2提交与更新进入Conflicted，包含14个独立冲突和7个自动合并对照。区段以编号和中文标题标识，保留独立上下文，覆盖双方修改/新增、两方向删除对修改、删除对段内插入、不同长度替换、交叉删除、整段重写与EOF新增。

当前安装引擎确认14块均可选择。另用verification副本逐块交替取我的/基础/对方，每次待解决数只减少1，其他区段状态不变；每块撤销/重做通过，9组空选择及删后重选通过，全21变化块分别采用单一来源能够精确重建每份原文。验证结果保存在该测试目录的verification-choices.json，用户mine副本未保存未resolve。

真实TortoiseSVN快捷入口已调起新窗口，显示14/14待解决；核对首项与第14项EOF空基础定位，并返回首项供用户试用。使用上一/下一冲突或来源区选择当前区段，下方结果区用于编辑；不会在采用后自动跳走。快捷方式为测试根目录“开始多处增删改冲突测试.lnk”，完成后用“多冲突保存后标记已解决.lnk”。本轮仅增加测试材料，程序与SVN绑定未更改。

新证据：`E:\Differ-Review\2026-11-08-svn-many-conflicts`，含14处待解决运行截图、EOF新增冲突截图与独立选择验证摘要。
