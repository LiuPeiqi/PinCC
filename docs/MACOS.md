# PinCC macOS 版制作计划

本文是 macOS 版的完整制作计划，包括技术路线、架构、每个模块在 Mac 上的做法、两端怎样保持一致、分阶段安排、风险和待决定事项。

计划依据 2026-09 做的两件事：
- 对全部源码逐个文件梳理，区分哪些能共用、哪些要按平台重写；
- 对 .NET for macOS、AppKit 和 macOS 平台规范的查证（见附录 C）。

另外，三份按不同侧重独立写出的方案经过评分合并，成为本文。

---

## 0. 摘要

- **目标**：Mac 版功能与 Windows 版相同。两端的**内容和布局统一**；**风格、操作习惯和运行效率**各自符合平台要求。
- **路线**：两端共用一份 C# 逻辑，各自用原生界面。
  - 纯逻辑放进顶层的 `shared/`：今天的 `src/Core`，加上从 `Desk.cs`、`CardWindow` 里抽出的、不涉及界面的逻辑。
  - `shared/` 以源码链接的方式编进三个项目：
    - `src/`：现有的 WPF 程序，路径、exe 和行为都不变；
    - `mac/`：新的 net10.0-macos 程序，用 C# 直接调用 AppKit（NSPanel、NSTextView 等）；
    - `tests/`：改成普通的 net10.0，在三台机器上都跑，同时保证 `shared/` 里混不进任何界面类型。
- **怎样保证一致**：同一份代码，同一套坐标约定，事先录下的基准输出（golden），两端运行同一组场景并逐项比对，以及构建时的守卫检查。
- **工作量**：约 48 个工作日，分 8 个阶段，每个阶段都有能自动验证的完成标准。
- **最大的两个风险**：
  - 中文输入法在“不激活程序的面板”里的边角问题。P0 先实测，失败有备用方案 B。
  - 抽取逻辑时 Windows 版出问题。先录基准，之后每改一步都逐项比对。

---

## 1. 目标与约束

### 1.1 什么统一，什么各自实现

| 两端统一（同一份代码或同一份数据） | 各自实现（按平台习惯） |
|---|---|
| 卡片显示什么：各部分的顺序、标题、摘要、状态行、按钮、文案 | 字体、图标、圆角、阴影、深色模式、材质效果 |
| 卡片放在哪里：平铺、避让、胶囊、隐藏、+N、推开重叠卡片 | 窗口类型（WPF 分层窗口 / NSPanel）、窗口层级、桌面空间 |
| 行为规则：唯一修改入口、撤销分组、提醒只响一次、编辑 / 撤销条 / 临时展开的状态切换 | 快捷键（Ctrl ↔ ⌘）、菜单写法、设置窗口的形式、开机或登录启动的方式 |
| 数据：同一数据库结构（user_version 1）、同一 WAL 文件格式 | 数据目录、日志目录 |
| CLI：同一组命令、JSON 字段、`--md` 输出、退出码 0–4 | 命令名（`PinCC.exe` / `pincc`）、安装方式、帮助里的 shell 提示 |
| LLM：工具表、提示词、隐私守卫、摘要过时规则 | 工具查找方式（注册表 PATH + WSL / 登录 shell 环境）、子进程清理方式 |
| 抓取：HTTP、编码识别、正文提取、Office 文本 | 无头渲染（Edge / WebKit） |
| 防抖和批处理规则 | 定时器（Windows 每秒一次；Mac 到点精确触发，加一个允许误差的同步节拍） |

这里的“内容统一”指两端的功能和显示方式一致，**不包括**两台电脑之间同步卡片数据（见 §15 第 9 条）。

### 1.2 硬约束

- **Windows 版每天在用，不能出问题。** `src/PinCC.csproj` 的路径、程序集名、单文件发布方式和 `build.cmd` 的输出都不变；为 Win32 调校过的代码一行都不搬。
- **不移动 `src/`，也不修改 `.probe/`。** `.probe/` 里的脚本写死了 `src/bin/Debug/net10.0-windows/PinCC.exe`。
- **自动界面检查只在屏幕外渲染**，用 `PINCC_UI_SNAPSHOT` 或新增的 `PINCC_SCENARIO`。不在桌面上弹窗，也不碰正在运行的实例。发布前先把正在运行的 `dist\standalone\PinCC.exe` 改名为 `.old`。
- **保持零 NuGet 依赖。** 项目里不写任何 PackageReference。SDK 隐式还原的官方包不算在内，包括自包含发布用的运行时包、NativeAOT 的 ILCompiler 包，以及 `dotnet workload install macos` 下载的工作负载包；现在的 standalone 版也是这样。
- **开发环境**：Windows（WSL + Windows 版 dotnet，SDK 10.0.401）。net10.0-macos 只能在 Mac 上编译。现有一台 Apple 芯片 Mac 和一台 Intel Mac。

---

## 2. 技术路线比较

| 路线 | 两端一致 | Mac 原生程度 | 维护成本 | 能否在本机验证 | 结论 |
|---|---|---|---|---|---|
| **共享 C#：Windows 用 WPF，Mac 用 .NET for macOS 调用 AppKit** | 高，逻辑只有一份 | 高，用的就是 AppKit 对象 | 一种语言，两套视图 | 共享层可以；Mac 视图要在 Mac 上 | **采用** |
| Avalonia（只用于 Mac，或两端都用） | 中 | 中低（见下方说明） | 两端都用会丢掉调校好的 WPF/Win32 | 可以在 Windows 上跑 | 否 |
| Swift/AppKit 界面 + NativeAOT 编出的 C# 动态库 | 高 | 高，但用的是同样的 AppKit 对象，并不更原生 | 两种语言，外加大量 C 接口；NativeAOT 不能从 Windows 交叉编译到 macOS；没找到已上线的产品 | 否 | 作为退路保留 |
| 全部用 Swift 重写，靠跨语言测试样例对齐 | 低：约 6000 行细致逻辑要写两遍（撤销、时间解析、Markdown、抓取隐私、LLM 工具表、桌面逻辑） | 最高 | 最高，而且两端会越走越远 | 否 | 否 |
| MAUI / Mac Catalyst、Electron、Tauri | — | 低：做不出不激活的浮动面板，也控制不了窗口层级和桌面空间 | 每个窗口带一份很重的运行时；Windows 版也得重写 | — | 否 |

Avalonia 被否决的具体原因：
- 没有不激活程序的面板（NSPanel nonactivatingPanel）；
- 没有 collectionBehavior，窗口层级只有 Topmost 一种；
- Mac 上拼音输入卡死的问题 #15077 没有修复，Windows 上未激活窗口的输入法问题 #22273 也还开着；
- 菜单和设置窗口都不是原生的。

三份独立方案（原生优先、单一事实来源优先、交付风险优先）按 6 项打分，每项 10 分，得分分别为 46、45、47。最终方案：
- 以得分最高的“交付风险优先”方案的源码链接结构为骨架；
- 采用“原生优先”方案的 AppKit 细节；
- 采用“单一事实来源”方案的防漂移措施，但逐步加入，不一次性重写 WPF 视图。

---

## 3. 总体架构

### 3.1 目录结构

```text
PinCC/
  build.cmd                 基本不变：仍是 dotnet run --project tests，然后两次 dotnet publish src
  global.json               新增：SDK 10.0.401，"rollForward": "latestFeature"（Windows 升级 SDK 后 build.cmd 仍能用）；
                            Mac 的 macos workload 版本由 build-mac.sh 安装固定的 workload set 锁定
  shared/                   不放在 src/ 里（避免被 src/PinCC.csproj 的默认通配重复编译）
    PinCC.Shared.props  Version.props
    core/   Db.cs Store.cs Card.cs When.cs Markdown.cs Cli.cs Fetch.cs Llm.cs Proc.cs   （从 src/Core 移来）
            Platform/ AppPaths.cs Sqlite.cs PathSyntax.cs CliPlatform.cs LoginEnv.cs
                      IPageRenderer.cs IToolHost.cs IChildReaper.cs
                      ToolHost.Windows.cs ToolHost.Wsl.cs Reaper.Windows.cs EdgeRenderer.Windows.cs
                      ToolHost.Mac.cs Reaper.Unix.cs MacPaths.cs
    shell/  Geometry.cs Layout.cs LayoutPass.cs Metrics.cs Ports.cs Diagnostics.cs
            DeskCore.cs DeskCore.Tick.cs DeskCore.Enrich.cs DeskCore.Menus.cs
            Presenters/ CardScene.cs CardPresenter.cs EditSession.cs EditorOps.cs ResizeGeometry.cs MdRender.cs
                        HubModel.cs PickerModels.cs LibraryModel.cs SettingsSchema.cs RecorderRules.cs ClipIngest.cs
            Data/ Palette.cs Strings.cs PlatformText.cs Commands.cs KeyBindings.cs Gesture.cs IconMap.cs
                  HotkeyDefaults.cs KnownConflicts.cs StyleTokens.cs MenuSpec.cs
  src/                      Windows 程序，路径不变（.probe/*.sh 仍能找到 src/bin/Debug/net10.0-windows/PinCC.exe）
    PinCC.csproj Program.cs WinAssembly.cs app.manifest pincc.ico
    UI/ App.cs CardWindow.xaml(.cs) MarkdownView.cs HubWindow.cs Flyout.cs Toast.cs ManageWindow.cs AuxWindow.cs
        Theme.xaml PaletteBrushes.cs MenuRenderer.cs Native.cs Hotkeys.cs ClipData.cs
        Win/ WinPlatform.cs WinScreens.cs WinWindowHost.cs WinScheduler.cs WinSystemEvents.cs WinFullscreen.cs
             WinClipboard.cs WinShell.cs WinFocus.cs WinImages.cs WinSnapshot.cs WinInstanceGate.cs
  mac/                      只在 Apple 芯片 Mac 上编译
    PinCC.Mac.csproj Info.plist Entitlements.plist Entitlements.Dev.plist Assets/AppIcon
    Program.cs AppDelegate.cs MacPlatform.cs
    App/ MainMenu.cs StatusItem.cs MacInstanceGate.cs
    Desk/ MacScreens.cs MacWindowHost.cs MacScheduler.cs MacSystemEvents.cs MacFullscreen.cs
    Views/ CardPanel.cs CardContentView.cs CardEditor.cs MdAttributed.cs HubPanel.cs ToastPanel.cs Popovers.cs
           MenuRenderer.cs SettingsWindow.cs LibraryWindow.cs HotkeyRecorderView.cs
    Services/ MacHotkeys.cs MacClipboard.cs MacShell.cs CliInstaller.cs MacFocus.cs MacImages.cs MacSnapshot.cs WebKitRenderer.cs
    Resources/bin/pincc     命令行垫片脚本
    build-mac.sh            test | app | verify | release
  cli/PinCC.Cli.csproj      net10.0 控制台，只链接 shared/core。P1 起用它在 Mac 上提供 CLI；
                            P3 以后由 .app 里的同一个二进制提供，是否保留并改用 NativeAOT 按 P0 的测量决定
  tests/
    PinCC.Tests.csproj      net10.0，去掉 UseWPF、manifest 和 Native.cs；导入 shared props
    Program.cs FetchTests.cs DeskTests.cs GeometryTests.cs GoldenTests.cs TableTests.cs GuardTests.cs ParityTool.cs
    golden/ cli/ layout/ scenes/ md/      fixtures/win-created.db      scenarios/*.json
  tools/mac.sh              从 WSL 推代码到指定的 Mac → ssh 运行 build-mac.sh → 取回结果
  artifacts/                基准和 Mac 结果（P1 加进 .gitignore；不用 .probe/）
  docs/DESIGN.md（增加“平台适配”一节）  docs/MACOS.md（本文）  docs/mac-port.md（P0 实测记录）
  README.md（增加 macOS 一节，“目录”一节的 src/Core/ 改为 shared/）
  .probe/                   不动
```

### 3.2 为什么用源码链接，而不是独立的 Core 程序集

- Windows exe 仍是一个程序集，所以：
  - 内部可见性不变（`ClipData.cs:85` 调用 internal 的 `Fetch.ImageExt`）；
  - `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` 的作用范围不变；
  - 单文件发布的组成不变。
- `shared/` 放在 `src/` 外面。SDK 风格项目默认递归包含所有子目录里的 `*.cs`，放进 `src/` 会被 `src/PinCC.csproj` 重复编译。
- 等 golden 把行为固定下来以后，再把 `shared/` 改成真正的类库，只是机械工作。

```xml
<!-- shared/PinCC.Shared.props -->
<Project>
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)core/**/*.cs" LinkBase="shared/core" />
    <Compile Include="$(MSBuildThisFileDirectory)shell/**/*.cs" LinkBase="shared/shell" />
  </ItemGroup>
  <Import Project="$(MSBuildThisFileDirectory)Version.props" />
</Project>
```

### 3.3 平台差异只允许放在三个地方

1. **`shared/core/Platform/` 下的文件**：包括 `*.Windows.cs`、`*.Wsl.cs`、`*.Mac.cs`、`*.Unix.cs`，以及 AppPaths、Sqlite、PathSyntax 这类按系统分支的文件。
   - 它们只用 BCL 和对系统库的 P/Invoke（Windows 端是 kernel32 和注册表，Mac 端是 libc），不引用 WPF 或 AppKit 类型。
   - DllImport 到调用时才绑定，Registry 也在 Microsoft.NETCore.App 里，所以这些文件在所有平台上都能编译。
   - 运行时用 `OperatingSystem.IsWindows()` / `IsMacOS()` 选择，这些守卫同时消除 CA1416 警告。
   - 这样 Mac 的命令拼装、登录环境解析和路径规则在 Windows 上也能跑单元测试。
2. **`shell/Ports.cs` 里的接口**：窗口、屏幕、定时、系统事件、剪贴板等。
3. **数据表里的平台行**：KeyBindings、PlatformText、IconMap、HotkeyDefaults、KnownConflicts、StyleTokens。

`shared/` 里禁止出现 `#if`，由测试守卫检查。

### 3.4 线程模型

DeskCore 的代码全部在主线程运行，await 之后也回到主线程。WPF 的 Dispatcher 和 AppKit 主线程的 SynchronizationContext 都能保证这一点；Mac 端在 P0 验证。

---

## 4. 共享层：core（今天的 src/Core）

