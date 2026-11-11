# 开发指南

本文介绍如何编译、调试和修改 MabiAgent。日常使用见 [README](../README.md)，项目许可见 [LICENSE](../LICENSE)。

## 准备环境

在 Windows 10 或 11 的 64 位系统上开发。安装 dotnet/global.json 指定的 .NET 10 SDK；打包脚本使用 Python 3.12。调试库存读取时还需要安装 Npcap。

游戏测试统一使用繁体中文界面。在“环境设定 → 图形”中选择视窗模式、1280×960 解析度、PC 界面样式，并将调整比例设为 100%。这套设置与现有识别图片、点击位置相对应。

NuGet 依赖版本写在各项目的 .csproj 中，packages.lock.json 记录还原结果。调整依赖后，请一起提交锁文件。

桌面程序使用 `CETCompat=false` 生成 apphost，根目录便携入口的 `CreateAppHost` 同时设置 `DisableCetCompat=true`，减少硬件栈保护与旧系统环境的兼容冲突。更新进程复制同一入口。修改发布目标时应检查两个 EXE 的 PE 扩展 DLL 特性，防止自定义入口遗漏此设置。

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
| MabiAgent.Update | 校验更新清单、暂存文件、备份并原子替换已安装文件 |
| MabiAgent.Desktop | Avalonia 界面、设置、主程序和编辑工作台 |
| config | 配方、道具、地点、页面和操作配置 |
| assets/templates | 识别按钮及页面所用的小图 |
| models | OCR 模型、来源和许可 |
| tools / tests | 打包工具及其 Python 测试 |

例如，要修改“到达设备后如何安排加工”，先看 ProductionRunner 和 ScreenProductionPort；要修改材料不足时安排多少轮采集，先看材料规划与 AutomationCoordinator；按钮样式和颜色从 Desktop/Styles/Workspace.axaml 查起。

## 执行架构与边界

Core 负责队列、材料需求与预留；Workflows 选择并执行任务；Windows 提供截图、识别和输入适配；Capture 从网络数据生成分账户、分角色、分容器的库存；Desktop 负责 Avalonia 界面和运行接线。抓包是库存数量的唯一来源，画面识别用于确认页面、身份与动作结果，预计采集轮数不计入库存。

任务链表达固定点击、按键、文字、延时和声明的稳定图文等待。固定动作不额外截图或识别；需要等待时只识别稳定字形或不透明静态图形。动画按钮、台词、半透明组件、快捷键提示和角色名等可变内容从识别对象中排除，用户明确授权的钓鱼上钩图标与进度条例外。

所有正式任务、工作台整链和分段测试共用输入工厂与最小守卫。守卫检查目标进程和窗口、输入通道、暂停、最小化、几何边界与互斥；真正消费另存持久意图并处理重复发送。后台模式不抢焦点、不占鼠标、不移动游戏窗口，前台鼠标只在用户显式选择时检查焦点。

可逆步骤使用有界恢复。一次局部失败只阻断依赖它的任务，独立任务继续。购买、出售、委托、领取和转移等消费在发送前保存意图；已发送但结果未知时只核账，不重放。设备可以并行，同一设备按顺序加工；采集在原生批结束切换。用户队列、设置、任务链、待核账和显式删除分别保存，未知游戏机制先查长期记忆，再向用户确认。

公共通知识别先于加工局部识别；导航等待收到通知后由共用入口关闭，再继续原检查点。设备寻路保存从发送起120秒的总期限，暂停、端口重建和未知帧不刷新；加工菜单未知等待在45秒结束。超时释放本批可逆导航并隔离任务，已提交生产槽、到期时间及消费意图保留。到场仍通过自动重开的设备页确认。

列表完整名称按字形选择模式：`list_label`用二值笔画及留白区分前后缀；`list_name`同时比较灰度完整名称与首字，适合亮暗变化且首字不同的钓鱼点。钓鱼误入其他点位详情时关闭重选，最多三次，超限清理导航占用并由调度隔离该任务。

