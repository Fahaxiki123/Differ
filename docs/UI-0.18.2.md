# 0.18.2 圆角修正与实际入口核对

用户指出0.18.1的实际界面仍为方角。上轮交付的缺陷有两项：只处理当前定位条，普通差异条仍为直线边框；只核对工程正式目录，漏查开始菜单仍指向D盘0.17.1。不能仅凭用户局部截图认定具体运行版本，也不能以选中状态测试代替全状态验收。

本次修改：

- 普通与选中提示条统一5 DIP宽、2.5 DIP圆头，真实端头内收1 DIP；同组多行中段连续。
- 选中正文轮廓使用4 DIP圆角；保留正文行高、背景、比较及编辑行为。
- 普通标记使用现有Monaco可见行装饰和组首尾类别，不增加全文件DOM。当前定位遮罩只占5 DIP且不接收鼠标，保留折叠入口。
- 使用与用户图类似的Markdown三个分散修改、一个三行修改，分别验证未选择、选中及切换后状态。

证据目录：`E:\Differ-Review\2026-11-08-rail-correction`。before-selected.png和after-selected.png均为同机器、同窗口、同内容、同一选择位置；comparison.png仅裁切并以最近邻放大2倍。after-line24.png对应用户图中的第三条选中状态。navigation目录另有真实WebView2浅深截图与四角、跨行接缝像素检查。

已确认的入口：SVN全局命令指向E盘固定发布目录；开始菜单Differ.lnk指向`D:\Differ\Differ.App.exe`，更新前为0.17.1，安装清单也为0.17.1。已备份旧安装到证据目录installed-0.17.1-backup，安装更新不选择SVN绑定，前后核对Diff值。

开发规范已更新到4.2.2。安装、全量验证和清理结果在收尾后记录。

收尾验证：Release build、566项全量测试、format通过。固定发布与离线安装包0.18.2生成后，以/INTEGRATE=0原位升级D:\Differ，安装器退出0；D盘EXE/DLL版本与安装清单已更新，开始菜单仍指向原D盘位置，安装后Editor CSS与E盘正式目录哈希一致，SVN Diff值与备份一致。随后直接启动D盘真实程序，用F8选中相同Markdown差异，保存installed-selected.png；comparison-installed.png为真正安装版的同内容局部放大对照。临时测试运行目录已移入回收站，保留旧D盘安装备份与证据。清理Apply/Check记录在证据目录。