| 文件 | 改动 | 对 Windows 的影响 |
|---|---|---|
| `Db.cs` | 库名改成逻辑名 `"sqlite3"`。`Platform/Sqlite.cs` 用 `[ModuleInitializer]` 调用 `NativeLibrary.SetDllImportResolver`：Windows 加载 System32 里的 winsqlite3，macOS 加载 `/usr/lib/libsqlite3.dylib`，其他名字返回 `IntPtr.Zero`，不影响 user32、Carbon 等的默认解析。每个程序集只能注册一次解析器（第二次调用会抛 InvalidOperationException），而链接源码后 exe 程序集里的全部 P/Invoke 共用它，所以库名映射只能写在这一处，由守卫测试检查。UTF-16 的 `*16` 接口、WAL、`data_version` 两端相同；StdCall 在 x64/arm64 上被忽略；macOS 14 及以上自带 SQLite 3.43+，支持 VACUUM INTO、upsert 和 pragma_table_info。不打包私有 SQLite。 | 不变 |
| `Store.cs` | `DefaultHome` 改为 `AppPaths.DataHome`，依次取 `PINCC_HOME`、`%LOCALAPPDATA%\PinCC`、`~/Library/Application Support/PinCC`，**绝不返回空字符串**（否则数据库会建在当前目录下）。另外提供 `LogDir`（Mac 上是 `~/Library/Logs/PinCC`）和 `CacheDir`（`~/Library/Caches/PinCC`）。 | 不变 |
| `Card.cs` | 文件卡片判定改走 `PathSyntax.IsAbsolute(line, os)`：Windows 仍只认 `X:\` 和 `\\server\`（`Card.cs:224`），Mac 认 `/…` 和 `~/`。标题回退同时按 `\` 和 `/` 两种分隔符切分。 | 不变。Windows 上以 `/` 开头的笔记不能因此变成文件卡片 |
| `When.cs`、`Markdown.cs` | 原样共享 | 不变 |
| `Cli.cs` | `Program.cs:52-65` 移成 `Cli.Main(args, TextWriter, Stream stdin)`。`Cli.IsCliInvocation(args, os)`：Windows 上保持现状，除 `--startup` 外带任何参数都走 CLI（未知命令照旧打印用法并返回 1，`/?` 仍是帮助）；macOS 上同样规则，但另外排除 AppKit 和 Xcode 会注入的 `-NS*`、`-Apple*`、`-psn_*`。`Cli.Help(CliPlatform)`：正文和错误提示里写死的命令名 `PinCC`（Cli.cs:68、90、100、112、197、239、318-341）统一换成 `CliPlatform.Command`；数据目录、GUI 程序说明和 shell 提示（Cli.cs:342-348）放进平台段。`Cli.Help` 现在是 `const string`，`ManageWindow.cs:205` 的“复制 CLI 说明”要改为 `Cli.Help(CliPlatform.Windows)`。另有两处有意的输出修正（§4.1）。 | 参数路由和帮助内容不变 |
| `Fetch.cs` | 594 行中约 555 行原样共享。只有 `Rendered()` 里查找和启动浏览器的部分（约 L519-553）与平台有关，改为注入 `IPageRenderer.DumpDomAsync(uri, timeout, ct)`；外层的协议检查、同时只跑一个、长度检查和 `FromHtml` 保留在共享层。另加三个小钩子：按平台区分的 User-Agent 常量；`IsPackage`，让 Mac 上的 `.app`、`.rtfd`、iWork 文档包算作文件而不是文件夹；可选的平台文本提取（PDFKit / `textutil`，放到后期）。 | 原来的代码原样搬进 `EdgeRenderer.Windows.cs` |
| `Llm.cs` | 共享：工具表、参数数组、模型名校验、提示词与内容中和、Ask 流程、Resolve/Save（`ManageWindow.cs:267` 写 `llm.env` 的部分移进共享的设置模型）。`BuildCommand` 交给 `IToolHost { Which, BaseEnv, Wrap, WrapCustom, Scan, Label }`，有三个实现：`ToolHost.Windows`（注册表 PATH、.exe/.cmd/.bat、cmd.exe 包装和含 `" % !` 时拒绝）、`ToolHost.Wsl`、`ToolHost.Mac`。登录环境的标记解析和白名单共享。Mac 上 `llm.where` 默认为 `native`。顺手修正 `Llm.cs:197` 为 `IsWsl && cwd.StartsWith('/')`，否则 Mac 上的绝对路径会跳过“目录是否存在”的检查。 | 代码原样搬移 |
| `Proc.cs` | 共享：Run、Sink、stdin 写入、期限和解码。Job 对象（L96-135）换成 `IChildReaper { Adopt, KillTree, KillAll }`：Windows 继续用现有的 Job；Unix 第一版记录子进程，超时调用 `Kill(entireProcessTree: true)`，程序退出或收到 SIGTERM/SIGHUP 时 KillAll。只有孤儿进程测试失败，才改用第二版：`posix_spawn` + `POSIX_SPAWN_SETPGROUP` + `killpg`，下次启动时清扫残留。macOS 没有“父进程死了自动结束子进程”的内核机制。 | 不变 |

### 4.1 两处有意改变 Windows 输出的修正

1. **日期格式统一用 `CultureInfo.InvariantCulture`。**
   - `Cli.cs` 第 52、107、261、265、266 行含 `:` 的自定义格式会用当前区域的时间分隔符。CLI 的区域取决于调用它的终端或 agent 的 LANG/LC_ALL（没设置时 .NET 退回 ICU 默认），Windows 上还受用户自定义的区域格式影响，所以两端、甚至同一端的不同调用方，输出都可能不同。
   - `Store.cs:291` 的备份文件名、`backup.last` 和每日维护用的日期戳（Desk.cs:148、983，以后在 DeskCore 里）也改用 InvariantCulture。在非公历区域（例如泰国佛历），`yyyy` 会得到不同的年份。对中文区域的输出没有影响。
2. **`list --md` 和 `show` 的图片行写成 `![](<路径>)`。** Mac 的路径含 “Application Support”，中间有空格，不加尖括号的话 Markdown 解析会断开。Windows 上路径含空格时（例如 `PINCC_HOME` 指向含空格的目录）现在也会断。

这两处各自单独提交，并同时更新 golden。是否也在 Windows 上生效，需要你确认（§15 第 7 条）。

---

## 5. 共享层：shell（不含界面的“桌面”）

### 5.1 Geometry、Layout、Metrics

- **`Geometry.cs`**：
  - `Px`，从 `Native.cs:7-25` 移出。测试项目目前只为了它才编译整个 `Native.cs`。
  - `Screen(Id, AltId, Bounds, Work, Scale, PixelScale, Primary)`。
    - `AltId` 取代今天的 `Device` 字段。Windows 上它是 GDI 设备名，全屏判断（Desk.cs:255）要用它；按 Id 找不到屏幕时也用它匹配数据库里存成 `\\.\DISPLAYn` 的旧 `mon` 值（Native.cs:57）。
    - Mac 上 `AltId` 是 CGDirectDisplayID 的字符串。
    - Windows 上 `PixelScale = Scale`。
  - `Size2`、`Corner`、`Slot`。
  - 坐标翻转函数 `CocoaToTopLeft` / `TopLeftToCocoa`，以及向内取整。
- **`Layout.cs`**：`Tile` 和 `PushOut` 原样保留，再并入这些纯函数：
  - `Trim`（Desk.cs:648-662）；
  - `Spot`（Desk.cs:1409-1422，提示条放在哪里）；
  - `HubRect`（HubWindow.cs:152-159）。+ 按钮连同速拨区域（40 ×（40+3×38））都是平铺的障碍物，两端必须完全一致。
  - `ScreenOf(screens, rect)`：由 `Native.cs:67-71` 的 `Screens.Of` 改写成纯函数。今天的实现不纯：它自己枚举显示器，而且没有重叠时调用 Win32 的 MonitorFromPoint。改写后先取重叠面积最大的屏幕，都不重叠时取离矩形中心最近的屏幕，并用 golden 核对与 MONITOR_DEFAULTTONEAREST 的结果一致。`Desk.cs:263` 那个依赖窗口句柄的 `ScreenOf(CardWindow)` 留在 DeskCore，改为基于卡片状态。
  - `StripRect`、`PeekRect`、`ZoomTarget`、`CenterIn`。
- **`Metrics.cs`**：所有尺寸常量只在这里定义一次。下表中“工作区”在 Windows 上指 rcWork，在 Mac 上指 visibleFrame。

| 常量 | 值 |
|---|---|
| 胶囊高度 | 36 |
| 缩略图 | 48（图片卡片的最小边也是 48） |
| 最小尺寸 | 160×64 |
| 底部行高 | 31（行 28 + 下边距 3） |
| 顶部内边距 | 10 |
| 内容区内边距 | 左右各 12、顶部 10，加 1 的边框；内容宽度 = W − 26 |
| 编辑器底部留白 | 10 |
| 自动高度取整余量 | +3 |
| 段落之间的间距 | 8 |
| 链接标题 | 行高 round(1.45×字号)，最多 2 行 |
| 摘要预算里的标题区高度 | 链接 72 / 文件 52 |
| + 按钮 / 速拨按钮 / 速拨步距 / 离工作区边的距离 | 40 / 32 / 38 / 16 |
| 工作区边距 | 16 |
| 新卡片 | 宽 260，高度自适应（H 为空）；读不到尺寸的图片卡片退回 260×200 |
| 行高 | round(1.5×字号) |
| 自动高度上限 | max(64, min(360, 工作区高度 × 0.4))，单位 DIP；自动高度、图片排版和摘要预算共用这一个值（Desk.cs:257-261） |

  有两项是用户设置，不是常量，Metrics 里只放默认值和范围，实际值作为 settings 传给 LayoutPass：
  - 卡片间距 G：`tile.gap`，默认 12，界面可选 8/12/18，读取时限制在 4–40（Desk.cs:73）；
  - 字号：`text.size`，默认 14，范围 12–20。

### 5.2 LayoutPass：两端唯一放置窗口的代码

`LayoutResult Compute(screens, cardStates, hubs, settings, force)` 由 Desk.cs:796-870 改成纯函数，返回：
- 移动列表；
- 哪些卡片显示为胶囊；
- 哪些卡片隐藏；
- 溢出数量；
- 是否因鼠标悬停而推迟布局；
- 显示集合，即 Desk.cs:882-892 的可见性规则：最小化时只显示到期、编辑中和撤销条中的卡片；溢出的卡片隐藏；未保存的新卡片只在编辑时显示。

只有“应用”这一步留在各平台：
- Windows 是 Desk.cs:872-880 的 DeferWindowPos 批量移动和跨 DPI 两步放置，再按显示集合执行显示或隐藏，重新出现的卡片提到前面；
- Mac 是一次 CATransaction，其余相同。

### 5.3 DeskCore

约 1250 行来自 Desk.cs，只依赖接口：
- 设置；Start、Flush、Shutdown、Maintenance；
- Change、Push、Gesture（唯一修改入口）和撤销栈；
- NewCard（未保存的卡片用负数临时 id）；
- CreateFrom / Ingest（输入统一为 ClipPayload）；
- 编辑提交规则；完成后的撤销条；
- 置顶、颜色、透明度、折叠、临时展开、缩放；OnUserMoved；
- Tile、CollapseAll、ToggleHidden、ShowAll、Cycle；
- `Tick(now)` 和 `NextWake()`；Poll / Reload；
- 提醒只响一次，以及全屏时推迟提醒声；
- RefreshFiles；Enrich、Summarize、Budget；
- 热键注册和冲突去重；
- CardMenu / HubMenu（输出 MenuSpec）；
- Step 执行器和 layout.json 写出。

因此修改规则和提醒时机两端完全相同。菜单的项目和顺序也相同，只有少数平台行由 MenuSpec 按平台给出：
- Windows 的“管理…”在 Mac 上是“卡片库… ⌘F”和“设置… ⌘,”两项；
- “退出”在 Mac 上是“退出 PinCC ⌘Q”；
- “快捷键冲突 · 去设置”在 Mac 上打开设置窗口的快捷键页。

这些平台行也纳入表完整性测试。

### 5.4 Presenters 和各界面的数据模型

- **`CardPresenter`**：输入卡片和 CardViewState，输出 `CardScene`。内容包括：
  - 尺寸和边框样式；
  - 按顺序排列的各部分及其全部文案；
  - 图片排版的计算；
  - 状态标签和操作按钮，包括到期时的“10分钟 / 1小时 / ○”一行，以及宽度 ≥200 时的规则；
  - 可见性、自动高度和溢出淡出的公式；
  - 透明度、角标、红框。

  来源为 CardWindow.xaml.cs:137-147、191-216、236-653。
- **`EditSession`**：一次编辑的状态，包括原文、是否有改动、提交还是丢弃，以及编辑中 CLI 写入时怎样合并（保留用户的文字，另起一个撤销组）。DeskCore 的编辑提交规则用它来实现。
- **`EditorOps`**：续列表、缩进、包裹、切换行首前缀、`[] ` 快捷输入（CardWindow.xaml.cs:692-702、762-855）。
- **`ResizeGeometry.HitBand` / `KeepAspect`**：缩放边带的判定和图片锁定宽高比（CardWindow.xaml.cs:1040-1076）。
- **`MdRender`**：把 MarkdownView 里的样式决定整理成与平台无关的块，每块带源码行号。包括：每级缩进 16、段间距（标题 10，列表接列表 2，其余 6）、标题字号、`•` 和 `◦` 圆点、引用条 2.5、代码块、任务方框 14 DIP 等。
- **`HubModel`**：
  - 悬停速拨的三项：平铺显示、全部折叠 / 展开、最小化全部 / 恢复显示，各自的图标、提示和状态；贴底部角时顺序反转（HubWindow.cs:161-187）；
  - 颜色点显示下一张卡的颜色；有到期卡片时显示提醒点；
  - 角标的优先级：热键问题 `!` > 溢出 `+N` > 隐藏数；
  - 提示文字的组成。
- **其他模型**：
  - `ReminderPickerModel` 和 `ColorPickerModel`（Flyout.cs:92-171）；
  - `LibraryModel`；
  - `SettingsSchema`，带能力标记 HasWsl、HasPathInstall、HasCliShim、HasMenuBarExtra、LoginItemOptIn；
  - `RecorderRules`；
  - `ClipIngest`，图片扩展名增加 .heic/.heif；
  - `MenuSpec`，图标用语义化的 IconId。

### 5.5 数据表

| 表 | 内容 |
|---|---|
| `Palette` | 9 种卡片颜色，每种含名称和 Back/Strip/Accent/Ink 四个颜色；自动轮换顺序 0,4,2,6,3,1,5（Card.cs:193）；正文色 `#1F2328`；界面色（Bg、Border、Hover、Muted `#6B6F76`、FieldBg）；到期红 `#D13438`；界面强调色 `#316CA5` |
| `Strings` | 全部中文文案 |
| `PlatformText` | 按键名取的平台用词，例如“开机启动 / 登录时打开”“打开所在位置 / 在访达中显示” |
| `Commands` + `KeyBindings.Windows` / `.Mac` | 命令枚举和两端的按键绑定 |
| `Gesture` | 与平台无关的组合键写法，按平台格式化，例如同一个 Ctrl+Alt+N 在 Windows 上显示为 `Ctrl+Alt+N`，在 Mac 上显示为 `⌃⌥N`。两端的默认热键是分别选的，来自 HotkeyDefaults（Windows `Ctrl+Alt+Shift+N`，Mac `⌃⌥⌘N`） |
| `IconMap` | IconId 到 Segoe 码位和 SF Symbol 名称的映射 |
| `HotkeyDefaults`、`KnownConflicts.Windows` / `.Mac` | 默认热键和已知冲突表 |
| `StyleTokens` | 只放与几何无关的属性：圆角曲线、阴影、动画 |

### 5.6 Ports（平台接口）

- **窗口与屏幕**：`IScreenProvider`、`IWindowHost`（CreateCard、CreateHub、ShowToast、ApplyFrames、ShowMenu、OpenPicker、OpenSettings、OpenLibrary、Quit）、`ICardView`、`IHubView`、`IToast`
- **时间与事件**：`IScheduler`（Every，可设误差；Debounce；At；Post）、`ISystemEvents`、`IFullscreenMonitor`、`IClock`
- **系统服务**：`IClipboard`（读和写）、`IShell`（Open、Reveal、MustRevealOnly）、`ISound`、`IAutoStart`（含 `LaunchedAtLogin`）、`IFocus`、`IImageInfo`、`IDragOut`、`IHotkeyService`、`ISnapshotRenderer`、`IInstanceGate`
- **汇总**：`IPlatform` 把以上接口集中成一个对象，`DeskCore(Store, IPlatform)`。Windows 的实现是 `WinPlatform`，Mac 的是 `MacPlatform`。

### 5.7 诊断

`Diagnostics.cs` 提供：
- `FixtureScreens`：用 `PINCC_SCREENS` 指定假显示器；
- `FrozenClock`：用 `PINCC_NOW` 固定当前时间；
- 场景加载：`PINCC_SCENARIO`；
- `PINCC_STEPS`；
- 统一格式的 layout.json：以工作区左上角为原点、y 向下的 DIP 坐标。两端输出的结果可以直接比较。

现有的诊断模式怎么处理：

