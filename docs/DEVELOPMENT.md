# 开发指南

## 架构

- `dotnet/MabiAgent.Core`：库存、队列与规划基础类型。
- `dotnet/MabiAgent.Workflows`：共享调度、生产/采集/钓鱼运行器、持久消费意图。
- `dotnet/MabiAgent.Windows`：画面、稳定锚点、后台输入、公共脚本与最小守卫。
- `dotnet/MabiAgent.Capture`：被动抓包与按账号、角色、容器隔离的库存。
- `dotnet/MabiAgent.Desktop`：Avalonia 界面及工作台；使用 Semi.Avalonia 与 Ursa.Avalonia。
- `config`、`assets/templates`：运行目录、脚本及识别资源；`models`：OCR 模型和来源许可。
- `tools/build_portable.py`、`tools/native_portable.py`：构建、原位更新和按安装清单导出。

所有配方和新增发布链使用共享执行器。任务链发布由 PublishedCatalogCompiler 编译，禁止为某个配方另建一套调度。普通采集与钓鱼入口区分。采集模板默认展示差异节点，高级编辑才展开公共节点。

## 运行约定

库存数量只取抓包，未知不能当零；屏幕负责识别页面、稳定身份和动作结果。后台输入统一经过会话、输入工厂和最小守卫，不激活或移动游戏窗口。仅声明的等待节点识别图文；动态收竿图标及钓鱼进度条为专用例外。

消费先保存意图，已发送而结果未知不可重放。局部失败隔离相关任务；手动暂停必须显式继续。删除任务不应被状态阻止或自动恢复；停止全部清除执行账，但不回滚游戏。

加工槽在领取后会前移，同设备串行、不同设备并行；到场以设备页自动打开判断。离场领取使用设备级最短间隔，避免短时加工反复导航。生活 VIP 只在共享 ScreenProductionPort 观察帧提供有效时长；目录保存标准时长，ProductionRunner 在发送委托前固定到期值，重启或切换设置不重算旧消费。维护与新建页共用 production.life_vip，缺省 false。

贩售候选限当前有效采集目标及明确指定材料，留存为配置下限与队列需要的较大值；背包无解暂停，不用存仓兜底。仓库先于采购，自动采购白名单仅空瓶、纸。角色槽位1～6与抓包身份按账号关联，场景中未知槽位不阻止普通流程。

## 构建与检查

使用 Windows x64、.NET 10 SDK；Python 3 仅用于打包工具。NuGet 版本与锁文件在项目内固定。

```powershell
dotnet restore dotnet/MabiAgent.Desktop
dotnet build dotnet/MabiAgent.Desktop --no-restore
dotnet test dotnet/MabiAgent.Tests
dotnet test dotnet/MabiAgent.Workflows.Tests
dotnet test dotnet/MabiAgent.Editor.Tests
python -B -m unittest discover -s tests -p test_native_portable.py
```

Windows 输入测试需交互桌面，不能与 UI 自检并行，以免焦点影响结果。涉及真实游戏的测试应使用独立测试角色与隔离数据目录；不可把历史图、模拟或离线测试称为当前实机通过。仓库不分发包含个人角色信息的现场截图、抓包及本机诊断夹具；依赖它们的现场回归需要开发者在本地取得对应授权数据。Core、Workflows 以及无现场夹具的测试可直接运行。

```powershell
python -B tools/build_portable.py --output dist/native
# 本机原位更新，保留 data
python -B tools/build_portable.py --update
# 从已检查安装清单导出，排除用户文件
python -B tools/build_portable.py --export-clean 发布包/MabiAgent-NET验收版/MabiAgent-干净交付.zip
```

构建包含离线交互与原生依赖自检。运行 `MabiAgent.Desktop.exe --self-check --interaction-check --native-check --root <源码根目录> --state-dir <隔离目录> --report <报告路径>` 可单独检查。禁止将调试队列、账号记录或未知消费放入发布包。

## 扩展与维护

新增地点优先复制相似模板、设置唯一运行键、补稳定锚点，再通过编译校验与聚焦流程验证。新增配方保留标准加工时长，不把 VIP 时长写入目录。修改通用行为需验证新配方和已有链都经过同一入口，删除被替代实现，避免两套执行路径。

版本来源为 `dotnet/Directory.Build.props`，窗口标题和打包说明同步更新。0.5.0 后按补丁位递增，例如0.5.1。发布前构建、必要回归、独立 UI 自检、清单校验及空数据启动检查；本机更新需验证既有用户文件未变。

## 源码仓库边界

提交源码、通用配置、最小测试和面向使用者/开发者的说明。数据、日志、现场截图、抓包、发布产物、本地进度管理、对话、自动化助手指引均留在本地。`.gitignore` 不能清除已有历史；首次上传应从审核过的文件集建立独立根提交，后续沿该干净分支更新，禁止合并含本地记录的旧历史。

暂不授予额外开源许可。第三方依赖遵循各自许可；OCR 来源与哈希见 `models/ocr-model.source.json`，许可见相邻 LICENSE 文件。不要将依赖许可证误用为整个项目的授权。
