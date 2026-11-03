# 开发指南

这份文档写给想阅读、修改或构建 MabiAgent 的开发者。使用方法见仓库根目录的 [README](../README.md)。

参与开发前请注意：本项目以 [PolyForm Noncommercial 1.0.0](../LICENSE) 发布，只允许非商业用途；它是测试性质的工具，对游戏账号的风险由使用者自行承担。提交的代码同样遵循这份许可。

## 环境

| 工具 | 版本 | 用途 |
| --- | --- | --- |
| Windows | 10 / 11 x64 | 运行和测试（输入、截图、抓包都依赖 Win32） |
| .NET SDK | 10.0.402（见 `dotnet/global.json`） | 编译全部项目 |
| Python | 3.12+ | 仅用于打包脚本和打包测试 |
| Npcap | 最新版 | 可选，测试抓包时需要 |

NuGet 依赖的版本写死在各个 `.csproj` 里，并启用了锁文件（`packages.lock.json`）。新增或升级依赖后要一并提交更新后的锁文件。

## 目录结构

```
dotnet/
  MabiAgent.Core/        队列、库存、规划等基础类型，不依赖 UI 和 Win32
  MabiAgent.Workflows/   调度器和各类运行器（生产、采集、钓鱼、仓库、维护）
  MabiAgent.Windows/     画面识别、模板匹配、OCR、后台输入、界面脚本
  MabiAgent.Capture/     被动抓包，按账号 / 角色 / 容器隔离库存
  MabiAgent.Desktop/     Avalonia 界面、任务链工作台、程序入口
  *.Tests/               对应项目的测试
config/                  页面目录、导航脚本、道具表等运行配置
assets/templates/        识别用的模板图片
models/                  OCR 模型及其来源、许可
tools/                   打包脚本
tests/                   打包脚本的 Python 测试
```

依赖方向是 Core ← Workflows ← Windows / Capture ← Desktop。Core 和 Workflows 不引用 Avalonia 或 Win32，方便在测试里直接跑。

## 编译和测试

```powershell
dotnet restore dotnet/MabiAgent.Desktop
dotnet build dotnet/MabiAgent.Desktop --no-restore

dotnet test dotnet/MabiAgent.Tests
dotnet test dotnet/MabiAgent.Workflows.Tests
dotnet test dotnet/MabiAgent.Editor.Tests --filter "FullyQualifiedName!~ProcessingNavigationTests"
python -B -m unittest discover -s tests -p test_native_portable.py
```

上面这几组测试不需要游戏，也不需要个人数据，可以直接跑。

`MabiAgent.Editor.Tests` 中的 `ProcessingNavigationTests`、`MabiAgent.Windows.Tests` 和 `MabiAgent.Capture.Tests` 里有一部分测试依赖真实截图、抓包或交互式桌面。仓库不分发含个人角色信息的现场截图和抓包，这些测试需要你在本地准备自己的数据。输入相关测试会抢焦点，不要和 UI 自检同时跑。

所有项目开启了 `TreatWarningsAsErrors`，警告也会让编译失败。

## 运行和调试

直接从源码目录运行时，用 `--root` 指向仓库根目录，用 `--state-dir` 指向一个单独的数据目录，避免污染真实数据：

```powershell
dotnet run --project dotnet/MabiAgent.Desktop -- --root . --state-dir .tmp/dev-state
```

常用参数：

| 参数 | 作用 |
| --- | --- |
| `--ui-preview` | 只打开界面，不启动自动化服务和抓包，适合改 UI |
| `--page <n>` | 启动后切到第 n 个页面（0 起） |
| `--mini` | 启动后直接进入迷你窗口 |
| `--light` / `--dark` | 强制浅色或深色主题 |
| `--compact` | 以 1000×700 的窗口尺寸启动，检查窄屏布局 |

## UI 自检

界面有一组离线自检，会真实创建窗口和控件，但不会向桌面或游戏发送任何输入：

```powershell
MabiAgent.Desktop.exe --self-check --interaction-check --native-check `
  --root <仓库根目录> --state-dir <临时目录> --report <报告.json>