| 模式 | Windows | Mac | 能否用于自动检查 |
|---|---|---|---|
| `PINCC_SCENARIO`（新增） | 在拿 mutex 之前分流，不开窗口，输出统一格式的 layout.json、卡片 PNG 和 scene 哈希 | 相同 | 能 |
| `PINCC_UI_SNAPSHOT` | 保持现状：在拿 mutex 之前分流，渲染管理窗口、选择器和菜单的 PNG | 新增：用 cacheDisplay 渲染设置各页、卡片库、提醒和颜色选择器、+ 按钮与速拨、提示条。NSMenu 不能 cacheDisplay，所以菜单输出 MenuSpec 文本，与 Windows 逐项比对 | 能 |
| `PINCC_SNAPSHOT`（含 `PINCC_SNAPSHOT_DELAY`、`PINCC_STEPS`） | 保持原样，layout.json 仍是物理像素格式（README 已写明）。它会经过单实例 mutex，正在运行的实例旁边一启动就会通知它“显示全部”然后退出 | 不支持 | 不能 |

---

## 6. 坐标与布局统一

- **布局空间**：一个 y 向下、取整数的坐标空间。
  - Windows：物理像素，`Scale = dpi/96`，保持现状。
  - Mac：point，`Scale = 1`，`PixelScale = backingScaleFactor`。
- **Mac 坐标翻转**：`MacScreens` 统一翻转一次，`yDown = screens[0].frame.maxY − (y + h)`。Work 取 visibleFrame 向内取整，已经去掉菜单栏、Dock 和刘海区域。
- **放置卡片**：`frame.origin.x = vf.minX + x`，`frame.origin.y = vf.maxY − y − h`（单位 point）。
- **写进数据库的位置含义不变**，所以不用改数据库结构：
  - `mon` 是一个不透明的显示器 id：Windows 上是设备接口路径，Mac 上是 `CGDisplayCreateUUIDFromDisplayID` 转成的字符串；
  - `x/y/w/h` 是相对工作区左上角的 DIP 偏移。
  - 遇到不认识的 `mon`，卡片回到主屏参与平铺；为了显示而做的位置夹紧从不写回数据库。
- **唯一允许的差异**：自动高度卡片的文字高度。两端字体不同，行高、内边距、内容宽度和 Budget 公式又都共享，所以最多相差一行。
- **“1:1 像素”的换算**：Mac 上是像素数 ÷ PixelScale（backingScaleFactor）个 point。Windows 的 Work 是物理像素，Desk.cs:582 直接用它和图片像素比较本来就正确，输出不能变。共享代码里凡是“1:1 像素”的换算，一律写成 `Work × PixelScale ÷ Scale` 和 `像素 ÷ PixelScale`，这样两端都对。涉及的地方有：
  - ZoomActual（Desk.cs:582-583）；
  - NaturalSize（Desk.cs:348，新图片卡片的初始尺寸）；
  - 图片解码宽度（CardWindow.xaml.cs:234、389）。
- **Mac 特有的情况**：
  - Dock 可能在左、右或下方；
  - 自动隐藏的 Dock 仍会留出约 4 pt；
  - 刘海屏的菜单栏约 37 pt 高；
  - 台前调度的侧边条不算在 visibleFrame 里；
  - 屏幕 id 在显示器断开时可能取不到，要有退路；
  - “显示器具有单独的空间”关闭时，只有主屏有菜单栏，全屏应用也会占用所有显示器（P0 第 10 项实测）。

  Dock 和菜单栏自动隐藏不一定会发通知，所以每次节拍都重新检查 visibleFrame。

---

## 7. Windows 端改动：重新接线，不重写

- **`src/PinCC.csproj`**：路径、WinExe、net10.0-windows、UseWPF、程序集名、manifest、图标和单文件发布都不变。`src/Core` 已经移走，改为导入 `../shared/PinCC.Shared.props`。`DefaultDllImportSearchPaths(System32)` 移到 `src/WinAssembly.cs`。
- **`Program.cs`**：
  - 保留命名 Mutex 和 Event（作为 `WinInstanceGate`）；
  - `PINCC_UI_SNAPSHOT` 和新增的 `PINCC_SCENARIO` 都在拿 mutex 之前分流，紧接在 Program.cs:19-23 之后。所以场景运行既不拿 mutex，也不设置 `Local\PinCC.Show`；
  - 保留控制台附加和 UTF-16 输出；
  - 改为调用 `Cli.IsCliInvocation` 和 `Cli.Main`。
- **`App.cs`**：改为 `new DeskCore(store, WinPlatform)`。`PINCC_SCENARIO` 模式由 Program.cs 分流进来：不显示窗口，也不创建、不等待 `Local\PinCC.Show` 事件，只写出 layout.json 和各卡片的 PNG。不能等待这个事件的原因：它是 AutoReset 事件（App.cs:72-73），多一个等待者就会抢走正在运行实例的“显示全部”信号。
- **`CardWindow`**：实现 `ICardView`，用原有的 XAML 画出 `CardScene`。WndProc 全部保留：
  - WM_NCHITTEST（边带宽度改为从 `ResizeGeometry` 取）；
  - WM_NCLBUTTONDBLCLK（双击下边缘恢复自适应高度）；
  - WM_SIZING（图片锁定比例）；
  - WM_ENTER/EXITSIZEMOVE；
  - WM_ACTIVATE 配合 IsOwnedBy；
  - WM_DPICHANGED 补上 NOACTIVATE；
  - WS_EX_TOOLWINDOW 和 WindowFromPoint 悬停判定。

  编辑快捷键改为从 `KeyBindings.Windows` 查表，具体修改交给 `EditorOps`。
- **其余视图**：
  - `MarkdownView` 按 `MdRender` 的结果生成 TextBlock；
  - Hub、Flyout、Toast、ManageWindow、AuxWindow 和 Theme.xaml 仍是 WPF，只把数据来源换成共享模型；
  - `PaletteBrushes.cs` 把 RGBA 转成 Brush；
  - `MenuRenderer` 把 MenuSpec 画成带 Segoe 图标的 ContextMenu。
- **`src/UI/Win/` 里新增的薄适配器**：

| 适配器 | 包装的现有代码 |
|---|---|
| WinScreens | 现有的 Screens |
| WinWindowHost | 现有的 Desk.cs:872-892 |
| WinScheduler | DispatcherTimer（1 s / 100 ms / 600 ms）；`At()` 建在 1 s 节拍上（到点后的下一次节拍触发），所以 Windows 的提醒时机不变 |
| WinSystemEvents | MessageWindow、SessionEnding、Show 事件 |
| WinFullscreen | SHQueryUserNotificationState 加上前台窗口判断 |
| WinClipboard | ClipData，输出 ClipPayload |
| WinShell | ShellExecute、`explorer /select`、只定位不运行的扩展名表、PlaySound、HKCU Run、HKCU PATH |
| WinFocus、WinImages、WinSnapshot、WinInstanceGate | 对应的现有代码 |

  `Hotkeys.cs` 实现 `IHotkeyService`。

---

## 8. Mac 端实现要点

### 8.1 程序外壳与生命周期

- **`mac/PinCC.Mac.csproj`**：
  - net10.0-macos（.NET 11 正式版发布后改为 net11.0-macos）；
  - `SupportedOSPlatformVersion` 14.0；
  - `RuntimeIdentifiers` 为 osx-arm64;osx-x64，Release 生成通用二进制；
  - `InvariantGlobalization=false`，`AssemblyName` 为 PinCC，导入 Shared.props。
- **Info.plist**：

| 键 | 值与说明 |
|---|---|
| `CFBundleIdentifier` | 正式版 `com.<你>.pincc`，开发版 `com.<你>.pincc.dev` |
| `LSUIElement` | YES：没有 Dock 图标，也不出现在 ⌘Tab 里 |
| `CFBundleDevelopmentRegion` | zh-Hans |
| `LSMinimumSystemVersion` | 14.0 |
| `NSDesktopFolderUsageDescription`、`NSDocumentsFolderUsageDescription`、`NSDownloadsFolderUsageDescription` | 访问这三个文件夹时向用户说明用途 |

- **`Program.Main`**：
  1. 如果 `Cli.IsCliInvocation(args)` 为真，用 UTF-8（无 BOM）的 stdout 运行 `Cli.Main`，并在 `NSApplication.Init` 之前返回，所以不会闪出 Dock 图标。
  2. 如果设置了 `PINCC_SCENARIO` 或 `PINCC_UI_SNAPSHOT`，直接进入屏幕外渲染：不加 flock，不发送也不监听显示通知，不显示任何窗口，写完结果就退出。
  3. 否则由 `MacInstanceGate` 对 `DataHome/.instance` 加 flock。已有实例在运行时，发送显示通知后退出。
     - 通知名带上 bundle id（例如 `com.<你>.pincc.Show`），开发版和正式版互不唤醒。
     - 发送时用 `postNotificationName:object:userInfo:deliverImmediately:`，deliverImmediately 设为 YES（或者观察方注册时用 `suspensionBehavior: .deliverImmediately`）。原因是 AppKit 在程序不在前台时会暂停分布式通知的投递，而 PinCC 几乎总在后台。
  4. 以 accessory 激活策略启动程序。
- **AppDelegate**：
  - didFinishLaunching：DeskCore.Start。
  - applicationShouldHandleReopen（从访达、启动台或 Spotlight 再次打开）：ShowAll。如果菜单栏图标已被关掉或被系统隐藏，同时打开设置窗口（里面有“退出 PinCC”）。
  - applicationShouldTerminate 和 willPowerOff：Flush。
  - willTerminate：Shutdown，然后关闭 Store。
  - 不在 Info.plist 里声明 `NSSupportsSuddenTermination`，所以系统不会不经通知直接结束进程，注销和关机时一定会走 applicationShouldTerminate → Flush。
  - `ObjCRuntime.Runtime.MarshalManagedException` 和 AppDomain 的异常处理写入 `~/Library/Logs/PinCC/error.log`。
  - 设置窗口或卡片库打开期间，临时切换为 .regular 激活策略。
- **隐藏的主菜单**：agent 程序不显示菜单栏，但没有主菜单的话，文本框里的 ⌘C、⌘V、⌘Z 都不起作用。
  - App 菜单：关于 PinCC、设置… ⌘,、安装命令行工具…、退出 PinCC ⌘Q。
  - 编辑菜单：撤销 ⌘Z、重做 ⇧⌘Z、剪切、拷贝、粘贴、全选。
- **可选的菜单栏图标**（NSStatusItem，用 SF Symbol 模板图）：菜单内容是 HubMenu 加上设置和退出。macOS 26 允许用户在“系统设置 › 菜单栏”里关掉第三方图标，所以设置和退出同时保留在 + 按钮的右键菜单里；再次打开程序时，如果菜单栏图标不可见，也会打开设置窗口。

### 8.2 卡片面板（CardPanel）

- **创建一次，样式不再修改**：NSPanel 子类，创建时就设定 `[.borderless, .nonactivatingPanel]`，之后绝不改动。创建后再改这个标志，WindowServer 的状态不会跟着更新，结果是面板看起来有焦点，却打不了字。
- **焦点相关的三个属性**：
  - `canBecomeKey = true`：无边框窗口默认不能成为 key 窗口，不改的话编辑器收不到键盘输入；
  - `hidesOnDeactivate = false`：NSPanel 默认是 true，不改的话切到别的程序时所有卡片都会消失；
  - `becomesKeyOnlyIfNeeded = false`（保持默认）：单击卡片的任何位置，卡片就成为 key，但面板本身不激活程序，前一个程序仍在前台。这对应 Windows 上卡片被点击后激活（CardWindow 的 IsActive）。设成 true 的话，只有点到文本视图才会成为 key，Return、⌘⌫、⌘C、⌘V、⌘Z、⌘N、⌘F、⌘` 这些卡片快捷键就收不到，“选中”的强调色描边也不会出现。
  - 悬停不改变 key；开始编辑时同样调用 makeKey。
  - 卡片带 `.ignoresCycle`，系统自带的 ⌘` 窗口轮换不包含卡片，所以 ⌘` / ⇧⌘` 由 KeyBindings 在面板的 keyDown 里处理，被切换到的卡片调用 makeKey（对应 Desk.Cycle 里的 Activate）。
- **外观**：
  - `isOpaque = false`，`backgroundColor = .clear`；
  - `hasShadow = true`。阴影在点击区域之外，不影响布局矩形；每次形状变化调用 `invalidateShadow`。
  - `animationBehavior = .none`，`isExcludedFromWindowsMenu`。
- **层级与桌面空间**：
  - 生效置顶（包括到期）的卡片用 `.floating`，其余用 `.normal`；
  - `collectionBehavior = [.canJoinAllSpaces, .ignoresCycle]`，到期时加上 `.stationary`，这样“显示桌面”和调度中心不会把它推开；
  - **永远不加 `.fullScreenAuxiliary`**，这样全屏应用所在的空间里不会出现卡片。
- **移动窗口**：
  - 置前用 `orderFrontRegardless()`，不会激活程序；
  - 退到后面用 `order(.below, relativeTo:)`；
  - 放置用 `setFrame(_:display:false)`，同一次布局的所有窗口放在同一个关闭了动画的 CATransaction 里。
- **内容视图**：用 layer 绘制，8 pt 连续曲率圆角，1 px 描边。描边按 CardScene 的边框样式：到期是 2 px 红色，选中是强调色，普通是 ink 0.12。各部分按 CardScene 生成 NSStackView。
- **悬停和第一次点击**：
  - NSTrackingArea 选项 `[.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect]`。没有 `.activeAlways` 的话，PinCC 不在前台时悬停按钮不会出现，而不在前台正是常态。注意 `.activeAlways` 不支持 `.cursorUpdate`，cursorUpdate 不会被调用，所以光标在 mouseMoved 里设置（见下）。
  - `acceptsFirstMouse = true`，否则点 ○、📌 的第一下只会激活程序。
- **移动与缩放**：手写鼠标跟踪循环。
  - 移动阈值 3 pt。
  - 缩放边带由共享的 `ResizeGeometry` 决定（内侧 6 pt，角上 12 pt）。光标在 mouseMoved 里按边带调用 `NSCursor.set()`：边上用 resizeLeftRight / resizeUpDown，角上用斜向光标（macOS 15 及以上用 `NSCursor.frameResize(position:directions:)`，macOS 14 用程序自带的光标图片）。
  - PinCC 不在前台时，系统可能不允许后台程序改光标。这一点放进 P0 第 1 项验证；不通过就接受“后台时边带不显示缩放光标，但照样能拖动缩放”。
  - 图片卡片用共享的 `KeepAspect` 锁定比例。
  - 松开鼠标时只调用一次 `OnUserMoved`；双击下边带恢复自适应高度。
  - ⌘-拖动保留给系统的“移动后台窗口”。
- **动画和效果**：
  - CAGradientLayer 做溢出部分的淡出；
  - CABasicAnimation 做到期红框闪三下，“减少动态效果”打开时跳过；
  - 角标单独一个图层。
- **外观模式**：浅色卡片强制使用 `.aqua` 外观，墨炭强制 `.darkAqua`；“降低透明度”打开时透明度固定为 1。

### 8.3 编辑器与中文输入法

- **NSTextView 设置**：`isRichText = false`、`importsGraphics = false`、`allowsUndo = true`；关闭智能引号、智能破折号、文本替换、自动拼写纠正和数据检测。否则 `--` 会变成 `—`，直引号会变成弯引号，Markdown 被破坏。
- **按键处理**：Return、Tab、⇧Tab、Esc 在 `textView(_:doCommandBy:)` 里处理（对应 `insertNewline:`、`insertTab:`、`insertBacktab:`、`cancelOperation:`）。这个回调只在输入法提交之后触发，所以拼音选词时按回车不会误续列表。`[] ` 快捷改写用 `hasMarkedText()` 保护。
- **可撤销**：`EditorOps` 的修改依次调用 `shouldChangeText`、`replaceCharacters`、`didChangeText`，这样能用 ⌘Z 撤销。
- **粘贴**：覆盖 `paste(_:)` 处理图片和文件。插入点和选区颜色取卡片的强调色。
- **不绑定 ⌃+字母**：保留 NSTextView 自带的 Emacs 式按键（⌃A、⌃E、⌃K 等），所以卡片和编辑器都不绑定 ⌃+字母。
- **焦点方案 A（首选）**：卡片是能成为 key 的不激活面板，打字时前一个程序仍在前台。按 Esc 时，要把键盘还给原程序，同时卡片留在屏幕上。
  - AppKit 没有“只放弃 key”的公开接口：`resignKey()` 是状态变化后的回调，不能直接调用；Spotlight 类面板是靠关闭面板交还焦点的。
  - 具体做法放进 P0 第 2 项验证，例如在同一次 CATransaction 里先 orderOut 再 orderFrontRegardless，确认 Safari 等原程序重新收到键盘、卡片不闪。不通过就改用方案 B。
