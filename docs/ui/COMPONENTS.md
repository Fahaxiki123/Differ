# Differ.UI 组件目录

## 接入方式

项目引用 `src/Differ.UI/Differ.UI.csproj`。独立 WPF 应用在应用资源中合并 `/Differ.UI;component/Themes/Light.xaml` 或 `Dark.xaml`，再合并 `/Differ.UI;component/Themes/Controls.xaml`。Controls 已合并 Metrics。Differ.App 通过自己的主题根和 ThemeService 加载配色。

控件通过 `Style` 显式选择样式，换肤用的 Brush 通过 `DynamicResource` 获取。尺寸来自 `Dui.Metrics.*`，例如 `ToolbarRowHeight` 是 GridLength，`ToolbarHeight` 是 Double，`PanePadding` 是 Thickness，`ControlRadius` 是 CornerRadius。

```xml
<Button Style="{StaticResource Dui.Button.Primary}"
        Command="{Binding SaveCommand}"
        Content="保存" />
```

## 组件和状态

| 角色 | 样式或控件 | 用法与状态 | Gallery 位置 |
|---|---|---|---|
| 命令按钮 | `Dui.Button.Primary`、`Secondary`、`Quiet`、`Danger`、`Toolbar`、`Icon` | 保留 WPF `Command` 和 `CanExecute`。悬停、按下、键盘焦点和禁用可区分。图标按钮需可访问名称和工具提示。危险按钮配明确文字及必要确认。 | 基础控件、工作区 |
| 输入 | `Dui.Input.TextBox`、`Dui.Input.ComboBox` | 用 WPF 原生输入和选择。错误由 `Validation.HasError` 驱动，提示显示校验内容。TextBox 的 `IsReadOnly` 可用于保留选择与复制。ComboBox 的 `IsReadOnly` 遵循原生可编辑文本区语义，禁止整项选择时使用 `IsEnabled`。 | 基础控件、右上主题选择 |
| 选择 | `Dui.CheckBox`、`Dui.Toggle.Switch`、`Dui.Toggle.Segment`、`Dui.Radio.Segment` | 用原控件的 `IsChecked`，单选分段用 RadioButton 的 `GroupName`。独立布尔选项用 CheckBox 或 ToggleButton。混合状态仅用于支持三态的控件。 | 基础控件、工作区 |
| 工具栏 | `Dui.ToolBar` | 样式用于 WPF ToolBar。保留原生溢出行为，必要命令由调用方设为 `ToolBar.OverflowMode="Never"`。低频项允许溢出。 | 工作区，缩至 720 DIP 可检查 |
| 标签 | `Dui.Tab` | 样式用于 TabItem，支持选中、悬停、焦点与禁用。长标题、关闭按钮和未保存文字由头部模板提供，标签内容的持久性由宿主负责。 | 工作区的两个合成文件 |
| 窗格与状态 | `Dui.PaneHeader`、`Dui.StatusBar` | 样式用于 Border，传入标题、状态或命令组合。信息、警告和错误可用 `Dui.Status.Info/Warning/Error` 与对应 `*Surface` 画刷，配合文字或图标。 | 工作区、菜单与反馈 |
| 菜单与提示 | `Dui.Menu`、`Dui.MenuItem`、`Dui.ContextMenu`、`Dui.ToolTip`、`Dui.Popup` | 菜单保留原生导航、复选、启用状态和快捷键提示能力。若提示内容较长，用可换行的 TextBlock。图标提示不能代替可访问名称。 | 菜单与反馈 |
| 对话框 | `Dui.Dialog` | 设到原生 Window，保留系统标题栏。调用方指定 `Owner`、默认按钮与取消按钮，关闭后恢复合理的键盘焦点。 | 菜单与反馈、基础控件的危险操作示例 |
| 进度和空状态 | `Dui.ProgressBar`、`Dui.EmptyState` | 进度由 `Value` 或 `IsIndeterminate` 表达，处理期间由调用方禁用重复执行并给出文字。空态由 ContentControl 承载标题与下一步。 | 菜单与反馈 |
| 滚动与分隔 | `Dui.ScrollBar`、`Dui.GridSplitter` | 保留原生滚动及分隔拖动。列表虚拟化、滚动同步和窗格最小尺寸由宿主设置。 | 工作区的长行、长内容及中线 |
| 图标 | `Differ.UI.Controls.AppIcon` 的 `Kind`、`Size`、`Foreground` | 默认 16 DIP，可用 `Dui.Metrics.IconSizeLarge` 取得 20 DIP。随宿主前景色绘制的本地矢量图形，未知枚举回退为文件图标。图标本身不占键盘焦点。 | 表面与字体、工作区 |

## 主题与职责

- 通用库的 `Dui.*` 涵盖表面、交互状态、图标、字体和尺度。文件来源、差异语义、保存权限及正文模型由 Differ.App 管理。
- Gallery 直接引用 Differ.UI。工作区的文字、差异色标、导航序号和未保存状态均为内存中的合成示例。实际比较行为和运行证据见 [第一阶段记录](PHASE1.md)。
- Gallery 合成色标的文字用共享主文字色，竖条用独立的 `Gallery.Diff.*`。预览宽度 720/1024 DIP、示例长行和标题字号是 Gallery 演示参数。
- Dialog 的 `Owner`、标签关闭、工具栏溢出、文档绑定和容器回收由消费页按自己的交互要求组合。

## 运行和试用

从仓库根目录启动源码示例。

```powershell
dotnet run --project .\samples\Differ.UI.Gallery\Differ.UI.Gallery.csproj -c Release
```

- 用主题选择器或 F9 切换浅深色，用右侧按钮切换 720 × 560 和 1024 × 720 DIP。工作区可试切换、关闭并恢复标签、拖动分隔线及浏览长内容。
- 在基础控件用 Tab 检查焦点，用 Space 或 Enter 激活操作，把校验示例从 `120` 改为 `42`。
- 在菜单与反馈用右键或 Shift+F10 打开上下文菜单。打开示例确认对话框后用 Enter 确认或 Esc 取消。点击开始处理可观察忙碌和完成状态。