```

- `--interaction-check`：交互和布局回归，覆盖深浅两套主题、从 860×600 到 2560×1440 的多种窗口尺寸，以及迷你窗口的展开和收起。
- `--native-check`：加载 OCR 和 OpenCV，确认原生依赖齐全。
- `--preview <png>` / `--preview-suite <目录>`：把界面渲染成图片，用来人工检查视觉效果。

报告里 `ok` 为 `true` 且 `game_input` 为 `false` 才算通过。打包脚本会自动跑一遍。

## 界面约定

- 控件库是 [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia) 和 [Ursa.Avalonia](https://github.com/irihitech/Ursa.Avalonia)。优先用它们已有的控件（`Banner`、`Badge`、`PopConfirm`、`SelectionList`、`Divider` 等），不要自己重写同类控件。
- 颜色全部写成 `Styles/Workspace.axaml` 里的主题资源，深浅两套都要定义，不要在页面里写死颜色。
- 界面字体是内嵌的 Noto Sans SC（`Assets/Fonts`，SIL OFL 1.1），资源键 `WorkspaceFont`。它的字符集是 GB 18030，覆盖全部 Big5 繁体字，游戏里的繁体道具名不会回退到别的字体。
- 表格（`DataGrid`）统一允许拖动列宽。数字、状态等短列写 `Width="Auto"` 按内容自适应，名称、说明等长文本列写 `*` 加 `MinWidth` 填满剩余宽度，不要写死像素宽度。
- 主窗口和调试工作台都是无边框窗口：`ExtendClientAreaToDecorationsHint=True`，由 Ursa 绘制最小化、最大化、关闭按钮。主窗口的 `TitleStrip`、工作台的 `WorkbenchTitleStrip`（都在顶部 40px）以及侧栏品牌区标记为 `WindowDecorationProperties.ElementRole="TitleBar"`，负责拖动；新增顶部控件时不要放进右上角按钮区域。
- 图标统一走 `Controls/UiHelp.cs` 里的 `UiIcon` / `Icon`，路径数据来自 Material Design Icons（Apache-2.0）。新增图标时从 `@mdi/svg` 取 24×24 的路径，加到同一张表里。
- 动画只在合成层上播放（`Controls/Motion.cs`），不要改控件的 `Opacity` 或布局尺寸，否则离屏截图和布局检查会看到中间态。
- 主窗口和迷你窗口共用同一个 `MainViewModel`。新增运行状态时，先在 ViewModel 里算好，再分别绑定，不要在窗口代码里重复计算。

## 运行时的几条硬性规则

这些规则对应着真实出过的问题，改动相关代码时请保持：

- **库存只信抓包。** 屏幕只负责识别页面、身份和动作结果。数量未知时不能当成 0。
- **后台输入不抢窗口。** 所有输入都要经过会话、输入工厂和最小守卫，不能激活或移动游戏窗口。
- **消费先记账再执行。** 买、卖、用道具之前先保存意图；已经发出但结果未知的操作不能自动重放。
- **故障要隔离。** 一个任务失败只暂停相关任务，其他独立任务继续。手动暂停必须由用户显式继续。
- **停止不回滚。** “停止全部”清空执行账，但不会也不能撤销游戏内已发生的操作。
- **只有一条执行路径。** 所有配方和发布的任务链都通过共享执行器运行，由 `PublishedCatalogCompiler` 编译。不要为某个配方单独写一套调度。
- **自动采购白名单只有空瓶和纸。** 仓库优先于采购。背包无解时暂停，不用存仓兜底。
- **生活 VIP 只影响新提交的加工。** 目录里保存标准时长，`ProductionRunner` 在发送委托前确定到期时间，之后不重算。

## 新增采集地点或配方

1. 在任务链工作台里复制最相似的模板。
2. 设置唯一的运行键，补齐能稳定识别的锚点图片。
3. 通过编译校验，再用聚焦流程实际跑一遍。
4. 新增配方时写标准加工时长，不要把 VIP 时长写进目录。

改通用行为时，要确认新配方和已有任务链都走同一个入口，并删掉被替代的旧实现。

## 打包发布

```powershell
# 构建完整发布包到 dist/native
python -B tools/build_portable.py --output dist/native

# 本机原位更新已有安装，保留 data 目录
python -B tools/build_portable.py --update

# 从已校验的安装导出不含用户数据的 zip
python -B tools/build_portable.py --export-clean dist/MabiAgent-clean.zip
```

打包脚本会复制运行资源和许可文件、执行离线自检、生成带 SHA256 的 `release-manifest.json`。发布包里不能带调试队列、账号记录或未处理的消费记录。

程序版本源为 `dotnet/Directory.Build.props`；窗口从程序集读取版本，打包说明读取同一版本字段，README 中的当前版本需同步更新。0.5.0 之后按补丁位递增（0.5.1、0.5.2……）。发布前至少完成：编译、上面列出的测试、UI 自检、清单校验和空数据目录启动检查。

## 磁盘占用

写入数据目录的内容必须有上限，新增写入时请沿用下面的做法：

| 内容 | 位置 | 上限 |
| --- | --- | --- |
| 运行事件 | `runtime-events.jsonl` | `RuntimeDiagnostics` 超限后轮换为 `.previous` |
| 进度与故障日志 | `runtime-progress.log`、`runtime-faults.log` | 超过 256 KiB 后只留最后 100 行 |
| 任务链版本 | `task-chains/chains/*/revisions` | 保存时保留最新 20 个（`StorageCleanup.PruneRevisions`） |
| 已删除任务链 | `task-chains/deleted` | 写入删除时间，30 天后在启动时清除 |
| 锚点修改历史 | `anchor-debug/versions.json` | 保留预设版本和最近 50 条 |
| 缩略图内存缓存 | `AnchorImageCache` | 256 张 |

启动时 `MainViewModel.AttachAutomation` 会在后台调用 `EditorRepository.CleanupStorage()`，清理结果写进进度日志。任务链的图片资源（`task-chains/assets`）按内容哈希去重，目前不自动删除，因为判断“无人引用”需要扫描全部任务链和锚点，误删的代价比占用空间大。

## 提交代码

- 只提交源码、通用配置、最小化的测试数据和文档。
- 不要提交个人数据、日志、现场截图、抓包文件、发布产物和本地工具配置。`.gitignore` 已经排除了常见的情况，提交前仍请自己检查一遍。
- `.gitignore` 不能清除已经进入历史的文件。一旦误提交了个人数据，需要重写历史，而不是再提交一次删除。
- 第三方依赖按各自许可使用，不要把依赖的许可当成本项目的许可。OCR 模型的来源和哈希记录在 `models/ocr-model.source.json`。

首次须知复用 NpcapPacketSource 的原生库加载与网卡枚举做只读依赖探测，不打开抓包句柄、不请求提权。仅不可用时创建安装说明及官网下载按钮，浏览器只在点击时打开。须知确认记录仍兼容已有 version=1；确认后不重复探测或弹窗。