- **焦点方案 B（P0 拼音测试失败时启用）**：编辑时激活 PinCC；按 Esc 时调用 `NSApp.yieldActivation(to:)`，再激活之前的程序（macOS 14 的协作式激活）。
- 两种方案都放在 `IFocus` 接口后面，DeskCore 不用改。

### 8.4 Markdown 渲染（MdAttributed）

- 由 `MdRender` 生成 NSAttributedString。
  - 字体：系统字体（SF Pro，中文自动回退到苹方）和等宽系统字体（SF Mono），字号 13/14/16/18 与 Windows 相同。
  - 段落行高：`min/maxLineHeight = round(1.5×字号)`。
- 列表用 headIndent 加制表位；链接用 `.link` 属性；任务方框用 NSTextAttachment，按字符位置判断点击；引用条和代码块背景按 MdRender 的样式标记绘制。
- 双击时把光标放到对应位置：每段带 `pincc.line` 属性记录源码行号，再结合 `characterIndexForInsertion(at:)`。

### 8.5 + 按钮、提示条、弹出面板、菜单

- **HubPanel（+ 按钮）**：
  - 每个屏幕一个不激活面板，`canBecomeKey = false`，层级 `.floating`，`[.canJoinAllSpaces, .stationary, .ignoresCycle]`；
  - 手写拖动，松开时按 HubModel 吸附到最近的屏幕角；
  - **悬停速拨**：用 NSTrackingArea（`.activeAlways`）加上与 Windows 相同的进入 300 ms、离开 350 ms 延时，弹出“平铺显示 / 全部折叠·展开 / 最小化全部·恢复显示”三个按钮。项目、图标和提示来自 HubModel，贴底部角时顺序反转。面板尺寸按 HubRect，包含速拨区域。`acceptsFirstMouse = true`，PinCC 不在前台时第一次点击就生效；
  - 用 `registerForDraggedTypes` 接收拖入；
  - macOS 26 及以上用 NSGlassEffectView，更早的版本用 NSVisualEffectView。Liquid Glass 只用在控件层，卡片属于内容层，不用。
- **ToastPanel（提示条）**：
  - 不激活，`canBecomeKey = false`，材质背景，位置取共享的 `Layout.Spot`；
  - 显示 4 秒，悬停时暂停，点击关闭，与 Windows 相同。
  - 提示留在程序内，不用系统通知中心，保证两端布局一致。
- **提醒和颜色选择器**：先用 `NSPopover(.transient)`，它自己定位和翻转；“设定”是回车默认按钮。卡片失去 key 时，如果新的 key 窗口属于这个 popover，就不退出编辑（对应 Windows 的 IsOwnedBy）。
  - 还没验证的一点：PinCC 不在前台时，popover 里的输入框能否打字、能否用拼音，而且不激活程序。popover 的窗口由 AppKit 创建，不是 nonactivatingPanel，所以可能激活 PinCC，也可能拿不到 key。
  - 这一点加进 P0 第 2 项。不通过就改成自己的不激活 NSPanel 子窗口（addChildWindow，样式同卡片）。
- **菜单**：
  - MenuSpec 生成 NSMenu，通过 `menu(for:)` 提供，所以 ⌃点按和双指点按也能打开；
  - 用 SF Symbols 图标，快捷键显示来自 `KeyBindings.Mac`；
  - 单选项用 `state = .on` 打勾；
  - 透明度改成“透明度 ▸”子菜单（100/90/80/70/50，当前值打勾），不再在菜单项里嵌按钮。

### 8.6 设置窗口与卡片库

- **设置窗口（⌘,）**：
  - 用 NSTabViewController（toolbar 样式），分为通用、快捷键、AI 摘要、外观四页，全部由 `SettingsSchema` 生成；
  - 改动立即生效，⌘W 关闭，记住上次打开的页；
  - 不显示 WSL 和发行版这两行，改为显示捕获到的登录 shell（例如“/bin/zsh 登录环境”）；
  - 新增“命令行”一行，用来安装 `pincc` 命令。
- **卡片库窗口（⌘F）**：NSSearchField 搜索，带计数的 NSSegmentedControl 过滤，NSOutlineView 列表（历史可以展开）。Esc 清空搜索框（Mac 上 Esc 不关闭窗口）。
- 两个窗口打开期间都是 `.floating` 层级，居中显示在鼠标所在屏幕的 visibleFrame 上。

### 8.7 全局热键与录入

- **注册**：用 Carbon `RegisterEventHotKey`，带 `kEventHotKeyExclusive` 标志。组合已被别的程序独占时返回 -9878，界面显示“被占用”，相当于 Windows 的错误 1409。`InstallEventHandler(kEventHotKeyPressed)` 的回调用 `[UnmanagedCallersOnly]`。**不需要任何系统权限。**
- **冲突检查**：`CopySymbolicHotKeys`（系统快捷键）、自己的菜单，加上 Mac 已知冲突表。表中包括：⌘Space（Spotlight）、⌥⌘Space（访达搜索）、⌃Space（输入法）、⇧⌘3/4/5（截图）、⌃⌘A（微信/QQ）、⌥Space（Raycast/Alfred）等。
- **规则**：默认的和用户录入的热键都必须含 ⌘ 或 ⌃（macOS 15.0–15.1 拒绝只用 ⌥ 或 ⌥⇧ 的组合）。显示顺序为 ⌃⌥⇧⌘。
- **录入框**：
  - 做成一个不实现 NSTextInputClient 的第一响应者视图（所以不会经过输入法），加上本地事件监听；
  - 录入期间先注销自己的全部热键；
  - **不用 CGEventTap**，它会触发“输入监控”权限。

  能报告的冲突有两类：系统快捷键（查 CopySymbolicHotKeys），以及别的程序用 Carbon 注册的组合（注册时会失败）。用事件钩子实现的程序快捷键检测不到，只能靠已知表提示。
- **默认值**：“新建卡片”为 ⌃⌥⌘N（待确认，见 §15 第 2 条）。其余四个动作（用剪贴板新建、最小化 / 恢复、全部折叠 / 展开、平铺显示）默认不绑定，与 Windows 相同。

### 8.8 剪贴板、拖入、拖出

- **读取顺序**：
  1. 文件 URL；
  2. `public.url` 加 `public.url-name`（从 Safari 拖出的链接带标题）；
  3. `org.chromium.source-url`（Chrome/Edge 复制时带的来源网址）；
  4. `com.apple.webarchive` 里 WebMainResource 的 WebResourceURL（从 Safari 复制文字时的来源网址）。Firefox 在 Mac 上用哪种类型带来源网址还没查证，P5 实测；
  5. png / tiff / heic，统一转成 PNG 存为 `img/<hash>`；
  6. 纯文本。

  这对应 Windows 上“粘贴时记录来源网址”（CF_HTML 的 SourceURL）。
- **读取时机**：只在用户明确操作时才读剪贴板。macOS 15.4 加入了程序读取剪贴板时的提示和 detect 接口，到 macOS 26 仍默认关闭，macOS 27 未查证。“用剪贴板新建”（+ 按钮菜单和全局热键）不是 ⌘V 粘贴，可能被算作程序读取，所以先用 NSPasteboard 的 detect 接口判断内容类型，P5 在 macOS 27 上实测是否弹提示。
- **写入**：先 `clearContents()`。
  - 纯图片卡片写 PNG 数据加文件 URL；其余卡片写纯文本（正文 + 摘要），与 Windows 的 Desk.Copy 相同；
  - “复制链接 / 复制路径”和设置里的“复制 CLI 说明”写纯文本；
  - 编辑状态下的 ⌘C 仍交给 NSTextView。
- **截图流程**：⌃⇧⌘4 截到剪贴板，点 + 新建卡片，按 ⌘V。对应 Windows 上 Win+Shift+S 的流程。
- **拖入**：+ 按钮和卡片注册 `.fileURL`、`.URL`、`.string`、`.png`、`.tiff`、`.html`、`org.chromium.source-url` 这几种类型，与粘贴共用同一个 ClipPayload。
- **拖出图片**：用 ⌥-拖动，而不是照搬 Ctrl+拖动：Mac 上 ⌃点按等于右键，而 ⌥ 在拖动时本来就表示“拷贝”。拖动会话携带文件 URL，操作类型为 `.copy`。

### 8.9 打开文件、在访达中显示、提示音、登录项

- **打开**：`NSWorkspace.open(url)`。**在访达中显示**：`activateFileViewerSelecting`，菜单文字为“在访达中显示”。
- **只定位不运行**：`.app .command .sh .tool .terminal .workflow .pkg .mpkg .scpt`，以及没有扩展名的可执行文件。
- **提示音**：用 `AudioServicesCreateSystemSoundID` 载入 `/System/Library/Sounds/Glass.aiff`，再用 `AudioServicesPlayAlertSound` 播放，这样走“播放声音效果的设备”，音量跟随提醒音量；失败再用 `NSSound.beep()`。不需要权限。（直接用 `NSSound(named: "Glass").play()` 走的是主音量，不跟随提醒音量。）
- **登录项**：`SMAppService.mainApp.register()` / `unregister()`（macOS 13+，出现在“系统设置 › 通用 › 登录项”）。
  - 开关显示实时的 `.status`，因为用户可能在系统设置里关掉它；
  - 状态为 `requiresApproval` 时，提供跳转到 `openSystemSettingsLoginItems()` 的按钮；
  - Mac 的惯例是由用户选择开启：首次运行时的提示条上给一个按钮。开启后，系统自己会弹出“已添加后台项目”的通知。
  - **Windows `--startup` 的对应**：Windows 上 `--startup` 只有一个作用，开机启动时如果没有卡片，就不弹“点 + 新建卡片…”欢迎提示（Desk.cs:129）。SMAppService 启动程序时不带参数，所以 Mac 用 `IAutoStart.LaunchedAtLogin` 判断：看启动 Apple 事件里的 keyAELaunchedAsLogInItem，或者用开机时长近似，P0 第 9 项验证哪种可靠。判断不了时，欢迎提示只在首次运行时显示。

### 8.10 定时、提醒与省电

- **App Nap 的影响**：系统会降低后台程序定时器的频率，而 PinCC 通常就在后台。因此：
  - **提醒**：DeskCore 提供 `NextWake()`（最近一个提醒时间）。Mac 用严格的一次性 DispatchSourceTimer 对准这个时间；唤醒、系统时间或时区变化、日期变化、数据变化时重新设定。
  - **1 秒节拍只负责同步**（轮询 `data_version`、检查 visibleFrame）。它允许 0.1–0.2 s 的误差，并运行在 common 模式，这样菜单打开和拖动缩放期间也会触发。
  - 只在 LLM 运行期间持有 `beginActivity`。
  - P6 可选：用 DispatchSource 监视 `pincc.db-wal`，让 CLI 做的修改立即显示。
- **系统事件**：`didChangeScreenParametersNotification`、`NSSystemClockDidChange`、`NSSystemTimeZoneDidChange`、`NSCalendarDayChanged`。`didWakeNotification` **必须**在 `NSWorkspace.shared.notificationCenter` 上监听，默认的通知中心收不到。
- **目标**：30 张卡片时，空闲 CPU 约为 0%。

### 8.11 全屏与桌面空间

- Mac 的全屏应用在单独的桌面空间里。卡片不带 `.fullScreenAuxiliary`，自然不会出现在那里，Windows 上“退到全屏窗口后面”的效果在 Mac 上不用额外实现。
- **全屏检测只用于两件事**：隐藏 + 按钮，推迟提醒声。
  - 检测由事件触发（`activeSpaceDidChange`、`didActivateApplication`）；
  - 检查卡片的 `isOnActiveSpace`，并用 `CGWindowListCopyWindowInfo` 看其他进程的第 0 层窗口是否铺满整个屏幕。只读窗口边界，不读标题，所以不需要“屏幕录制”权限。
  - 系统没有公开接口能得知别的程序是否全屏或是否处于专注模式。判断错了，后果只是提醒声提前响。

### 8.12 图片

- **读取和解码**：
  - 用 `CGImageSourceCopyPropertiesAtIndex` 读像素尺寸，不需要解码，也支持 HEIC；
  - 用 `CGImageSourceCreateThumbnailAtIndex` 按档位解码：最大边为显示宽度 × backingScaleFactor，并用 `WithTransform` 应用 EXIF 方向；
  - 解码结果放在设了成本上限的 NSCache 里；
  - 窗口换到缩放比例不同的屏幕时（`viewDidChangeBackingProperties`）重新解码。
- **操作**：
  - 缩放：触控板捏合（每次捏合算一个撤销组）或滚轮；
  - 1:1：⌘0，或双指轻点两下；接三键鼠标时，中键（otherMouseDown，buttonNumber 2）同样恢复 1:1；
  - 双击：用“预览”打开。

### 8.13 AI 工具（ToolHost.Mac）

- **必须捕获登录 shell 的环境。** 从访达、Dock 或登录项启动的程序，PATH 只有 `/usr/bin:/bin:/usr/sbin:/sbin`，也读不到 `~/.zshrc` 和 `~/.zprofile` 里的设置。做法：
  1. 从 `getpwuid` 取用户的 shell（默认 /bin/zsh）；
  2. 执行 `<shell> -l -i -c 'printf 标记; /usr/bin/env'`，关闭 stdin，期限 15 秒；
  3. 用与 WSL 相同的标记解析器和白名单（PATH、NVM_BIN、代理等）提取，保存为 `llm.env`。

  启动时在后台做一次，点“检测”时再做一次。
- **启动顺序**：
  - 首次运行时的 DetectLlm、CatchUpSummaries，以及 Enrich 触发的自动摘要，都要先等登录环境捕获完成，或者先用上次保存的 `llm.env`。
  - 否则它们只拿到访达启动时的极简 PATH，找不到用 nvm 或 npm 安装的 claude、codex。而 `llm.detected` 一旦写入就不再自动检测（Desk.cs:122、157）。
  - 捕获超时或失败时，退回“进程 PATH + `/etc/paths`、`/etc/paths.d/*` + 下表的固定目录”，并且这次不写 `llm.detected`，下次启动再检测。
- **Which（查找工具）**：按 `:` 分割 PATH 并检查可执行权限。另外探测这些位置：

| 位置 | 说明 |
|---|---|
| `/opt/homebrew/bin` | Apple 芯片上的 Homebrew |
| `/usr/local/bin` | Intel 上的 Homebrew；Ollama.app 也装在这里 |
| `~/.local/bin` | Claude Code 原生安装位置 |
| `~/.claude/local`、`~/.npm-global/bin`、`~/.volta/bin`、`~/.bun/bin`、`$NVM_BIN` | 其他常见安装位置 |
| Ollama.app 内置路径 | Ollama 自带的命令行 |

- **启动子进程**：
  - npm 安装的工具是 `#!/usr/bin/env node` 脚本，直接执行；
  - 自定义命令用 `[shell, -c, 命令]` 执行；
  - 缺少 LANG 时补 `LANG=en_US.UTF-8`；保留 NO_COLOR、TERM=dumb、PYTHONUTF8、PYTHONIOENCODING；去掉 WSL_UTF8 和 WSLENV；
  - 超时 150 秒。
- 两个架构的程序都是原生的，所以子进程不会跑在 Rosetta 下。
- **隐私权限（TCC）**：
  - 读 `~/Documents`、`~/Desktop`、`~/Downloads` 会弹一次授权，而且子进程的访问（例如 claude 读 PDF）也算在 PinCC 头上；
  - 把文件拖进来属于“隐含同意”，不会弹窗；
  - 每 15 秒一次的文件卡片刷新（RefreshFiles），以及用 CLI 添加的文件卡片（没有经过拖入，不算隐含同意），也会触发授权；
  - 用户拒绝授权时，要区分“没有权限”和“文件不存在”：卡片显示“需要授权”，而不是“文件缺失”，摘要也不标为过时。P5 实测拒绝后 stat 和读取的返回值；
  - Info.plist 里写好用途说明。
