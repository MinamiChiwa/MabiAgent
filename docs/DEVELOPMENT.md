# 开发指南

本文介绍如何编译、调试和修改 MabiAgent。日常使用见 [README](../README.md)，项目许可见 [LICENSE](../LICENSE)。

## 准备环境

在 Windows 10 或 11 的 64 位系统上开发。安装 dotnet/global.json 指定的 .NET 10 SDK；打包脚本使用 Python 3.12。调试库存读取时还需要安装 Npcap。

游戏测试统一使用繁体中文界面。在“环境设定 → 图形”中选择视窗模式、1280×960 解析度、PC 界面样式，并将调整比例设为 100%。这套设置与现有识别图片、点击位置相对应。

NuGet 依赖版本写在各项目的 .csproj 中，packages.lock.json 记录还原结果。调整依赖后，请一起提交锁文件。

## 编译与运行

在仓库根目录执行：

```powershell
dotnet restore dotnet/MabiAgent.Desktop
dotnet build dotnet/MabiAgent.Desktop --no-restore
dotnet run --project dotnet/MabiAgent.Desktop -- --root . --state-dir .tmp/dev-state
```

--root 指向包含 config、assets 和 models 的目录；--state-dir 指定本次运行的数据目录。开发时使用 .tmp/dev-state 之类的独立目录，便于随时重建测试环境。

修改界面时可以使用以下参数：

| 参数 | 用途 |
| --- | --- |
| --ui-preview | 打开界面预览 |
| --page <n> | 选择页面，编号从 0 开始 |
| --mini | 进入迷你窗口 |
| --light / --dark | 设置预览主题 |
| --compact | 使用 1000×700 窗口 |

## 代码从哪里看

| 项目或目录 | 主要职责 |
| --- | --- |
| MabiAgent.Core | 队列、库存、材料需求等基础类型 |
| MabiAgent.Workflows | 选择下一项任务，执行生产、采集、钓鱼、取料和维护流程 |
| MabiAgent.Windows | 截图、图像与文字识别、游戏输入、各页面的操作脚本 |
| MabiAgent.Capture | 接收本机游戏网络数据，按账号、角色和容器保存库存 |
| MabiAgent.Desktop | Avalonia 界面、设置、主程序和编辑工作台 |
| config | 配方、道具、地点、页面和操作配置 |
| assets/templates | 识别按钮及页面所用的小图 |
| models | OCR 模型、来源和许可 |
| tools / tests | 打包工具及其 Python 测试 |

例如，要修改“到达设备后如何安排加工”，先看 ProductionRunner 和 ScreenProductionPort；要修改材料不足时安排多少轮采集，先看材料规划与 AutomationCoordinator；按钮样式和颜色从 Desktop/Styles/Workspace.axaml 查起。

## 修改界面

界面使用 Semi.Avalonia 和 Ursa.Avalonia。新增功能时优先选择现有组件，例如 Banner、PopConfirm、SelectionList 和 Divider，再使用 Avalonia 基础控件组合。

颜色统一定义在 Styles/Workspace.axaml 的深浅主题资源中。主界面和迷你窗口绑定同一个 MainViewModel，任务进度、待处理问题和运行按钮沿用同一份状态。主题选择保存在 ThemeKey，顶栏按钮与维护页共同使用。

Noto Sans SC 字体放在 Desktop/Assets/Fonts，包含常规、中等和粗体。新图标加入 Controls/UiHelp.cs 的 UiIcon 路径表，保持 Material Design Icons 的 24×24 尺寸。字体和图标许可随发布包提供。

主窗口底部是页面导航，顶部保留拖动区域和常用入口。Ursa 负责窗口按钮，新增顶栏控件时要留出右上角空间。工作台宽度小于 1280 像素时，标题与搜索操作分成两行；搜索框至少 220 像素，普通操作按钮高 36 像素。迷你消息数放在按钮内部，以便完整显示多位数字。

表格的短列按内容定宽，名称和说明列使用星号宽度及 MinWidth；用户可以拖动调整。动画集中在 Controls/Motion.cs 中，通过合成层播放，便于布局测量和截图时获得稳定结果。

## 修改任务流程

所有配方通过共用的 ProductionRunner 执行，采集使用 GatheringRunner，钓鱼使用对应的钓鱼流程。PublishedCatalogCompiler 把用户发布的任务链加入运行配置。新增配方和地点时，应接入这些入口，通用修复才能同时覆盖现有任务和后续新增任务。

库存数量来自网络数据。画面识别用于确认页面、物品身份和操作结果。数量缺失时保留“未知”，规划器据此等待刷新或继续其他任务。主动刷新优先使用私人剧场；确认角色缺少剧场道具时，可以改为同角色重新登录。

购买、出售、委托加工、转移材料等操作，在发送前保存记录。记录标为“已发送、结果未知”时，先核对结果再决定后续动作，以免重复花费。失败通常只影响相关任务；手动暂停后由用户点击继续。停止全部会清除执行记录，游戏内已经完成的操作仍然有效。

输入统一经过 ScreenWorkflowSession、输入工厂和窗口检查。后台模式保持用户当前的前台窗口。测试新步骤时，沿用已有点击、键盘、等待与重试代码。

生产时长在目录中保存标准值。生活 VIP 在 ScreenProductionPort 中换算有效时长，ProductionRunner 在发送委托前记录预计完成时间。背包整理按当前采集目标和用户指定的材料选择出售对象，留存数量取设置下限与生产需求的较大值。缺料先查仓库，自动购买仅支持空瓶和纸。