取料、维修和购买共用 `maintenance.map_navigation`：先确认提尔克那标题或从世界地图选择提尔克那，选择后最多等待8秒；标题出现后才居中店铺、放大并选择目标。其他地图的同名店铺不满足定位条件。标题使用固定白色字形，普通与高亮度二值变体共用原图，分离穿过页眉的底图灰字，字形阈值保持0.97。 缩放每次只发送一个双指手势，在声明等待点按固定店名重新居中，最多九次判断最大比例；店名离开视野时最多两次有限缩小重定位。目标保管箱或NPC已可见时直接选择，不因店名被遮挡而恢复。采集及生产接手遗留地图或已声明退出页时复用公共维护退出，在当前观察上关闭并等待返回场景。

## 修改界面

界面使用 Semi.Avalonia 和 Ursa.Avalonia。新增功能时优先选择现有组件，例如 Banner、PopConfirm、SelectionList 和 Divider，再使用 Avalonia 基础控件组合。

颜色统一定义在 Styles/Workspace.axaml 的深浅主题资源中。主界面和迷你窗口绑定同一个 MainViewModel，任务进度、待处理问题和运行按钮沿用同一份状态。主题选择保存在 ThemeKey，顶栏按钮与设置页共同使用。

Noto Sans SC 字体放在 Desktop/Assets/Fonts，包含常规、中等和粗体。新图标加入 Controls/UiHelp.cs 的 UiIcon 路径表，保持 Material Design Icons 的 24×24 尺寸。字体和图标许可随发布包提供。

主窗口底部导航为“队列／仓库／设置”，顶部保留拖动区域和常用入口；任务链、页面与锚点从设置页的“配置工作台…”进入。Ursa 负责窗口按钮，新增顶栏控件时要留出右上角空间。工作台宽度小于 1280 像素时，标题与搜索操作分成两行；搜索框至少 220 像素，普通操作按钮高 36 像素。迷你消息数放在按钮内部，以便完整显示多位数字。

表格的短列按内容定宽，名称和说明列使用星号宽度及 MinWidth；用户可以拖动调整。动画集中在 Controls/Motion.cs 中，通过合成层播放，便于布局测量和截图时获得稳定结果。
队列列表保持固定行高和受约束视口，使虚拟化只创建可见行及缓冲区；计划列默认使用轻量文本，双击进入编辑时才创建输入控件，结束编辑后换回文本并保存。刷新以整批替换集合，队列文件与库存版本都未变化时跳过重建，保留选中与滚动位置。微交互只改变换或不透明度且不超过 250 毫秒；系统开启减弱动效时不播放位移。`--self-check` 内的队列性能自检输出行容器数、集合通知次数和输入控件实例数，改动后应复跑并与上次数值比较。