- **不能开启 App Sandbox**：沙盒会继承给子进程，claude 和 codex 读不了自己的配置，Homebrew 装的工具也运行不了。所以不上 Mac App Store。

### 8.14 网页渲染（WebKitRenderer）

- **首选：用系统自带的 WebKit 在进程内渲染。**
  - WKWebView 挂在一个位于所有屏幕之外、从不显示给用户的无边框 NSWindow 里（没挂到窗口上或处于隐藏状态的 WKWebView，JS 定时器会被节流，页面可能渲染不完），只在主线程使用；
  - 用 `WKWebsiteDataStore.nonPersistent()`，不留任何数据；设置自定义 User-Agent 和 `mediaTypesRequiringUserActionForPlayback = .all`，不自动播放声音；
  - didFinish 之后再用 MutationObserver 等页面不再变化（最长 8 秒），取出 `document.documentElement.outerHTML`，交给同一个 `FromHtml`；
  - 同一时间只渲染一个页面，期限与 Windows 相同。
- **退路**：按 bundle id 查找 Chrome、Edge、Chromium 或 Brave，带 `--use-mock-keychain` 执行 `--dump-dom`。

### 8.15 命令行

- 同一个二进制在调用 `NSApplication.Init`（连接 WindowServer）之前就进入 CLI，始终输出 UTF-8。AppKit.framework 在进程启动时就由 dyld 加载，避开的是连接 WindowServer 这一步。shell 直接拿到退出码，不需要 `start /wait`。
- `Contents/Resources/bin/pincc` 是一个垫片脚本。因为会被符号链接到别处，必须先解析链接的真实位置：

  ```sh
  #!/bin/sh
  exec "$(dirname "$(readlink -f "$0")")/../../MacOS/PinCC" "$@"
  ```

  macOS 12.3 起的 `readlink` 支持 `-f`。用垫片而不直接链接可执行文件，是因为运行时通过符号链接可能找不到 bundle。
- **“安装命令行工具…”**：把垫片链接到 `~/.local/bin`，并在 `~/.zprofile` 里加一行 PATH；或者链接到 `/usr/local/bin`，这需要管理员授权。
- 帮助里去掉 WSL 和 PowerShell 的说明，改成 `pincc` 和 zsh 的示例。
- P1 到 P3 之间，Mac 上的 CLI 由 `cli/` 提供（只链接 shared/core 的 net10.0 控制台程序）。P3 以后改由 .app 里的同一个二进制提供。如果 P0 测得 .app 的冷启动超过约 300 ms，就保留 `cli/`，改用 NativeAOT 编译，并用 lipo 合成通用二进制。

### 8.16 数据与日志目录

- **数据**：`~/Library/Application Support/PinCC`，包括 pincc.db、img/、files/、backup/。仍可用 `PINCC_HOME` 覆盖。
- **日志**：`~/Library/Logs/PinCC/error.log`。
- **缓存**：`~/Library/Caches/PinCC`，存渲染器配置这类能重新生成的数据，Time Machine 按惯例不备份这里。
- 程序不在沙盒里，所以 GUI 和 CLI 共用同一个数据库。
- Unix 上，文件被打开时也能删除（unlink），所以图片清理逻辑不用改。

---

## 9. 平台适配对照表

| 方面 | Windows（保持现状） | macOS |
|---|---|---|
| 卡片窗口 | 无边框分层 WPF 窗口，8 DIP 圆角，1 px 描边（到期 2 px 红），无阴影，WS_EX_TOOLWINDOW | 不激活的无边框 NSPanel，8 pt 连续曲率圆角，同样的描边规则，系统原生阴影（在点击区之外），`animationBehavior .none` |
| 字体、图标、颜色 | 微软雅黑 UI / Segoe UI、Cascadia Mono、Segoe Fluent 图标，界面外框只有浅色 | SF Pro + 苹方、SF Mono，字号和行高与 Windows 相同，SF Symbols；外框用系统语义色，跟随浅色/深色和用户选的强调色，程序里不设主题选项；卡片配色两端相同，浅色卡片强制浅色外观（墨炭用深色）；“降低透明度”时不透明，“减少动态效果”时不闪 |
| 程序在哪里 | 不在任务栏和托盘，每个显示器一个 + | 不显示 Dock 图标，每个屏幕同样一个 +；隐藏主菜单保证 ⌘C/⌘V/⌘Z 可用；菜单栏图标可选；从访达或 Spotlight 再次打开时显示全部卡片 |
| 卡片快捷键 | 见 §10 | 见 §10 |
| 编辑器与输入法 | WPF TextBox | NSTextView，关闭智能替换；Return/Tab/Esc 经 doCommandBy 处理，拼音选词不受影响 |
| 鼠标与触控板 | Ctrl+滚轮调透明度，滚轮缩放，中键 1:1，Ctrl+拖出图片，DragMove | ⌘+滚动调透明度（累计触控板增量，忽略惯性），捏合或滚动缩放，⌘0 或双指轻点两下 1:1，⌥-拖出图片，手写的移动跟踪 |
| 全局热键 | RegisterHotKey，默认 Ctrl+Alt+Shift+N，低级键盘钩子录入 | Carbon 热键，默认 ⌃⌥⌘N（待定），不需要权限，录入不用事件钩子 |
| 快速记录后的焦点 | 记下之前的前台窗口，Esc 后切回 | 方案 A：卡片是不激活的 key 面板，前一个程序一直在前台；方案 B：激活后再交还 |
| 设置与卡片库 | 一个管理窗口（卡片 / 设置两个标签），置顶，Esc 关闭 | “设置…”窗口（⌘,，toolbar 分页）和“卡片库”窗口（⌘F），⌘W 关闭 |
| 开机/登录启动 | HKCU Run，首次运行自动开启并提示 | SMAppService 登录项，由用户选择开启，开关显示实时状态 |
| 提醒与定时 | 每秒一次的 DispatcherTimer；PlaySound “Notification.Reminder” | 到点精确触发的一次性定时器，加一个允许误差的同步节拍；“Glass” 用 AudioServicesPlayAlertSound 播放，跟随提醒音量 |
| 全屏 | 每秒轮询前台窗口，卡片退到全屏窗口后面，隐藏 + | 全屏应用在单独的桌面空间里，卡片天然不出现；检测只用来隐藏 + 和推迟提醒声 |
| 屏幕与缩放 | 物理像素，PerMonitorV2，跨 DPI 两步放置，DeferWindowPos | point，只需翻转一次坐标，Work 取 visibleFrame，不用处理 DPI |
| CLI | `PinCC.exe`：管道和文件输出 UTF-8（无 BOM），交互式控制台附加后用 UTF-16；通过 HKCU PATH 安装 | 同一个二进制，始终输出 UTF-8，`pincc` 垫片装到 `~/.local/bin` 或 `/usr/local/bin` |
| AI 工具 | 注册表 PATH、.cmd 垫片走 cmd.exe、可选 WSL、Job 对象 | 只有一个本机环境，必须捕获登录 shell 环境，探测 Homebrew，子进程直接执行，用 Reaper.Unix 清理子进程树（先用 `Kill(entireProcessTree)`，必要时改用进程组） |
| 无头渲染 | `msedge --headless=new --dump-dom` | 屏幕外的 WKWebView；退路是 Chromium 系浏览器 |
| 数据与隐私 | `%LOCALAPPDATA%\PinCC`，`explorer /select`，没有权限弹窗 | `~/Library/Application Support/PinCC`，“在访达中显示”，受保护的文件夹首次访问时弹授权 |
| 分发 | 单文件 exe（依赖框架版和独立版） | 通用 .app；自用时 ad-hoc 签名；给别人时需要 Developer ID 签名和公证，打包成 dmg |

---

## 10. 快捷键对照

| 命令 | Windows | macOS |
|---|---|---|
| 新建卡片（全局） | Ctrl+Alt+Shift+N | ⌃⌥⌘N（待定） |
| 用剪贴板新建 / 最小化全部 / 全部折叠 / 平铺显示（全局） | 默认无，可在设置中指定 | 默认无，可在设置中指定 |
| 进入编辑 | Enter / 双击文字 | Return / 双击文字 |
| 结束编辑（自动保存） | Esc / 点卡片外 | Esc / 点卡片外 |
| 删除卡片 | Del、Ctrl+D | ⌘⌫（也接受 ⌫）。⌘D 在 Mac 上表示“复制一份”，不用来删除 |
| 撤销 | Ctrl+Z（撤销条上也可以） | ⌘Z（撤销条上也可以） |
| 重做（撤销上一次撤销） | 在“管理 › 卡片”历史里操作 | ⇧⌘Z，也可以在卡片库历史里操作 |
| 复制卡片 / 粘贴进卡片 | Ctrl+C / Ctrl+V | ⌘C / ⌘V |
| 新建（焦点在卡片上时） | Ctrl+N | ⌘N |
| 管理 / 卡片库 | Ctrl+F | ⌘F |
| 设置 | 右键 + → 管理… →“设置”标签（有热键冲突时，菜单里有“快捷键冲突 · 去设置”直接跳转） | ⌘, |
| 在卡片间切换 | Ctrl+Tab / Ctrl+Shift+Tab | ⌘` / ⇧⌘` |
| 退出 | 右键 + → 退出 | ⌘Q |
| 置顶卡片的透明度 | Ctrl+滚轮；也可以在置顶图标上直接滚动，或用右键菜单里的透明度一行（未置顶时只提示“置顶后可调透明度”） | ⌘+滚动；也可以在置顶图标上直接滚动，或用菜单里的“透明度 ▸”（⌃+滚动是系统辅助功能的缩放） |
| 图片缩放 / 1:1 | 滚轮 / 中键 | 捏合或滚轮 / ⌘0、双指轻点两下，或三键鼠标的中键 |
| 拖出图片 | Ctrl+拖动 | ⌥-拖动 |
| 加粗 / 斜体 | Ctrl+B / Ctrl+I | ⌘B / ⌘I |
| 删除线 / 行内代码 | Ctrl+T / Ctrl+E | ⇧⌘X / ⌥⌘C（P4 在两台 Mac 上核对是否被占用） |
| 任务清单项 / 普通列表项 | Ctrl+L / Ctrl+Shift+L | ⇧⌘L（与“备忘录”相同）/ ⇧⌘8 |
| 续列表 / 缩进 / 取消缩进 | Enter / Tab / Shift+Tab | Return / Tab / ⇧Tab（输入法提交后才生效） |
| 双击下边缘 | 恢复自适应高度 | 同样恢复自适应高度（在卡片自己的边带内，替代系统“拉伸到屏幕边缘”的行为） |

Mac 上额外的规则（由表完整性测试检查）：
- 卡片和编辑器都不绑定 ⌃+字母，留给 Emacs 式按键；
- ⌘D、⌘H、⌘M 不用于删除或隐藏。⌘H 会连到期卡片一起隐藏，所以“最小化全部”在程序内部实现；
- 在卡片上按 ⌘W 不做任何事；
- 所有全局热键都必须含 ⌘ 或 ⌃。

---

## 11. 一致性保障（契约与测试）

1. **同一份源码，三种编译。** `shared/` 同时编进 WPF、AppKit 和纯 net10.0 的测试项目。任何界面类型漏进 `shared/`，测试项目就编译不过。另有一个测试扫描 `shared/`：`*.Windows.cs` 以外的文件里出现 `System.Windows`、`AppKit`、`Microsoft.Win32` 就失败，任何文件里出现 `#if` 也失败。`build.cmd` 和 `build-mac.sh` 都先跑完整测试，再生成产物。
2. **在重构之前，先用今天的代码录下基准输出（golden）**，之后在 win-x64、osx-arm64、osx-x64 上都必须通过：

| golden | 内容 |
|---|---|
| `golden/cli/*.txt` | add、list --json、list --md、show、set、rm、log、restore、help 以及各种错误退出码。在预置数据的 `PINCC_HOME` 上用固定时间和固定时区运行。固定时区要新增 `PINCC_TZ` 钩子，由 When 统一使用，因为 Windows 上的 .NET 不读 TZ 环境变量；Mac 和 CI 上另外设 `TZ`。`{HOME}` 和 `{CMD}` 统一替换；help 里的平台段落按平台分别比较 |
| `golden/layout/*.json` | 场景 → LayoutResult。覆盖负坐标、缩放 1/1.25/1.5/2、四个屏幕角、刘海工作区、Dock 在左边，以及自由、编辑中、平铺、胶囊、隐藏、+N 等状态，还有悬停推迟 |
| `golden/scenes/*.json` | 卡片 + 状态 + 时间 → CardScene。覆盖链接、文件、文件缺失、文件夹、纯图片、多图、到期、任务 n/m、摘要过时、胶囊、撤销条 |
| `golden/md/*.json` | 一组 Markdown 样例 → MdRender 结果 |
| EditorOps 记录 | 编辑操作的输入和结果 |
| `fixtures/win-created.db` | Windows 上建的数据库，必须能在两台 Mac 上打开、迁移并列出 |

   修改任何一个 golden，都要作为单独的提交，在三台机器上确认。
3. **FakeHost 场景测试**：不开窗口，在单线程的 SynchronizationContext 下运行，三台机器都跑。覆盖：
   - 新建 → 输入 → Esc 提交；新建后什么都没写，不留记录；清空已有卡片（没有图片和摘要时）等于删除，同一步可以撤销。
   - 完成 → 撤销条 → 指针在卡片上时一直保留，离开 4 秒后消失 → 重新平铺；整组撤销。
   - CLI 通过另一个连接写入，一次 Poll 之内就能看到；编辑中 CLI 写入时，保留用户的文字，并新增一个撤销组。
   - 提醒只响一次（包括启动时已经到期的一批）；`NextWake` 的正确性。
   - 全屏时推迟提醒声；溢出 +N；热键“重复”和“被占用”的区分。
4. **两端真程序对照（parity）**：
   - 输入：`tests/scenarios/*.json`，包括假显示器、预置数据的 CLI 命令、设置、`PINCC_STEPS` 步骤和 `PINCC_NOW`。
   - 两端都以屏幕外渲染模式运行，输出统一格式的 layout.json、每张卡片 CardScene 的 SHA-256，以及 PNG。
   - `dotnet run --project tests -- parity win mac` 的要求：
     - CardScene 先去掉依赖字体实测的字段，再比较哈希，必须完全相同。实测字段包括：自动高度、由它得出的尺寸、是否溢出，以及由溢出决定的淡出和“✨ 摘要”标签。
     - 实测字段单独比较：自动高度最多差一行；溢出状态只允许在差一行造成的临界情况下不同。
     - 固定大小卡片的位置完全相同；胶囊、隐藏、置顶标记和 +N 数量完全相同；自动高度卡片带来的位置差异允许。
   - 另外生成一张左右并排的对照图，供人工检查结构、顺序、颜色和文字。
   - 辅助界面（设置各页、卡片库、选择器、+ 按钮与速拨、提示条）由两端的 `PINCC_UI_SNAPSHOT` 各自渲染，放进同一张对照图；菜单比较 MenuSpec 文本（见 §5.7）。
5. **视图层不做任何决定。** `src/UI` 和 `mac/Views` 只接收 CardScene、HubModel、MenuSpec，以及选择器、卡片库和设置的模型。在视图目录里（事先批准的适配器文件除外）出现下面任何一种，构建就失败：
   - 中文字符串字面量（文案必须来自 Strings 或 PlatformText）；
   - 窗口尺寸、位置和最小尺寸的赋值里出现数字字面量（这些值必须来自 Metrics）。现有 WPF 文件里其他的数字（字号、角标、圆角等纯样式值）先登记进白名单，白名单只减不增；
   - 直接访问 `Store.` 或调用 `DeskCore.Change`；
   - 手写的 “Ctrl+” 或 “⌘” 快捷键文字（必须来自 KeyBindings 的格式化函数）。