### 添加地点或配方

1. 在工作台选取相似模板，新建任务链。
2. 设置唯一运行键，补齐目标和稳定识别图片。
3. 保存并发布，检查编译结果。
4. 使用独立数据目录，先测试识别与导航，再测试整段操作。
5. 新配方填写标准加工时长和产量；检查材料来源是否齐全。

## 测试

以下命令可在源码目录运行：

```powershell
dotnet test dotnet/MabiAgent.Tests
dotnet test dotnet/MabiAgent.Workflows.Tests
dotnet test dotnet/MabiAgent.Capture.Tests
dotnet test dotnet/MabiAgent.Editor.Tests --filter "FullyQualifiedName!~ProcessingNavigationTests"
python -B -m unittest discover -s tests -p test_native_portable.py
```

ProcessingNavigationTests 和部分 Windows 测试使用本地游戏截图、抓包或交互式桌面。运行这些测试前需要准备对应资料，并单独安排输入测试，避免与 UI 检查争用窗口焦点。仓库中的合成测试数据可直接使用。

UI 检查会创建实际窗口，依次切换主题、窗口尺寸和页面。运行命令中的路径请换成自己的位置：

```powershell
MabiAgent.Desktop.exe --self-check --interaction-check --native-check --root <源码目录> --state-dir <测试数据目录> --report <报告.json>
```

- --interaction-check 检查表单、按钮、布局、主题切换和迷你窗口。
- --native-check 检查 OCR、OpenCV 等依赖能否加载。
- --mini-check 可单独检查迷你窗口，包含 1、12、123 条消息计数。
- --preview <png> 或 --preview-suite <目录> 输出图片，供人工检查。

先查看报告中的 ok 和错误信息，再核对截图。测试报告说明程序与界面的检查结果；涉及游戏操作的改动，还要单独记录游戏中的实际结果。

## 生成发布包

```powershell
# 构建到 dist/native
python -B tools/build_portable.py --output dist/native

# 更新本机现有安装，保留 data
python -B tools/build_portable.py --update

# 导出给用户的压缩包
python -B tools/build_portable.py --export-clean
```

发布工具复制配置、图片、模型和许可证，运行界面检查，然后生成 release-manifest.json，记录各文件的大小与 SHA256。导出 ZIP 时按这份清单收集文件。

`--update`完成安装更新后会自动导出ZIP；只更新说明时可单独运行`--export-clean`。不指定导出路径时，ZIP放在当前安装目录旁，名称为`MabiAgent-版本号-win-x64.zip`。同版本更新覆盖原文件；导出校验成功后移除同目录其他版本的MabiAgent发布ZIP及旧的“干净交付”文件，只保留当前版本。其他名称的压缩包保持原样。

版本写在 dotnet/Directory.Build.props，窗口标题和包内简短说明读取该值。README 的版本号需要一并修改。当前按补丁位递增，例如 0.5.2、0.5.3。

包根目录的“使用说明.txt”给首次使用者看，只写准备工作和启动步骤；详细帮助来自 README，开发帮助来自本文。文字调整后同时更新对应包内文件和清单。只改文档时可保留现有程序文件，重新导出 ZIP，并核对清单与压缩包内容。

## 数据与历史记录

用户文件集中在 --state-dir 指定的目录。运行日志按大小轮换；进度和故障日志超过 256 KiB 后保留最后 100 行。任务链保留最近 20 个版本，已删除任务链保留 30 天，锚点历史保留预设与最近 50 条记录。图片按内容哈希保存，缩略图按请求宽度解码，缓存最多256条弱引用；显示控件持有正在使用的图像。

OCR、工作台和兼容编辑器在首次使用时创建。OCR继续共用一个ONNX Runtime会话；变宽裁图使用直接CPU分配，避免内存池保留大裁图的峰值。修改这类设置要同时比较内存和识别耗时。依据是Avalonia的Bitmap.DecodeToWidth接口和ONNX Runtime的SessionOptions，不添加第二套图像或识别引擎。

接口依据：Avalonia源码`src/Avalonia.Base/Media/Imaging/Bitmap.cs`中的DecodeToWidth，以及Microsoft ONNX Runtime源码`csharp/src/Microsoft.ML.OnnxRuntime/SessionOptions.shared.cs`中的EnableCpuMemArena/EnableMemoryPattern。两者分别属于AvaloniaUI/Avalonia与microsoft/onnxruntime官方仓库。

`--self-check --memory-check`在隔离状态中测量启动、打开工作台和12次变宽OCR的工作集、私有内存、托管内存及耗时，报告末尾的完整回收仅用于诊断。请在新进程、相同构建和相同步骤下比较；工作集受系统缓存影响，不能单独当作泄漏证据。普通运行不强制GC，也不调用操作系统裁剪工作集。

相关实现位于 RuntimeDiagnostics、StorageCleanup 和 AnchorImageCache。调整保留数量时，也请检查恢复历史版本和删除任务链的测试。

## 提交前检查

提交内容以源码、通用配置、必要测试和文档为主。个人账号记录、队列、日志、游戏现场截图、抓包和构建产物留在本地。检查暂存区和提交历史，确认其中的资料适合分享。

第三方许可见项目根目录及各组件的许可文件。OCR 模型的来源和校验值在 models/ocr-model.source.json。首次启动说明由 StartupNotice 生成，NpcapAvailability 使用现有接口加载 Npcap 并枚举网卡；依赖异常时向用户提供官网下载入口。
