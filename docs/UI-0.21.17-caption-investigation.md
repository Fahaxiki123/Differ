# 0.21.17 主题切换标题栏接缝调查

2026-11-12。依据用户提供的两张720×620前台截图及当前源码检查；本次没有操作窗口、修改生产代码或替换开发预览。

## 已确认

- 差色边界位于x=564，即720−156。`DiffWindow.xaml:343–348` 将右侧156 DIP留给原生窗口按钮，只有前两列绘制WPF的 `Dui.Surface.Tabs` 实色背景；右侧列透明，露出DWM标题栏底板。顶部玻璃区域为38 DIP。
- `DiffWindow.xaml.cs:138–147` 将同名画刷的单个RGB值写入 `DWMWA_CAPTION_COLOR`；`OnEditorThemeFrame` 先更新主题画刷，再更新WPF雾层。属性写入和WPF画刷取值相同，不能证明两个渲染系统已经在同一个显示帧呈现相同底色。
- 用户图中原生按钮区域也有空间渐变，不能归结为“雾光完全没覆盖按钮区”。明确问题是同一雾场下面的WPF实底与DWM底板在该矩形区域显示不一致。
- 在避开图标和红色标注的y=30处，图1从x563的RGB(73,66,49)跳到x564的(79,71,54)；图2从(198,194,183)跳到(207,203,192)。y=40的工具栏同一横坐标没有相应突变。原图及完整采样记录在 `D:\项目归档\Differ\2026-11-12-caption-investigation`。

## 现有验证为什么没有挡住

`ThemeMistContinuityTests.FogContinuesThroughCaptionAndClearsOnCancellation` 使用普通 `WindowStyle.None` 窗口，仅将ThemeMistOverlay渲染到 `RenderTargetBitmap`。它验证了WPF雾光在原生按钮“坐标”处连续，但没有实际原生按钮及DWM最终合成。历史完整窗口记录也明确是离屏或模拟活动状态，前台真实点击尚待验证。因此690项测试通过不能作为这一前台效果已经正确的证据。

## 根因范围与修复方向

接缝对应两种标题栏底板的交界，已由布局、代码和像素位置交叉确认。尚未通过本次自然播放逐帧采样区分：DWM底色呈现提前/滞后，或原生玻璃区域对底色的处理差异。不能把具体时序方向写成已经证实。

应优先验证整条标题栏共用同一种底板/合成路径，并保留原生最小化、最大化、关闭及贴靠行为。另一个保守方案是让标题栏采用完整一致的过渡方式、雾光仅作用于正文，但这会改变现有已批准视觉范围，应在具体样板中说明。仅改变雾光颜色或扩大WPF覆盖矩形不能证明接缝已经消失。

验收需使用真实前台窗口自然播放，覆盖浅→深、深→浅、720与宽窗、起步/中段/结束、快速反向、窗口按钮与贴靠命中，并在x=窗口宽度−156附近记录双方最终屏幕像素；WPF离屏图只能作补充。本问题登记为当前前台验收未通过项，在收口前保留旧预览临时副本。

接口依据：[DWMWA_CAPTION_COLOR](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) 接收COLORREF；[WindowChrome.GlassFrameThickness](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome.glassframethickness)描述玻璃边框与标准窗口按钮的关系。