6. **表完整性测试**（任何系统上都能跑）：
   - 每个命令都有 Windows 和 Mac 绑定，或者明确标为“无”；
   - 同一平台内绑定不重复；
   - 每个 IconId 同时有 Segoe 码位和 SF Symbol；
   - 每个 PlatformText 键在两张表里都存在；
   - 每行 SettingsSchema 都声明了适用平台；
   - MenuSpec 里的每个平台行（§5.3）在两端都有对应；
   - Mac 的规则（见 §10）。
7. **数据契约**：数据库结构 user_version 1、同一套迁移、同样的 rev 和撤销组语义、同样的 WAL 文件格式；`mon` 是不透明的 id，`x/y/w/h` 是相对工作区左上角的 DIP 偏移，为显示而做的夹紧从不写回。
8. **CLI 契约**：同一组命令、JSON 字段及其顺序、`--md` 输出、退出码 0–4、`-` 表示从 stdin 读取。有测试检查：两端的 help 除平台段落外逐字节相同。
9. **一份版本号和一份差异清单**：`shared/Version.props` 同时给两端盖版本号。`docs/DESIGN.md` 增加“平台适配”一节，内容由 KeyBindings、PlatformText 和 SettingsSchema 的平台标记生成或校对，列出每一个允许的差异，也只列这些。

---

## 12. 构建、签名、分发

### 12.1 工具链

- **Apple 芯片 Mac（唯一编译 `mac/` 和发布 .app 的机器）**：
  - macOS 26.2 或更高、Xcode 26.6、arm64 版 .NET SDK 10.0.401；
  - 执行 `dotnet workload install macos`，用 `global.json` 和 workload set 固定版本。
  - 按 2026-09 查到的信息，.NET 10 最新的 macos 工作负载（26.5.10318）要求 Xcode 26.6，而 Xcode 26.6 要求 macOS 26.2 以上。
- **Intel Mac**：macOS 14 或更高，装 x64 版 SDK，只用来跑测试和检验 x64 版。macOS 27 只支持 Apple 芯片，Intel Mac 无法再升级到能运行以后 Xcode 的系统。
- **Windows 本机**：
  - 能做：`shared/` 和 `tests/` 的全部开发和测试、Windows 版的全部工作、Mac 端纯逻辑（`*.Mac.cs`）的单元测试；
  - 不能做：编译 `mac/`。
- **从 Windows 构建 Mac 版的流程**：
  1. 用 `tools/mac.sh` 在 WSL 里把代码 git push 到 Mac 上的裸仓库（P0 建好），再在 Mac 上检出。项目已经是 git 仓库（已有提交 d449dee）；`src/Core` 用 `git mv` 移到 `shared/core`，保留历史。
  2. 通过 ssh 运行 `mac/build-mac.sh`；
  3. 用 scp 取回 `artifacts/mac`。

  `tools/mac.sh` 可以指定目标机器：用同样的方式把代码送到 Intel Mac 并运行 `build-mac.sh test`。git push 只同步已提交的内容；想测未提交的改动时，改用 rsync，并加 `--filter=':- .gitignore'` 和排除 `artifacts/`，不传 bin/、obj/、dist/、.probe/。

  前提条件：
  - Mac 开启“远程登录”；
  - 该用户已在 Mac 上登录图形界面。verify 用 `open` 启动界面程序，需要现有的登录会话；
  - release 模式在 ssh 里签名前，先执行 `security unlock-keychain`，并且与 codesign、notarytool 写在同一条 ssh 命令里，否则 codesign 会报 errSecInternalComponent。

### 12.2 build-mac.sh 的四种模式

| 模式 | 做什么 |
|---|---|
| `test` | 运行 net10.0 测试 |
| `app` | 发布通用 .app，不开 hardened runtime：默认 bundle id 用 `.dev`；加 `--personal` 时用正式 bundle id，作为自用的日常版本 |
| `verify` | 先 app，再用 `open -W -g -j -n --env … --stdout <日志> --stderr <日志>` 以屏幕外渲染模式跑完所有场景（`-W` 等程序退出后才返回），然后把 layout.json 和 PNG 收集到 `artifacts/mac` |
| `release` | 只在对外分发时使用（§15 第 4 条）：Developer ID 签名 + hardened runtime + 安全时间戳 → notarytool 提交 → staple → 打 dmg |

### 12.3 签名与分发

- **自用（两台 Mac）**：
  - ad-hoc 签名，**不开** hardened runtime。ad-hoc 签名加 hardened runtime 再加 bundle 里的 dylib，启动时会因 Team ID 不匹配而失败（dotnet/macios#26609）。
  - 用 scp 或 rsync 拷贝过去，不会带上隔离属性，Gatekeeper 不会拦。
  - 注意：ad-hoc 签名没有稳定的身份，它的指定要求就是 cdhash，每次重新构建都会变。TCC 会把新版本当成另一个程序，访问文稿、桌面、下载文件夹的授权每次更新后都会重新弹出。
    - 想避免的话，可以用免费 Apple ID 的 Apple Development 证书签名；
    - 或者在编译机的钥匙串里建一个自签名的“代码签名”证书，用它签名（仍不开 hardened runtime）。
    - 这样指定要求变成“bundle id + 证书”，授权能跨版本保留，也不需要付费。
- **给别人用**：
  - 需要加入 Apple Developer Program（每年 99 美元），用 Developer ID Application 证书签名；
  - 开 hardened runtime，加 `com.apple.security.cs.allow-jit` 权限（JIT 版需要，NativeAOT 版不需要），带安全时间戳；
  - 经 notarytool 公证并 staple，打包成 dmg。
  - macOS 15 起，不能再用“右键 › 打开”绕过 Gatekeeper，未公证的程序要到“隐私与安全性”里点“仍要打开”。
- **不沙盒，不上 App Store**（原因见 §8.13 的隐私权限一条）。
- **通用二进制**：net10.0-macos 的 Release 构建在不指定 RID 时，默认生成 osx-x64 + osx-arm64 的通用包。
- **最低系统**：macOS 14（.NET 10 的要求）。
- **生命周期**：net10.0-macos 的支持期跟随 MAUI 政策，到 2027-05-11 结束，所以大约每年要升级一次目标框架和 Xcode。只有 `mac/` 的项目文件需要改。

---

## 13. 实施阶段（约 48 个工作日）

### P0 · Mac 工具链与风险验证（3 天，丢弃式，决定是否继续）

**工作**
- Apple 芯片 Mac：安装 Xcode 26.6（需要 macOS 26.2+）、arm64 SDK 10.0.401 和固定版本的 macos workload；开启远程登录；建一个 git 裸仓库；写好 `tools/mac.sh`。
- Intel Mac：记下系统版本（必须 ≥14），安装 x64 SDK。
- 在仓库外建一个 `spikes/MacSpike`（net10.0-macos），逐项验证：
  1. 用 `[.borderless, .nonactivatingPanel]` 创建的 key 面板，设 `hidesOnDeactivate=false`、`becomesKeyOnlyIfNeeded=false`、`.floating`/`.normal` 层级和 `[.canJoinAllSpaces, .ignoresCycle]`：
     - 点击时不会激活程序，但面板成为 key，能收到 Return、⌘⌫ 等按键；
     - Safari 在前台时，`acceptsFirstMouse` 和 `.activeAlways` 悬停仍然有效；
     - 后台时在 mouseMoved 里设置缩放光标是否生效。
  2. 编辑和焦点：
     - 面板里的 NSTextView 用苹果拼音和搜狗输入法都正常：候选词、回车上屏、Esc；
     - 通过隐藏的编辑菜单，⌘C、⌘V、⌘Z 可用；
     - Esc 后键盘回到 Safari 等原程序，卡片留在屏幕上且不闪（方案 A 的交还做法）；
     - NSPopover 里的输入框在 PinCC 不在前台时能打字、能用拼音，且不激活程序。
  3. await 之后回到主线程。
  4. Carbon 注册 ⌃⌥⌘N 成功（回调用 `[UnmanagedCallersOnly]`），重复注册返回 -9878。
  5. CLI 在 `NSApplication.Init` 之前返回：不闪 Dock 图标，输出 UTF-8，退出码正确；测量冷启动时间。
  6. 通过解析器加载 libsqlite3：`sqlite_version` ≥ 3.43，能读出 `compile_options`，emoji 能原样读写。
  7. 通过 `ssh … open -W -g -j -n --env` 启动时，`cacheDisplay` 能把视图渲染成 PNG。
  8. 通用包能在 Intel Mac 上运行；记下 30 个面板时的内存占用和空闲 CPU。
  9. 系统服务：
     - SMAppService 在 ad-hoc 签名下能否注册；
     - 登录启动的判断方法（keyAELaunchedAsLogInItem 或开机时长）；
     - NSStatusItem；
     - 挂在屏幕外窗口里的 WKWebView 能取出一个 JS 页面的 DOM。
  10. 桌面空间：
      - 在“显示桌面”、调度中心、台前调度下，普通卡片、到期卡片（`.stationary`）和 + 按钮的表现，以及 ShowAll 能否把被推开的卡片拉回；
      - “显示器具有单独的空间”开和关两种设置下，菜单栏、visibleFrame 和全屏的表现。

**验证**：在 `docs/mac-port.md` 里记录每一项的通过/失败、所用命令和测得的数字。

**完成标准**：第 1、2、3、5、6 项是闸门。
- 第 2 项的拼音或 Esc 交还失败 → 采用焦点方案 B；popover 输入失败 → 改用自己的不激活子面板。
- 第 5 项里，不闪 Dock、输出 UTF-8、退出码正确必须通过；冷启动超过约 300 ms → 保留 `cli/` 并改用 NativeAOT。
- 第 1、3、6 项没有退路，任何一项失败就停在 P0，回到 §2 重新评估路线。
- 第 7 项不是闸门：通过 ssh 渲染失败时，改为在 Mac 的终端里直接运行 verify。

这一阶段只新增 `tools/mac.sh` 和 `docs/mac-port.md`，不修改任何现有文件（包括 src/、tests/ 和 build.cmd）。

### P1 · 共享 core，测试跨平台，Windows 行为完全不变（4 天）

**工作**
1. 先用今天的程序录下基准，放进 `artifacts/baseline-win`（并把 `artifacts/` 加进 .gitignore）：
   - 在预置数据的 `PINCC_HOME` 上，用固定时区录 CLI 输出；
   - 用 `ui.sh`（PINCC_UI_SNAPSHOT）录下 ui-*.png。这个模式只输出这些，不写 layout.json；layout.json 和卡片 PNG 的基准等 P2 加上 `PINCC_SCENARIO` 后再录。
2. 调整结构：
   - 新增根目录的 `global.json` 和 `shared/PinCC.Shared.props`，用 `git mv` 把 `src/Core` 移到 `shared/core`；
   - 把 `Px`、`Screen`、`Corner`、`Slot` 移到 `shared/shell/Geometry.cs`，`Layout.cs` 移到 `shared/shell`；
   - `src/PinCC.csproj` 导入 props，DllImport 的程序集特性移到 `src/WinAssembly.cs`；
   - tests 改为 net10.0（去掉 UseWPF、manifest 和 Native.cs；`tests/PinCC.Tests.csproj:14` 的 `../src/Core/**/*.cs` 和 Layout.cs、Native.cs 链接换成导入 shared props）；
   - README 的“目录”一节把 `src/Core/` 改为 `shared/core/`。
3. 加上各个钩子：
   - Sqlite 解析器、AppPaths、PathSyntax；
   - `Cli.Main`、`IsCliInvocation`、`Help(CliPlatform)`；
   - `IPageRenderer`、`IToolHost`、`IChildReaper`，Windows 代码原样搬过去；
   - `ToolHost.Mac` 和 `Reaper.Unix`：到处都编译，只在 macOS 上启用。
4. §4.1 的两处输出修正各自单独提交，同时更新 golden。
5. 测试：
   - Windows/WSL 专有的用例加 IsWindows 条件：`FetchTests.cs:118-161`、`187-198`，以及 `tests/Program.cs:153-155`、`174-176` 这些用 Windows 路径的文件卡片用例。后者也可以改成对 `PathSyntax.IsAbsolute(line, os)` 分别传入两种系统来测，两端都能跑；
   - 新增 Mac 测试：/bin/sh 下的 Proc、POSIX 文件卡片、DataHome、libsqlite3 版本、Mac 命令拼装（所有系统上都跑）；
   - 新增守卫测试：界面类型泄漏、`#if`、SetDllImportResolver 只调用一次；
   - 新增 `build-mac.sh test`，并建立 `cli/`（只链接 shared/core 的 net10.0 控制台程序，先不用 NativeAOT）。P1 结束时（累计约第 7 天）起，Mac 上就能用 `pincc` 了。

**验证**
- Windows：
  - `build.cmd` 通过，测试数不少于基准，失败为 0；
  - CLI golden 除两个修正提交外没有差异；
  - `ui.sh` 输出的 ui-*.png 与基准完全相同。
  - 发布之前，先把正在运行的 `dist\standalone\PinCC.exe` 改名为 `.old`。
- Mac：
  - `tools/mac.sh test` 在 arm64 上通过，Intel Mac 本地运行也通过；
  - Mac 上 CLI 的输出在统一替换后与 golden 相同；
  - `win-created.db` 在 Mac 上能正确列出。

### P2 · 抽出 shared/shell，先让 WPF 用上（9 天，有基准保护）

**工作**
1. 先加屏幕外场景模式 `PINCC_SCENARIO`（在 Program.cs 拿 mutex 之前分流，不开窗口，不通知也不等待正在运行的实例），以及 FixtureScreens、FrozenClock 和 `PINCC_TZ`。用一个临时适配器接在今天的代码上，录下每个场景在 Windows 上的基准（layout.json、卡片 PNG），以及 scene、layout、EditorOps 的 golden。
2. 然后分步小提交，每一步都不改变行为：
   1. Metrics 和 Layout 辅助函数；
   2. `LayoutPass.Compute`，由 WinWindowHost 用今天的 DeferWindowPos 和两步放置来应用；
   3. CardPresenter/CardScene、EditorOps、ResizeGeometry、MdRender。CardWindow 和 MarkdownView 保留 XAML，只改为按 scene 绘制；
   4. Palette 数据和 IconId、HubModel、各选择器模型、LibraryModel、SettingsSchema、RecorderRules；
   5. MenuSpec、Command/KeyBindings、PlatformText、Strings；
   6. DeskCore 接到 Ports.cs 后面，配上 WinPlatform 的各个适配器，以及 `IScheduler.At` / `NextWake`。
3. 最后加上 FakeHost 场景测试、表完整性测试和视图守卫。

**验证**
- 三台机器上：layout、scene、md、EditorOps 的 golden，FakeHost 场景，表测试和守卫测试全部通过。
- Windows 上每次提交后：场景的 layout.json、scene 哈希和各卡片 PNG 都与基准相同（PNG 逐像素比较），`ui.sh` 输出的 PNG 逐像素相同，CLI golden 不变。
- 最后由你在自己的桌面上做 10 分钟冒烟检查（桌面上不做任何自动操作）：
  - 用热键新建卡片，按 Esc 后焦点回到原程序；
  - 悬停 +，依次点平铺、折叠、最小化；
  - 编辑并自动保存；
  - 完成卡片，再从撤销条撤销；
  - 拖动卡片，压住别的卡片时被推开；
  - 拉大卡片，再双击下边缘恢复自适应高度；
  - 置顶和 Ctrl+滚轮调透明度；
  - 提醒；
  - 粘贴截图；
  - ✨ 摘要；
  - 第二个显示器。

  之后再正常使用一天左右，Mac 界面的工作才开始依赖这一阶段的成果。

### P3 · Mac 桌面 MVP：卡片能画出来、能平铺，CLI 修改实时同步（6 天）