性能验收同时参考 Apple [Understanding and improving SwiftUI performance](https://developer.apple.com/documentation/xcode/understanding-and-improving-swiftui-performance) 与 [WWDC23 10160](https://developer.apple.com/videos/play/wwdc2023/10160/)：先在相同数据、窗口尺寸和构建下测量并定位长更新，减少无效更新，再复测；只有可见区域的内容才应按需创建。首访设置页等新增页面要记录总耗时、布局阶段、视觉后代数和可见/离屏控件数，改前改后采用同一条件。结构虚拟化数值、单次观感或未复测的耗时都不能证明卡顿已解决，也不能伪造时延或 FPS 门槛。

发布侧可用 `python tools/build_update_assets.py --package <安装目录> --version <版本> --output <资产目录>` 生成 `update-manifest.json` 与逐文件 `z-update-blob-<sha256>.bin`。文件列表只来自正式 `release-manifest.json`，`data/` 不入清单。客户端直连 `github.com/<仓库>/releases/latest`，从重定向取得版本标签，再读同版本 `releases/expanded_assets/<标签>` 的附件地址和 SHA-256；清单直连官方发布资产，检查更新不访问 API 地址，镜像只传输 blob 且失败回退官方；任一大小或哈希不符即放弃整个暂存目录。应用前先恢复未完成日志，恢复失败则拒绝覆盖；更新程序等待原程序退出，超时只报错退出，不终止原程序。

正式发布包含完整 ZIP、SHA256SUMS.txt 与自包含增量清单。对每个受支持旧版本，将包内 release-manifest.json 通过 --legacy-baseline 参数传给生成器（可重复）；任一旧版需要的变化文件保留独立资产，共同未变的文件归入 z-update-pack-<摘要>.zip。旧客户端跳过共同文件，新客户端在需要时只下载一次对应包并定向读取、校验成员。生成器在写入前检查附件总数，超限时停止发布。增量资源统一使用 z-update- 前缀，完整包 MabiAgent-<版本>-win-x64.zip 在附件中排最前。

界面简繁词库位于 `MabiAgent.Desktop/Localization/`。运行时本地化覆盖表格列标题、行内文本、帮助提示和动态状态；工作台分别维护显示名称与原始配置值。任务数量单位由内部任务类型决定，队列中的拖动只调整列表位置。任务按材料、加工时间和当前状态自动安排。

## 修改任务流程

所有配方通过共用的 ProductionRunner 执行，采集使用 GatheringRunner，钓鱼使用对应的钓鱼流程。PublishedCatalogCompiler 把用户发布的任务链加入运行配置。新增配方和地点时，应接入这些入口，通用修复才能同时覆盖现有任务和后续新增任务。

两种钓鱼共用 IdleFishingRunner，从首次发送抛竿开始持续计时，到目标时长即退出；轮间与无反应等待计时，暂停、背包及其他流程中断计时，重启不补算离线时间。ScreenFishingPort 从上钩/进度条首次消失的源帧起算三秒收竿动画，计入截图与识别耗时；动画中的未知过渡保留原时间，场景HUD恢复后已到时就同次调度原地按空格续抛。钓鱼等待采用100毫秒观察节奏，已用掉的执行时间从间隔中扣除；首次、窗口过期及背包返回才走 W/S 60 毫秒、等待 300 毫秒、空格 80 毫秒的共用后台输入。动态钓鱼按钮不参与图像识别。

抛竿前保存意图，20 秒无轮次时优先检查本帧上钩/进度条，再轻移重抛。收竿观察超时释放本次等待，定期负重检查需累计20轮且600秒周期到期；满包提示或连续无反应可提前请求共用检查。读取失败局部退出，付款、生产和整理等实际消费仍沿原核账规则。

库存数量来自网络数据。画面识别用于确认页面、物品身份和操作结果。数量缺失时保留“未知”，规划器据此等待刷新或继续其他任务。主动刷新使用快速重进：主界面 ESC → 点击“變更外貌/重生” → 重生页 ESC → 取消提示空格 → 等待重新加载回主界面。客户端目录从所选进程自动定位，读取本地PE及IL2CPP元数据适配协议号与物品字段；仅定位失败提供目录输入。缓存按文件指纹复用，未识别版本保持库存未知。

购买、出售、委托加工、转移材料等操作，在发送前保存记录。记录标为“已发送、结果未知”时，先核对结果再决定后续动作，以免重复花费。失败通常只影响相关任务；手动暂停后由用户点击继续。停止全部会清除执行记录，游戏内已经完成的操作仍然有效。

输入统一经过 ScreenWorkflowSession、输入工厂和窗口检查。后台模式保持用户当前的前台窗口。测试新步骤时，沿用已有点击、键盘、等待与重试代码。

生产时长在目录中保存标准值。生活 VIP 在 ScreenProductionPort 中换算有效时长，ProductionRunner 在发送委托前记录预计完成时间。背包达到整理阈值时，先点击游戏内“整理”；整理后仍超重，再按当前采集任务和用户指定的材料决定生产减重与出售对象，留存数量取设置下限与生产需求的较大值。缺料先查仓库，自动购买仅支持空瓶和纸。

### 添加地点或配方

1. 在工作台选取相似模板，新建任务链。
2. 设置唯一运行键，补齐目标和稳定识别图片。
3. 保存并发布，检查编译结果。
4. 使用独立数据目录，先测试识别与导航，再测试整段操作。
5. 新配方填写标准加工时长和产量；检查材料来源是否齐全。

## 自动导航任务设计

自动导航任务包含创建、AutoPlay 选择、状态更新、移除和切回等生命周期事件。解析时按帧头声明的长度读取可变字段，不能把某次观测到的字节长度、偏移或数字位置写成固定合同。

任务选择、完成字符串和设备到达是不同状态。导频只用于只读诊断；目标是否到达由固定脚本声明的必要结果节点确认。抓包、截图和输入观察分别记录来源、时间、会话与资源版本。

## 测试

以下命令可在源码目录运行：

```powershell
dotnet test dotnet/MabiAgent.Tests
dotnet test dotnet/MabiAgent.Workflows.Tests
dotnet test dotnet/MabiAgent.Capture.Tests
dotnet test dotnet/MabiAgent.Editor.Tests --filter "FullyQualifiedName!~ProcessingNavigationTests"
dotnet test dotnet/MabiAgent.Desktop.Tests
dotnet test dotnet/MabiAgent.Update.Tests
dotnet test dotnet/MabiAgent.Windows.Tests
python -B -m unittest discover -s tests -p test_native_portable.py
```

以上命令覆盖七个 .NET 测试项目。ProcessingNavigationTests 和部分 Windows 测试使用本地游戏截图、抓包或交互式桌面；运行这些测试前需要准备对应资料，并单独安排输入测试，避免与 UI 检查争用窗口焦点。仓库中的合成测试数据可直接使用。

UI 检查会创建实际窗口，依次切换主题、窗口尺寸和页面。运行命令中的路径请换成自己的位置：

```powershell
MabiAgent.Desktop.exe --self-check --interaction-check --native-check --root <源码目录> --state-dir <测试数据目录> --report <报告.json>
```

- --interaction-check 检查表单、按钮、布局、主题切换和迷你窗口。
- --native-check 检查 OCR、OpenCV 等依赖能否加载。
- --mini-check 可单独检查迷你窗口，包含 1、12、123 条消息计数。
- --preview <png> 或 --preview-suite <目录> 输出图片，供人工检查。

先查看报告中的 ok 和错误信息，再核对截图。测试报告用于核对程序与界面；涉及游戏操作的改动同时记录游戏中的实际结果。

## 识别与性能

识别读取一张当前截图，并校验来源时间、会话、窗口和资源版本。固定动作直接使用坐标与时序；只有脚本声明的等待节点才识别稳定图文。等待期间可以复用当前有效观察，但过期或来源不符的观察不得参与输入。

OCR 只处理需要读取文字或非库存数量的步骤。可缓存稳定裁图和不可变输入，新帧到来时更新；固定动作不额外截图，避免为了复用缓存增加识别次数。性能修改应在相同数据、窗口尺寸和构建条件下比较改前改后结果，并分别记录识别、截图、输入准备和发送阶段。

## 生成发布包

```powershell
# 构建到 dist/native
python -B tools/build_portable.py --output dist/native

# 更新本机现有安装，保留 data
python -B tools/build_portable.py --update

# 导出给用户的压缩包
python -B tools/build_portable.py --export-clean
```

发布工具复制配置、图片、模型和许可证，运行界面检查，然后生成 release-manifest.json，记录各文件的大小与 SHA256。导出 ZIP 时按这份清单收集文件，发布包不包含 PDB；源码、项目和包锁保留，以便按需构建调试版本。

`--update`完成安装更新后会自动导出ZIP；只更新说明时可单独运行`--export-clean`。不指定导出路径时，ZIP放在当前安装目录旁，名称为`MabiAgent-版本号-win-x64.zip`。同版本更新覆盖原文件；导出校验成功后移除同目录其他版本的MabiAgent发布ZIP及旧的“干净交付”文件，只保留当前版本。其他名称的压缩包保持原样。

版本写在 dotnet/Directory.Build.props，窗口标题和包内简短说明读取该值；README 的版本号需要同步。日常小修复沿用当前版本，集中交付或用户明确要求版本变更时再递增。

包根目录的“使用说明.txt”给首次使用者看，只写准备工作和启动步骤；详细帮助来自 README，开发帮助来自本文。文字调整后同时更新对应包内文件和清单。只改文档时可保留现有程序文件，重新导出 ZIP，并核对清单与压缩包内容。

## 数据与历史记录

用户文件集中在 --state-dir 指定的目录。运行日志按大小轮换；进度和故障日志超过 256 KiB 后保留最后 100 行。任务链保留最近 20 个版本，已删除任务链保留 30 天，锚点历史保留预设与最近 50 条记录。图片按内容哈希保存，缩略图按请求宽度解码，缓存最多256条弱引用；显示控件持有正在使用的图像。

工作台和兼容编辑器在首次使用时创建。OCR 在绑定自动化服务时预加载，继续共用一个 ONNX Runtime 会话，并使用默认内存池和内存模式配置。运行识别保留两线程、五秒超时和省电开关；修改这些设置时，同时检查识别结果、耗时和内存。

接口依据：Avalonia源码`src/Avalonia.Base/Media/Imaging/Bitmap.cs`中的DecodeToWidth，以及Microsoft ONNX Runtime源码`csharp/src/Microsoft.ML.OnnxRuntime/SessionOptions.shared.cs`中的EnableCpuMemArena/EnableMemoryPattern。两者分别属于AvaloniaUI/Avalonia与microsoft/onnxruntime官方仓库。

`--self-check --memory-check`在隔离状态中测量启动、打开工作台和12次变宽OCR的工作集、私有内存、托管内存及耗时，报告末尾的完整回收仅用于诊断。请在新进程、相同构建和相同步骤下比较；工作集受系统缓存影响，不能单独当作泄漏证据。普通运行不强制GC，也不调用操作系统裁剪工作集。

相关实现位于 RuntimeDiagnostics、StorageCleanup 和 AnchorImageCache。调整保留数量时，也请检查恢复历史版本和删除任务链的测试。

## 架构参考

MAA 的可复用设计包括事件与最近期限唤醒、单工作线程调度、源帧时间与新鲜度校验、资源修订失效、按引用加载资源及有界缓存。任务选择、页面识别、输入与截图按职责分层，业务完成由明确结果节点判断。

Android ADB 与 minitouch 控制 Android 系统，不能直接推导 Windows 游戏能力。Windows 消息、隐藏窗口、最小化、无 GUI 宿主和锁屏是不同边界。MabiAgent 继续使用 Avalonia、Semi 和 Ursa，并通过受守卫的输入工厂接入触控、键盘和文字。

ORT 关闭 spinning 可以降低空闲 CPU，但可能增加单次请求延迟；该配置适合作为响应时间允许时的低占用选项。资源修订和源帧元数据只在变化时刷新缓存，减少重复识别，同时保证输入使用当前画面。

MAA 核心采用 AGPL-3.0-only，MaaFramework 采用 LGPL v3。项目只借鉴通用设计并复用已有 .NET/Win32/ONNX 依赖，不复制 MAA 核心代码，不新增 MaaFramework 二进制。

## 工程教训

- 先判断本次动作实际需要什么，只为脚本声明的稳定等待识别，不为每次点击叠加页面、数量、双帧和包级证明。
- 抓包库存与输入脚本分工；系统边界、互斥和消费去重放在共享守卫的一处。
- 失败先修最小根因，再复验受影响步骤。相关步骤有界恢复，独立任务继续。
- 重复守卫、逐步证明和不断扩张的状态机会阻断简单点击链；离线测试不能替代对应业务流程本身。
- 只做必要聚焦回归；模拟、历史和离线结果按实际范围记录。切换实现时同步删除旧入口、配置和专用测试，保留单一执行器。

## 提交前检查

提交前运行与改动相关的构建和测试，并检查文档、配置和发布清单中的引用一致。

第三方许可见项目根目录及各组件的许可文件。OCR 模型的来源和校验值在 models/ocr-model.source.json。首次启动说明由 StartupNotice 生成，NpcapAvailability 使用现有接口加载 Npcap 并枚举网卡；依赖异常时向用户提供官网下载入口。

内置使用指南使用 MarkView.Avalonia（Markdown 渲染）与 Markdig（解析），文档直接嵌入根目录双语 README，开发指南和许可的相对链接在窗口内打开。窗口沿用 Ursa/Semi 组件与主题，简繁切换沿用持久偏好并恢复阅读比例。顶栏语言图标引用 SemiIconLanguage；更新入口仅在 HasUpdateNotification 为真时显示，设置页保留手动检查。聚焦界面验收可运行 `--self-check --guide-check --guide-preview <目录> --report <文件>`，覆盖 Markdown、语言、滚动、主题、更新显隐与窗口释放。