**工作**
- `mac/PinCC.Mac.csproj`，带 Info.plist 和开发用的 entitlements；`Program.cs` 先判断 CLI。
- AppDelegate（Store、DeskCore、MacInstanceGate、再次打开 → ShowAll、退出 → Flush、错误日志）和 MainMenu。
- MacScreens（坐标翻转、visibleFrame、UUID）、MacWindowHost、MacScheduler（同步节拍 + 严格的 At）、MacSystemEvents。
- CardPanel 和 CardContentView，只负责显示：各部分、MdAttributed、配色、描边、淡出、胶囊、图片卡片、红框、角标。
- HubPanel（+ 按钮、颜色点、角标）、ToastPanel、MacImages、MacSnapshot。
- 场景模式，以及 `build-mac.sh verify`。

**验证**
- `tools/mac.sh verify` 在 Mac 上跑完所有场景，与 Windows 结果的对照（`tests -- parity`）通过：scene 哈希相同，固定大小卡片完全一致，自动高度卡片最多差一行。检查左右对照图。
- 两台 Mac 上：
  - 执行 `pincc add x` 后，1 秒内出现卡片；
  - 重新排列显示器或拔掉一个显示器会触发重新布局，数据库里的 x/y 不变（对比数据库确认）；
  - Dock 在左边、Dock 自动隐藏、刘海屏三种情况下布局都正确。

### P4 · Mac 交互与 Windows 对齐（8 天）

**工作**
- P0 选定的焦点方案（A 或 B），跟踪区域，第一次点击就生效。
- 用 ResizeGeometry 实现拖动和缩放循环，每次手势只调用一次 `OnUserMoved`。
- CardEditor：EditorOps、doCommandBy、hasMarkedText 保护、覆盖 paste。
- HubPanel 的悬停速拨。
- 卡片状态：撤销条、临时展开、胶囊。
- MenuRenderer（MenuSpec 生成带 SF Symbols 的 NSMenu）、各弹出面板、`KeyBindings.Mac`（⌘Z、⌘⌫、⌘`、⌘F、⌘0、⌘+滚动、捏合、⌥-拖动）。
- 提醒的 At() 加提示音（AudioServicesPlayAlertSound），MacFullscreen 推迟提醒声。
- “减少动态效果”和“降低透明度”。

**验证**
- 每个 Step 动作都能在 Mac 场景模式下运行，每一步的 layout.json 与 Windows 对照通过；绑定表和菜单表的测试通过。
- 两台 Mac 上的手工检查清单：
  - 卡片和提醒弹出面板里都能用拼音输入；
  - Esc 后回到之前的程序；
  - Safari 在前台时，第一次点击 ○ 就能完成卡片；
  - PinCC 不在前台时悬停按钮仍会出现，PinCC 失去焦点时卡片不会消失；
  - PinCC 不在前台时悬停 + 出现速拨，三个按钮第一次点击就生效；
  - 单击未在编辑的卡片后，Return、⌘⌫、⌘`、⌘F 都能收到；
  - ⌘C、⌘V、⌘Z 可用，撤销条上的 ⌘Z 也可用；
  - ⇧⌘X、⌥⌘C、⇧⌘L、⇧⌘8 没有被系统、输入法或常用程序占用；
  - Safari 全屏所在的空间里没有卡片，提醒声被推迟；
  - 卡片全部被挡住 10 分钟后，提醒仍在 1 秒内触发；睡眠唤醒后也一样。

### P5 · Mac 系统功能、设置与卡片库（9 天）

**工作**
- MacHotkeys：Carbon 注册、CopySymbolicHotKeys、Mac 冲突表、录入视图、默认 ⌃⌥⌘N（取决于 §15 第 2 条的决定）。
- MacClipboard（读和写）、拖入目标和拖出。
- MacShell：打开、在访达中显示、只定位不运行的扩展名表、SMAppService 用户选择开启、CliInstaller。
- 接上 ToolHost.Mac：启动时在后台捕获登录环境，点“检测”时再捕获一次；首次检测和自动摘要等捕获完成后再跑（§8.13）。先用 Reaper.Unix 第一版，孤儿进程测试失败才改用第二版。
- WebKitRenderer 和 Chromium 退路，IsPackage 钩子。
- 由共享模型生成的设置窗口和卡片库窗口，菜单栏图标开关。
- TCC 用途说明；首次运行的提示条提供“登录时打开”按钮并显示热键。

**验证**
- 所有机器上：命令拼装、登录环境解析（带开机横幅和 ANSI 颜色码的样例）和 Which 的测试通过。
- 只在 Mac 上跑的测试：
  - `sleep 60 & sleep 60` 在 1 秒期限下不留孤儿进程；
  - Proc 能处理 UTF-8、stderr、超时和 4 MB 的 stdin；
  - WKWebView 能取出本机回环地址上一个 JS 页面的 DOM。
- 从访达启动程序后的手工检查：
  - “检测”在两台 Mac 上都能在正确的 Homebrew 目录下找到 claude、codex、ollama；
  - ✨ 对一个网址和一个从 `~/Documents` 拖进来的 PDF 都有效，同一个构建里只弹一次写明 PinCC 的授权；拒绝授权后，文件卡片显示“需要授权”，而不是“文件缺失”；
  - 首次运行时（从访达启动），“检测”能找到用 nvm 安装的工具；
  - 被别的程序占用的热键会被报告；
  - ⌃⇧⌘4 截的图能粘贴进新卡片；从 Safari 拖进来的链接保留标题和网址；从 Safari 和 Chrome 复制文字后粘贴，都记下来源网址；
  - Mac 端的 `PINCC_UI_SNAPSHOT` 输出设置、卡片库、选择器、+ 按钮与速拨的 PNG，菜单的 MenuSpec 文本与 Windows 一致；
  - 安装后，新开的终端里 `pincc` 可用；
  - 登录项开关与系统设置里的状态一致；
  - 在 Mac 上改的设置在 `pincc` 里能看到；历史恢复与 Windows 的撤销组语义相同。

### P6 · 性能、打包、全面对照、文档（5 天）

**工作**
- 以 P0 的数字为基准，用 Instruments（Time Profiler、Allocations、Energy）在两台 Mac 上测量 50 张卡片加 10 张图片卡片的情况。据此调整 NSCache 成本和解码档位；如果 Energy 报告显示有收益，再加 WAL 文件监视。
- NativeAOT 只在必要时做：只有冷启动或内存明显超出目标时，才尝试 PublishAot 的通用 GUI 构建。这要为所有用到反射序列化 JSON 的地方加 JsonSerializerContext，包括：
  - `Card.cs:47,56,98-99,139,249`；
  - `Cli.cs:106、115、276-301`：Dto 及其中嵌套的 color、progress，以及 log、restore 的匿名对象，都要改成具名记录；`Cli.cs:305-306` 的 `Json(object)` 改为按类型使用 JsonSerializerContext；
  - `Llm.cs:118,125`；
  - 移进共享层的 layout.json 写出（原 `Desk.cs:1579-1580`，匿名对象）和 llm.env 写出（原 `ManageWindow.cs:267`）；
  - 新增的 `scenarios/*.json` 读取。

  改动以 Windows 上的 CLI golden 为准，不能变。否则仍用 JIT，只在 P0 标出问题时才发布精简的 AOT CLI。
- 打包：
  - 只自用时（§15 第 4 条），用 `build-mac.sh app --personal` 生成正式 bundle id 的自用版，签名方式见 §12.3；
  - 对外分发时，用 `build-mac.sh release`：Developer ID 签名、hardened runtime、时间戳、notarytool、staple、dmg。
- 文档：README 增加 macOS 一节（安装、由 KeyBindings 生成的快捷键表、数据目录、CLI）；DESIGN.md 增加“平台适配”一节。

**验证**
- `lipo -archs` 显示 x86_64 和 arm64；
- `codesign --verify --deep --strict` 通过，公证版 `spctl -a -vv` 也通过；
- 在两台 Mac 的新用户账号上安装（对外分发时用公证过的 dmg，只自用时用自用版），登录项在重启后仍然有效；
- 30 张卡片静置 5 分钟，空闲 CPU 约为 0%；
- 两台 Mac 上的全面对照 0 失败；
- Windows 上 `build.cmd` 通过（测试数不减少，失败为 0）；CLI golden、场景的 layout.json 和卡片 PNG、`ui.sh` 输出都与 P2 结束时的基准相同。

### P7 · 预留修问题的时间（4 天）

**工作**：修复 P3–P6 暴露出来的问题，例如：
- 输入法的边角情况；
- Keynote、PowerPoint 和无边框游戏的全屏判断；
- macOS 15 和 26 上的热键冲突；
- claude 读文件时的 TCC 授权归属；
- 触控板调透明度的手感；
- 超出对照容差的换行差异。

另外可以加一个 macOS CI 任务，运行 `dotnet run --project tests`。

**验证**：三台机器上所有 golden、FakeHost 场景和对照全部通过；在两台 Mac 上重跑手工检查清单；你在 Windows 上重做一次冒烟检查。

| 阶段 | 天数 | 累计 |
|---|---|---|
| P0 验证 | 3 | 3 |
| P1 共享 core | 4 | 7 |
| P2 抽出 shell | 9 | 16 |
| P3 Mac 显示与平铺 | 6 | 22 |
| P4 Mac 交互 | 8 | 30 |
| P5 Mac 系统功能 | 9 | 39 |
| P6 性能与打包 | 5 | 44 |
| P7 预留 | 4 | 48 |

---

## 14. 风险与对策

| 风险 | 对策 |
|---|---|
| 把 Desk.cs 和 CardWindow 的逻辑搬进 shared/shell 时，Windows 版出问题（这个程序每天在用） | 重构前先用今天的代码录 golden（CLI、layout、scene、EditorOps、屏幕外 PNG）；`src/PinCC.csproj` 的路径和 exe 形态不变，用源码链接保持内部可见性和 DllImport 范围；WPF 视图保留 XAML，只换数据来源，不重写；Win32 代码不搬；每次提交只做一步不改变行为的修改，靠比对把关；屏幕外检查不碰桌面和正在运行的实例；发布前把正在运行的 exe 改名为 `.old`；P2 结束后由你做冒烟检查并正常使用一天 |
| 移动 `src/` 或在 `src/` 里嵌套项目，会让 `.probe` 脚本失效或导致重复编译 | 共享代码放在顶层 `shared/`；`src/PinCC.csproj` 原地不动；`.probe/` 不写也不清理；脚本的输出写到 `artifacts/` |
| 中文输入法在不激活的 key 面板里出问题：候选框孤立，或者面板看起来有焦点却打不了字；nonactivating 标志创建后不能再改；Esc 后“卡片留在屏幕上、键盘交还原程序”没有公开接口；NSPopover 在程序不在前台时可能激活程序或拿不到 key | P0 先在两台 Mac 上测苹果拼音和搜狗、Esc 交还和 popover 输入；每个面板创建时就确定样式；按键在 doCommandBy 里处理，并用 hasMarkedText 保护；方案 B 放在 `IFocus` 后面，DeskCore 不用改；popover 不行就改用自己的不激活子面板 |
| 后台程序改不了光标：`.activeAlways` 跟踪区域不支持 cursorUpdate，后台时 NSCursor.set() 也可能无效 | P0 第 1 项实测；不行就接受“后台时边带不显示缩放光标，但照样能拖动缩放” |
| .NET for macOS 的生命周期：net10.0-macos 支持到 2027-05-11 结束；每年都要新 Xcode；Intel Mac 跑不了以后的 Xcode；目前用 C# 写 AppKit 并在上线的产品很少 | 只有 `mac/` 用 macOS 目标框架，升级只改项目文件；用 global.json 固定 SDK 和 workload；`mac/` 只在 Apple 芯片 Mac 上编译；Intel Mac 只跑测试和 x64 版，如果它的系统低于 macOS 14，就改在 Apple 芯片 Mac 上用 Rosetta（`arch -x86_64`）跑 x64 测试；Carbon 和没有绑定的 API 用 P/Invoke；golden、scene 和对照都与语言无关，将来换成 Swift 界面 + NativeAOT 共享库也不用改逻辑 |
| 字体度量不同（雅黑/Segoe 与苹方/SF），会影响自动高度，进而影响溢出淡出、✨ 是否显示和胶囊降级 | 行高、内边距、内容宽度和 Budget 公式都共享；中文字符在两种字体里都是全角，差异主要在拉丁文换行；对照允许自动高度卡片最多差一行，其余必须完全相同，大多数布局样例用固定高度；是否内置同一种字体由你决定 |
| App Nap 和定时器合并让 Mac 上的提醒迟到；每秒轮询浪费电 | 用严格的一次性定时器对准 `NextWake`，唤醒、时间、时区、日期、数据变化时重设；1 秒节拍允许误差，只管同步；只在 LLM 运行时持有 beginActivity；WAL 监视是可选的；P4 手工测试卡片被挡住 10 分钟后和睡眠唤醒后的准时性 |
| NativeAOT 的工作量比看上去大：Card、Cli（匿名对象）、Llm，以及移进共享层的 layout.json 和 llm.env 写出，都用反射序列化 JSON，改成源生成可能改变 Windows CLI 的 JSON 输出 | AOT 不在关键路径上。P6 只有在测量结果说明需要时才做，每处 JSON 改动都必须让 Windows 上的 CLI golden 逐字节不变；默认发布 JIT 版加 allow-jit 权限 |
| 签名与隐私权限：ad-hoc 签名 + hardened runtime + bundle 里的 dylib 会因 Team ID 不匹配而无法启动（macios#26609）；ad-hoc 签名每次构建身份都变，授权要重新弹；带隔离属性的拷贝会被 Gatekeeper 拦下；受保护文件夹的授权都算在 PinCC 头上，包括子进程 claude 读文件 | 开发版和自用版不开 hardened runtime，用 scp/rsync 拷贝（不会带隔离属性）；自用版用免费的 Apple Development 证书或自签名证书签名，授权跨版本保留；对外分发用 Developer ID + hardened runtime + allow-jit + 公证；Info.plist 写好用途说明，优先用拖入建卡（隐含同意）；P5 测试授权归属；永不开沙盒 |
| Carbon 热键是老接口：不会报告与系统或其他程序快捷键的冲突；macOS 15.0–15.1 拒绝只用 Option 的组合 | 用独占标志注册，并要求含 ⌘ 或 ⌃（由表测试保证）；检查 CopySymbolicHotKeys、自己的菜单和 Mac 已知冲突表；不用事件钩子，所以不会弹“输入监控”权限；接口由 `IHotkeyService` 隔离；如果 ⌃⌥⌘N 在测试机上有冲突，就不设默认值，改为首次运行时询问 |
| 全屏检测只能靠推测：系统没有公开接口能得知别的程序全屏或专注模式 | 桌面空间本来就让卡片离开原生全屏；检测只用来隐藏 + 和推迟提醒声，判断错了只是提醒声提前响，不会丢数据 |
| 时间一长，逻辑慢慢溜进某一端的视图（例如在 CardPanel 里打个补丁），“一致”悄悄被破坏 | 视图只接收 scene 和模型；构建守卫拒绝视图目录里的中文字面量、Metrics 数值、对 Store/DeskCore 的直接修改、手写的快捷键文字，以及 shared/ 里的任何 `#if`；表完整性测试发现缺少的对应项；每次发布 Mac 版前都做全面对照；DESIGN“平台适配”一节列出每个允许的差异 |
| 通过 SSH 远程运行时可能连不上窗口服务器，无法渲染 | P0 第 7 项测试通过 LaunchServices 的 `open -g -j -n --env` 启动；不行就在 Mac 的终端里运行 `build-mac.sh verify`，仍是屏幕外渲染，结果再拷回来 |

---

## 15. 待决定事项（附建议默认值）

| # | 问题 | 建议 |
|---|---|---|
| 1 | Mac 上默认显示菜单栏图标吗？（Windows 上刻意没有托盘图标） | 默认显示，可以关掉。没有 Dock 图标的程序，Mac 用户习惯到菜单栏去找设置和退出 |
| 2 | Mac 的默认全局热键 | ⌃⌥⌘N，先在两台 Mac 上确认没有冲突；有冲突就不设默认，首次运行时让你选 |
| 3 | 登录启动 | 由用户选择开启（Mac 惯例），首次运行的提示条上给一个按钮 |
| 4 | 分发范围 | 只在自己的两台 Mac 上用：免费签名就够，建议用免费 Apple ID 的 Apple Development 证书，这样更新后不用重新授权文件夹访问（§12.3）。要给别人用：需要 Apple 开发者账号（每年 99 美元）并公证 |
| 5 | 卡片出现在所有桌面空间，还是只在创建它的空间 | 所有空间：卡片就像桌面本身，平铺也是按显示器算的 |
| 6 | 字体 | 两端各用系统字体，接受自动高度卡片最多差一行；另一个选择是两端内置同一种字体，约多 16 MB，也不够原生 |
| 7 | 两处 CLI 输出修正是否也应用到 Windows（日期不随区域设置变化；图片行写成 `![](<路径>)`） | 应用。两端输出一致，而且现在 Windows 上路径含空格时也会出错 |
| 8 | 远程验证 | Apple 芯片 Mac 开启“远程登录”（SSH）；告诉我 Intel Mac 的系统版本（需要 ≥14） |
| 9 | 两台电脑之间同步卡片 | 不在本计划内。需要的话另行设计：WAL 模式的数据库不能直接放进 iCloud 或 OneDrive |
| 10 | 深色模式 | Mac 的界面外框跟随系统深色，卡片保持浅色纸张感；Windows 的外框目前只有浅色，要不要也加深色是另一项工作 |

---

## 附录 A：文件迁移清单

| 现有文件 | 去向 | 类型 | 要点 |
|---|---|---|---|
| `src/Program.cs` | 留在 `src/`；Mac 另写 `mac/Program.cs` | 各平台自己实现 | 52-65 行进入 `Cli.Main`；kernel32 的控制台调用和命名 Event 在 macOS 上会抛异常，所以 Mac 另写 |
| `src/Core/Db.cs` | `shared/core` | 共享 + 小钩子 | 库名解析器；DllImport 程序集特性移到 `src/WinAssembly.cs` |
| `src/Core/Store.cs` | `shared/core` | 共享 + 小钩子 | `AppPaths.DataHome` |
| `src/Core/Card.cs` | `shared/core` | 共享 + 小钩子 | `PathSyntax` 按系统区分 |
| `src/Core/When.cs`、`Markdown.cs` | `shared/core` | 原样共享 | — |
| `src/Core/Cli.cs` | `shared/core` | 共享 + 小钩子 | `Main`、`IsCliInvocation`、`Help(CliPlatform)`，两处输出修正 |
| `src/Core/Fetch.cs` | `shared/core` + `EdgeRenderer.Windows.cs` / `WebKitRenderer` | 共享 + 小钩子 | 约 555/594 行原样；浏览器部分换成注入的渲染器 |
| `src/Core/Llm.cs` | `shared/core` + `ToolHost.*` | 共享 + 小钩子 | 工具表和流程共享；命令拼装交给 IToolHost；修 L197 |
| `src/Core/Proc.cs` | `shared/core` + `Reaper.*` | 共享 + 小钩子 | Job 对象换成 IChildReaper |
| `src/UI/Layout.cs` | `shared/shell` | 原样共享 | 并入 Trim、Spot、HubRect、ScreenOf 等纯函数 |
| `src/UI/Desk.cs` | 约 1250 行进 `shared/shell/DeskCore*`，其余进 `src/UI/Win/*` | 逻辑与视图拆开 | 唯一修改入口、布局、节拍、提醒、菜单内容都进入共享层 |
| `src/UI/CardWindow.xaml(.cs)` | 显示规则进 `CardPresenter`/`EditorOps`/`ResizeGeometry`；XAML 和 WndProc 留下 | 逻辑与视图拆开 | 尺寸常量进 Metrics；各部分的顺序和文案进 CardScene |
| `src/UI/MarkdownView.cs` | 样式决定进 `MdRender`；TextBlock 构建留下 | 逻辑与视图拆开 | 缩进、段间距、字号、任务框都共享 |
| `src/UI/HubWindow.cs` | 几何进 `Layout.HubRect`，状态进 `HubModel`；窗口留下 | 逻辑与视图拆开 | 角标优先级、吸附到角、全屏时隐藏但保留占位 |
| `src/UI/Flyout.cs` | 选择器模型进共享层；窗口留下 | 逻辑与视图拆开 | Mac 用 NSPopover |
| `src/UI/ManageWindow.cs` | 进 `LibraryModel` / `SettingsSchema` / `RecorderRules`；窗口留下 | 逻辑与视图拆开 | Mac 拆成设置和卡片库两个窗口 |
| `src/UI/Palette.cs` | 颜色数据进 `shared/shell/Data`；Brush 转换留下 | 共享 + 小钩子 | Mac 用 NSColor(srgbRed:…) |
| `src/UI/ClipData.cs` | 判断规则进 `ClipIngest`；IDataObject 读取留下 | 各平台自己实现 | 统一输出 ClipPayload DTO |
| `src/UI/Hotkeys.cs` | 动作表、冲突表、手势语法进共享层；RegisterHotKey 和钩子留下 | 各平台自己实现 | Mac 用 Carbon |
| `src/UI/Native.cs` | `Px`、`Screen` 原样进共享层（`Device` 改名 `AltId`）；`Screens.Of` 改写成纯函数 `ScreenOf(screens, rect)` 进共享层；`Screens.All/At/UnderCursor` 等 Win32 枚举留在 WinScreens | 各平台自己实现 | 测试项目不再编译这个文件 |
| `src/UI/Toast.cs`、`AuxWindow.cs`、`Theme.xaml` | 留下 | 纯视图 | 视觉尺寸只作为参考 |
| `src/app.manifest` | 留下 | Windows 专有 | 内容不变；tests 不再引用 |
| `src/UI/App.cs` | 留下；`PINCC_SCENARIO` 由 Program.cs 在拿 mutex 之前分流进来 | 各平台自己实现 | 启动和关闭的顺序由共享代码约定 |
| `tests/*` | net10.0，导入 shared props | 共享 | Windows/WSL 专有的用例加 IsWindows 条件：`FetchTests.cs:118-161、187-198`，`tests/Program.cs:153-155、174-176`；另加 Mac 对应的用例 |
| `README.md`（“目录”“构建”“诊断”几节）、`docs/DESIGN.md` | 留下 | 文档 | `src/Core/` 改为 `shared/core/`，增加 `shared/shell/`、`mac/`、`src/UI/Win/`；增加 macOS 一节和“平台适配”一节 |

## 附录 B：Windows API → macOS API 对照

| 用途 | Windows | macOS |
|---|---|---|
| SQLite | winsqlite3.dll | /usr/lib/libsqlite3.dylib |
| 单实例 / “显示全部”信号 | 命名 Mutex / EventWaitHandle | LaunchServices 单实例 + applicationShouldHandleReopen；flock + NSDistributedNotificationCenter（deliverImmediately） |
| 控制台 | GetStdHandle / AttachConsole / WriteConsoleW | 直接继承 stdout，始终 UTF-8 |
| 开机/登录启动 | HKCU\…\Run | SMAppService.mainApp |
| 加入 PATH | HKCU\Environment + WM_SETTINGCHANGE | 把 `pincc` 垫片链接到 ~/.local/bin 或 /usr/local/bin |
| 显示器 | EnumDisplayMonitors / GetMonitorInfo / GetDpiForMonitor | NSScreen.screens / visibleFrame / backingScaleFactor / CGDisplayCreateUUIDFromDisplayID |
| 批量移动窗口 | DeferWindowPos + 跨 DPI 两步放置 | 一个 CATransaction 里批量 setFrame |
| 置顶 / 置前 / 退后 | HWND_TOPMOST / SWP_NOACTIVATE / Behind | level .floating / orderFrontRegardless / order(.below) |
| 不抢焦点的窗口 | + 按钮和提示条：WS_EX_NOACTIVATE / MA_NOACTIVATE（卡片被点击后会激活） | NSPanel .nonactivatingPanel + acceptsFirstMouse；卡片能成为 key，+ 按钮和提示条不能 |
| 不出现在切换列表 | WS_EX_TOOLWINDOW | LSUIElement + .ignoresCycle |
| 缩放边框 | WM_NCHITTEST / WM_SIZING | 手写跟踪循环 + 共享的 ResizeGeometry / KeepAspect |
| 移动结束 | WM_EXITSIZEMOVE | 手写循环里的 mouseUp |
| 所属弹窗保持编辑状态 | WM_ACTIVATE + GW_OWNER | windowDidResignKey + 检查新的 keyWindow 是否属于 popover |
| 系统事件 | WM_DISPLAYCHANGE / WM_TIMECHANGE / WM_POWERBROADCAST | didChangeScreenParameters / NSSystemClockDidChange / NSWorkspace didWake |
| 全局热键 | RegisterHotKey（1409 表示被占用） | Carbon RegisterEventHotKey（-9878 表示被占用） |
| 录入快捷键 | WH_KEYBOARD_LL 钩子 | 第一响应者视图 + 本地事件监听 + CopySymbolicHotKeys |
| 全屏检测 | SHQueryUserNotificationState + 前台窗口判断 | 桌面空间 + CGWindowListCopyWindowInfo 窗口边界 |
| 剪贴板 | IDataObject / CF_HTML / DIB | NSPasteboard（fileURL、public.url(-name)、org.chromium.source-url、png/tiff/heic） |
| 拖出文件 | DragDrop.DoDragDrop FileDrop | NSDraggingSession + 文件 URL |
| 打开 / 在文件管理器中显示 | ShellExecute / explorer /select | NSWorkspace.open / activateFileViewerSelecting |
| 提示音 | PlaySound “Notification.Reminder” | Glass.aiff 用 AudioServicesPlayAlertSound 播放 / NSSound.beep |
| 图片尺寸和解码 | BitmapDecoder / DecodePixelWidth | CGImageSource 属性 / 缩略图（WithTransform） |
| 离屏截图（诊断） | RenderTargetBitmap | cacheDisplay(in:to:) |
| 无头渲染 | msedge --headless=new --dump-dom | 屏幕外 WKWebView（nonPersistent） |
| 子进程树 | Job 对象 KILL_ON_JOB_CLOSE | 第一版：记录子进程，超时 `Kill(entireProcessTree: true)`，退出时 KillAll；孤儿进程测试失败才改用 posix_spawn + POSIX_SPAWN_SETPGROUP + killpg（.NET 10 的 Process 不能设置进程组） |
| 工具的 PATH | 注册表 Path + WSL 登录 shell | 用户登录 shell 的环境（zsh -l -i） |

## 附录 C：已查证的关键事实（2026-09）

| 事实 | 来源 |
|---|---|
| .NET 10 通过 `macos` 工作负载（net10.0-macos）支持原生 AppKit；最新服务版本要求 Xcode 26.6，而 Xcode 26.6 要求 macOS 26.2+ | github.com/dotnet/macios releases、.NET 10 release notes |
| net10.0-macos 只能在 Mac 上构建 | dotnet/macios `docs/building-apps/build-properties.md` |
| net10.0-macos 运行在 CoreCLR 上；NativeAOT 受支持，但不能从 Windows 交叉编译 | devblogs “MAUI moves to CoreCLR”；dotnet/macios `docs/nativeaot.md`；learn.microsoft.com native-aot cross-compile |
| Release 构建默认生成 osx-x64 + osx-arm64 通用包 | dotnet/macios #26636、#25675 |
| Apple 平台 SDK 的支持期跟随 MAUI 政策：net10.0-macos 到 2027-05-11 | dotnet.microsoft.com MAUI 支持政策 |
| macOS 27 于 2026-09-14 发布，只支持 Apple 芯片；macOS 26 是 Intel 的最后一版 | support.apple.com/122867、MacRumors |
| .NET 在 macOS 上会尝试 `libsqlite3.dylib` 等几种名字变体；DefaultDllImportSearchPaths 在非 Windows 上无效；更稳的做法是用解析器指向绝对路径 | learn.microsoft.com native-library-loading |
| Avalonia 在 Mac 上没有 nonactivatingPanel 和 collectionBehavior；#15077 拼音问题、#22273 未激活窗口的输入法问题都未修复 | Avalonia 源码 native/Avalonia.Native/src/OSX；GitHub issues |
| 非激活面板的标志必须在创建时设定，之后修改不会生效；NSPanel 的 hidesOnDeactivate 默认为 true；无边框窗口默认不能成为 key | Apple NSPanel / NSWindow 文档；philz.blog |
| 需要 `.activeAlways` 跟踪区域和 acceptsFirstMouse，程序不在前台时悬停和第一次点击才有效；`.activeAlways` 不支持 `.cursorUpdate` | Apple NSTrackingArea / NSView 文档；NSTrackingArea.h；Apple Developer Forums 738051 |
| `becomesKeyOnlyIfNeeded = true` 时，只有点到需要键盘的视图，面板才成为 key | Apple NSPanel.becomesKeyOnlyIfNeeded 文档 |
| `resignKey()` 是通知方法，不应直接调用；Spotlight 类面板靠关闭面板交还焦点 | Apple NSWindow 文档；multi.app 关于命令面板激活行为的文章 |
| 程序不在前台时，AppKit 会暂停分布式通知的投递，除非使用 deliverImmediately | Apple《Notification Programming Topics》 |
| `NSSound.play()` 走主音量，`AudioServicesPlayAlertSound` 走提醒设备和提醒音量 | Rogue Amoeba《Playing Sound Effects the Right Way》 |
| `open -W` 等待程序退出，`--env` 给启动的程序设置环境变量 | open(1) man page |
| 在 ssh 会话里签名需要先解锁钥匙串，否则报 errSecInternalComponent | Apple Developer Forums 768354、712005 |
| ad-hoc 签名的指定要求就是 cdhash，每次构建都会变，TCC 授权随之失效 | YARG#1695 等开源项目的 issue |
| Carbon RegisterEventHotKey 仍可用，不需要权限；macOS 15.0 曾拒绝只用 Option 的组合，15.2 放宽 | sindresorhus/KeyboardShortcuts；Apple Developer Forums 763878 |
| addGlobalMonitorForEvents 需要“辅助功能”权限，CGEventTap 需要“输入监控”权限 | Apple Developer Forums 707680 |
| agent 程序没有主菜单时，⌘C/⌘V/⌘Z 在文本框里无效 | 多个开源 agent 程序的 issue |
| SMAppService.mainApp（macOS 13+），状态可能是 requiresApproval | Apple ServiceManagement 文档 |
| App Nap 会降低后台程序定时器的频率；didWake 必须在 NSWorkspace 的通知中心监听 | Apple Energy Guide、NSWorkspace 文档 |
| 从访达启动的程序只有 `/usr/bin:/bin:/usr/sbin:/sbin`；Homebrew 在 Apple 芯片上位于 /opt/homebrew | Homebrew FAQ、Claude Code setup 文档 |
| App Sandbox 会被子进程继承，Mac App Store 要求沙盒 | App Review Guidelines、Apple Developer Forums |
| TCC 按“负责的父程序”计算子进程的文件访问；拖入文件属于隐含同意 | Eclectic Light “Permissions, privacy and TCC” |
| ad-hoc 签名 + hardened runtime + bundle 里的 dylib 会因 Team ID 不匹配而启动失败 | dotnet/macios #26609 |
| JIT 版需要 `com.apple.security.cs.allow-jit`；公证需要 Developer ID、hardened runtime 和安全时间戳 | learn.microsoft.com macOS 部署与公证；Apple notarization 文档 |
| macOS 15 取消了“右键 › 打开”绕过 Gatekeeper 的方式 | mjtsai.com 2024-07-05 |
| ⌃+滚动是“缩放”辅助功能的默认手势 | AbilityNet macOS 15 说明 |
| Liquid Glass 不要用在内容层 | Apple HIG Materials |
| 系统没有公开接口能得知其他程序是否全屏或专注模式，只能用窗口列表近似判断 | Apple Developer Forums 792917、813033 |
