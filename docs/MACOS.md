# Pin Carbon Copy（PinCC）macOS 版制作计划（Swift 路线）

正式全称 **Pin Carbon Copy**；命令与产物仍用简写 **PinCC** / `pincc`（本计划中的路径、可执行文件名、互斥量名等均不因全称而改）。

本文是 macOS 版的完整制作计划，取代上一版基于 .NET（net10.0-macos）的路线（上一版保存在 git 提交 7e24435）。内容包括：技术路线、架构、两端数据怎样通用、两端逻辑怎样由机器保证一致、每个模块在 Mac 上的做法、分阶段安排、风险和待决定事项。

计划依据：
- 你在 2026-09-24 的两项决定：
  1. Mac 版只用 Swift，现有 C# 代码保证 Windows 和 Mac 的逻辑一致；
  2. “但是要保障数据可以通用”。
- 对 src/ 全部源码的逐文件梳理（约 7.5k 行 C#，其中 src/Core 约 2.7k 行）。
- 对 Swift 6.4、SwiftPM、Xcode 27、macOS 27 和各类 .NET/Swift 语义差异的查证（附录 C）。
- 上一版 §8 的 AppKit 实现细节。这部分经过多轮核对，与语言无关，本文 §8 沿用并改写为 Swift。
- 一份架构设计和两份独立评审。评审共提出 48 条意见，逐条处理的结果见附录 E.1–E.4。
- 第二轮核对：5 名核对者对照源码和外部资料提出 92 条意见，处理结果见附录 E.5。

上一版（.NET 路线）的计划保存在 git 提交 7e24435（“add mac plan”）里。本文提交后会替换 docs/MACOS.md，所以下文的“上一版 §x”一律指 `git show 7e24435:docs/MACOS.md` 里的章节，不指本文。基线 B0 以开始 P1 时的 HEAD 为准。

---

## 0. 摘要

- **目标**：Mac 版功能与 Windows 版相同。两端的**内容、布局、数据格式、CLI** 统一；**风格、快捷键、设置习惯和运行效率**各自符合平台要求。
- **路线**：Mac 版 100% 用 Swift 编写，包括 AppKit、Carbon、WebKit，以及通过 SwiftPM 系统库目标调用的系统 SQLite。Mac 上既不运行也不安装任何 .NET。现有的 C# 程序继续作为 Windows 版，同时是所有规则的**唯一规范**（下文称“基准实现”，oracle）。
- **一致性由机器保证，不靠移植时的细心**：
  1. 常量、文案、数据表只在 C# 里写一次，由生成器导出给 Swift（§5.1）。
  2. 每个纯函数都有 C# 生成的测试向量，Swift 必须逐项相同（§5.3）。
  3. .NET 的字符分类、大小写、JSON 转义、数字格式、代码页都导出成表，Swift 按表实现，不用 ICU 或 Foundation 的同名功能（§5.2）。
  4. 有状态的存储规则用同一种“操作脚本”在两端各跑一遍，每一步都比较完整的数据库转储（§5.5）。
  5. 两端的数据目录在 WSL 里交替读写，并在两台 Mac 上用 APFS 验证（§5.6、§5.7）。
  6. 布局和卡片内容的向量来自真实的 WPF 代码，全程不显示窗口（§5.9）。
  7. 移植台账记录每个 C# 成员和对应 Swift 代码的哈希，有任何一方变了而另一方没跟上，Mac 版就不允许构建；Windows 版从不被卡住（§5.11）。
- **数据通用**（§4）：
  - L1（必须做到）：任一端建的数据目录（pincc.db、img/、files/、backup/）在另一端能打开、迁移、列出、编辑、撤销和清理；CLI 在两端对两端的数据行为相同。
  - L2（推荐，由你确认）：先做 `export` / `import` 两个命令，用一个 SQLite 文件在两台电脑之间搬数据；以后再考虑合并导入或经同步文件夹的持续同步。运行中的数据库永远不放进云同步文件夹。
- **Windows 端的改动**：测试基础设施（W0）；一个生产环境下为恒等的时钟接缝（W1）；一组只在边界情况下改变输出的确定性修正（W2，含 §12 第 7 条的图片行和命令名常量）；少数需要你确认的数据格式修正（W3）；分文件、先录特征再搬的纯函数抽取，以及 LayoutPass 的影子模式和切换（W4a、W4b）；真实使用轨迹录制器（W4c）；按 Id 排序这一声明的行为变化（W4d）；`export`、`import` 两个新命令和按数据目录的 GUI 互斥量（W5）。每一处都要先证明行为不变，或只在声明的地方变化（§3.5）。
- **工作量**：约 131 个工作日（区间 118–150），含 P0 的 5 天和约 15% 预留。P0 与 P1 并行，所以关键路径约 126 个工作日。接受 §10.3 的三项裁剪后工作量约 122 天（关键路径约 117 天）。另有日历上的试用期（Windows 发布后的试用、LayoutPass 影子模式 10 天）和你本人在 Mac 前的手工检查（§10.2），与后续阶段重叠进行。
- **最大的三个风险**：
  1. 中文输入法和焦点在“不激活程序的面板”里的表现（P0 实测，失败有方案 B）；
  2. 桌面层有状态的编排规则悄悄走样（§5.10 用抽取的决策函数、转移表、真实使用轨迹三重覆盖）；
  3. 工作量。本计划比上一版（48 天）多出一倍多，原因是逻辑要写第二份，而且要用机器证明两份相同。

---

## 1. 目标与约束

### 1.1 什么统一，什么各自实现

| 两端统一（同一份规则或同一份数据） | 各自实现（按平台习惯） |
|---|---|
| 卡片显示什么：各部分的顺序、标题、摘要、状态行、按钮、文案 | 字体、图标、圆角、阴影、深色模式、材质效果 |
| 卡片放在哪里：平铺、避让、胶囊、隐藏、+N、推开重叠卡片 | 窗口类型（WPF 分层窗口 / NSPanel）、窗口层级、桌面空间 |
| 行为规则：唯一修改入口、撤销分组、提醒只响一次、编辑 / 撤销条 / 临时展开的状态切换 | 快捷键（Ctrl ↔ ⌘）、菜单写法、设置窗口的形式、开机或登录启动的方式 |
| **数据**：同一个数据目录格式，任一端写的数据另一端都能用（§4） | 数据目录的默认位置、日志和缓存目录 |
| CLI：同一组命令、参数、JSON 字节、`--md` 输出、退出码 0–4 | 命令名（`PinCC.exe` / `pincc`）、安装方式、帮助里的平台段落 |
| LLM：工具表、提示词、隐私守卫、摘要过时规则 | 工具查找方式（注册表 PATH + WSL / 登录 shell 环境）、子进程清理方式 |
| 抓取：编码识别、正文提取、Office 文本 | HTTP 实现（HttpClient / URLSession）、无头渲染（Edge / WebKit） |
| 防抖和批处理规则、1 秒评估网格 | 定时器实现（Windows DispatcherTimer；Mac 带误差的节拍加到点唤醒） |

### 1.2 “C# 是规范”具体指什么

- **规则只有一个来源。** 任何规则（数据怎么写、卡片怎么显示、时间怎么解析、布局怎么排）以 src/ 里的 C# 代码为准。Swift 代码是它的第二份实现。
- **Swift 只和 C# 的输出比较，不和文字描述比较。** 所有测试的期望值都由 C# 代码生成（测试向量、操作轨迹、CLI 录制、布局和场景结果）。没有一份手写的“规格说明”能单独决定 Swift 的行为。
- **C# 的毛病也照搬。** Swift 发现 C# 的问题时，只有两条路：先在 C# 里修（走 §5.15 的变更流程，Windows 先发布），或者在台账里记为“照搬”（replicate）。Swift 不能自己“修好”一个行为，否则两端就不一致了。
- **新规则先写 C#。** 两端共同的新功能永远是 C# 先实现，再生成向量，再移植到 Swift。
- **允许的差异只有一张表。** 平台差异（快捷键、文案里的平台用词、设置里的平台项、HTTP 实现、字体实测高度等）都登记在 §1.4 和 contracts/tables/mac-*.json 里，由测试检查。§1.4 的每一行在 `contracts/tables/mac-diffs.json` 里有一个 id；向量、场景和轨迹里标为“预期差异”的地方只能引用这些 id，引用未登记的 id 测试失败。表外的任何差异都算缺陷。

### 1.3 硬约束

- **Windows 版每天在用，不能出问题。** `src/PinCC.csproj` 的路径、程序集名、单文件发布方式和 `build.cmd` 的产物都不变。每一处 src/ 改动都要先证明行为不变，或只在事先声明的地方变化（§3.5、§5.14）。
- **`src/` 不移动，`.probe/` 不碰。** `.probe/*.sh` 写死了 `src/bin/Debug/net10.0-windows/PinCC.exe`。`.probe/ui.sh` 会在 `.probe/` 下删建目录，所以新的自动检查一律不调用它，改用 tools/ 下自己的脚本（§5.14）。
- **自动界面检查只在屏幕外渲染**，不在桌面上弹窗，也不向正在运行的实例发任何信号。发布前由你把正在运行的 `dist\standalone\PinCC.exe` 改名为 `.old`。
- **零第三方依赖。** C# 侧不写 PackageReference；Swift 侧不引入任何第三方 SwiftPM 包（SHA-1、ZIP、JSON 写入器都自己写）。系统库（SQLite、zlib）和工具链自带的 Swift Testing / XCTest 不算。
- **.NET 只作为 Windows 上的开发和测试工具**，由 WSL 调用 Windows 版 dotnet.exe（SDK 10.0.401）。两台 Mac 都不装 .NET SDK，原因见 §9.1。
- **开发环境**：Windows + WSL（Ubuntu 24.04，x86_64；目前没有 Swift）；一台 Apple 芯片 Mac 和一台 Intel Mac。macOS 27 已于 2026-09-14 发布，只支持 Apple 芯片；macOS 26 是 Intel 的最后一版。

### 1.4 声明的差异（除此之外都必须相同）

| 差异 | 范围 | 为什么允许 | 怎样约束 |
|---|---|---|---|
| 字体实测的文字高度 | 自适应高度卡片的原始文字高度 | 两端字体不同 | 只有原始高度允许差一行；之后的取整、夹紧、滞后等处理按精确函数比较（§5.9）；会因差一行而改变结果的布局场景单独列出 |
| 操作系统的错误文本 | `meta.Error`、CLI 错误信息里 .NET / SQLite / 系统给出的部分 | 来自系统，无法统一 | 存储为不透明文本；CLI 只比较“错误：”前缀和错误类别 |
| 无头渲染得到的 DOM | Edge 与 WebKit 渲染后提取的正文 | 浏览器不同 | 只在正文过少、走无头渲染时出现；只影响重新读取后的 TextHash |
| HTTP 细节 | 重定向、解压、超时的实现 | HttpClient 与 URLSession 不同 | 可观察的决定（跳转次数、降级拒绝、上限、超时文案“超时”）按 C# 规则实现，用回环测试服务器验证 |
| 快捷键、菜单平台项、平台用词、设置平台项 | KeyBindings、MenuSpec 平台行、PlatformText、SettingsSchema 作用域 | 平台习惯 | 表完整性测试（§5.1） |
| 帮助里的平台段落 | `Cli.Help` 的命名平台片段 | 命令名和 shell 说明不同 | 其余部分逐字节相同（§5.8） |
| 单独的 UTF-16 代理项 | 含孤立代理项的文本 | Swift 的 String 不能表示 | 两端都经 SQLite 的 UTF-16 接口读写，SQLite 做同样的转换（§6）；另有向量记录行为。进入 CLI 字节输出的截断（Cli.cs:252）不算差异，Swift 照搬 |
| 只影响显示的截断 | 完成条标题 14 单元（Desk.cs:490）、摘要失败信息 120 单元（Desk.cs:1243）、卡片库历史预览 40 单元（ManageWindow.cs:145）、编辑器外的 12,000 单元（CardWindow.xaml.cs:332） | 切在代理对中间时 Swift 无法表示孤立代理项 | 只影响屏幕上的文字，不写库、不进 CLI |
| GB18030 以外的非 UTF 编码 | 旧网页、旧文本文件的 big5、shift_jis 等 | 见 §10.3 裁剪项 b | 若采用裁剪项 b，登记为差异；否则按 .NET 表实现，没有差异 |
| 下载文档的 TextHash | 正文过少、改为下载的链接卡片重新读取后的 `meta.TextHash` 和 CLI `input_hash`（Desk.cs:1100） | 哈希输入是本机数据目录下的绝对路径（照搬，§3.5.2） | 只在本机重新读取时产生；向量和 CLI 录制把这类卡片的哈希当不透明值 |
| 批量命令的记录顺序、重叠自由卡片的推开结果 | W4d 落地（P12）之前 | Windows 按 `_cards` 的枚举顺序，Swift 按 Id | 重放时用轨迹里录下的顺序作为输入（§5.10）；W4d 之后删除这一行 |
| 来源网址 | 剪贴板和拖入的 sourceUrl | 两端剪贴板格式不同，尽力而为（§8.8） | 只影响新卡片的来源行 |
| HEIC / HEIF / AVIF | 拖入或粘贴这几种图片 | Mac 转码为 PNG/JPEG 后建图片卡片；Windows 的 ClipData.cs:12 不认这些扩展名，同样的文件成为文件卡片 | ClipIngest 向量分别记录两端的结果 |
| Mac 的包目录 | `.app`、`.rtfd`、iWork 文档 | Mac 当文件处理；Windows 的 Fetch.cs:341-346 把目录当文件夹 | 只在 Mac 本机建卡时出现；这类卡片在 Windows 上是异机路径，不读取 |
| 编辑器输入的 U+2028 / U+2029 | Mac 编辑器 | Mac 输入时换成 `\n`（§8.3），C# 没有这一步 | 只影响 Mac 上新输入的文字 |
| 回车插入的行尾 | 编辑器里按回车 | WPF 插入 CRLF（CardWindow.xaml:20）；Mac 的处理由 §12 第 14 条决定 | 决定之前两端向量都用 WPF 形状的文本。选“照搬”时 Mac 也插入 CRLF，删除这一行；选“在 C# 里修”（W2m）时差异只留在编辑中的缓冲区，保存时两端都换成 LF（CardWindow.xaml.cs:711、735），EditorOps 向量分别用 LF 和 CRLF 两种缓冲区生成，Mac 两组都要通过（经 CLI 写入的 CRLF 正文在 Mac 上同样原样载入） |
| LLM 工作目录的“/”捷径 | Llm.cs:197 | Windows 上以 `/` 开头的是 WSL 路径；Mac 的路径都以 `/` 开头，不照搬 | Mac 专有的解析器测试 |
| 全屏窗口 | 全屏应用在前时 | Windows 把卡片退到全屏窗口后面（Desk.cs:961-971）；Mac 的全屏应用在单独的桌面空间，不需要处理（§8.11） | 只影响层级，不写库 |
| “需要授权”状态 | Mac 的 TCC 拒绝访问 | 只在 Mac 出现的运行时状态，不写 meta（§8.13） | DATA.md 登记 |
| 子进程清理 | LLM 子进程树 | Windows 用 Job 对象；Mac 用 killpg 近似，调用 setsid 的孙进程收不到 | Proc 契约测试 |
| 历史日期的时区 | 早于向量网格的日期 | Windows 与 IANA 的时区数据不同 | 向量只取两套数据一致的日期，DATA.md 登记 |
| 裁剪项 a、c | 夏令时回拨那一小时的提醒；HtmlExtract 的 Text | 见 §10.3 | 只在采用时登记 |

---

## 2. 路线与取舍

### 2.1 为什么只剩 Swift + AppKit

2026-09 对 25 种方案做过一次打分（权重：一致 0.20、原生 0.20、效率 0.15、工作量 0.15、风险 0.15、长期维护 0.15；每个方案的关键结论另有一名评审尝试反驳）。前几名：

| 方案 | 总分 | 估算天数 | 是否满足“Mac 上不用 .NET” |
|---|---|---|---|
| Swift/AppKit 界面 + NativeAOT 编出的 C# 动态库 | 7.3 | 60 | 否，仍是 .NET 10 |
| Swift/AppKit 界面 + C# 后台子进程（stdio JSON） | 7.2 | 60 | 否 |
| 上一版计划：共享 C# + net10.0-macos | 7.2 | 48 | 否 |
| **原生 Swift + AppKit，Windows C# 作为基准，靠跨语言测试对齐** | **7.1** | **72**（复核后 66–78） | **是** |
| 纯 net10.0 + 手写 objc_msgSend | 6.9 | 67 | 否 |
| Rust core 同时用于两端 + Swift 外壳 | 6.7 | 84 | 是，但要重写 Windows 的 core |
| Tauri、Electron、Wails、Flutter、Compose 等 | 3.1–4.2 | 62–125 | 部分是，但做不出不激活的浮动面板，或每张卡片带一份很重的运行时 |

那次评审的结论是：如果反对的是 .NET 本身，唯一可信的答案就是原生 Swift + AppKit。你的决定正是这一条，所以本文不再比较其他路线。

被否决的方向和原因：
- 所有仍带 .NET 的方案（NativeAOT 动态库、后台子进程、objc_msgSend、Avalonia、Uno）：违反“Mac 上不用 .NET”。
- Rust 共享 core：要重写每天在用的 Windows core，违反“Windows 不能出问题”。
- Swift core 以 DLL 形式给 WPF 调用（SE-0495 `@c` + Windows 版 Swift）：同样要改写 Windows 的逻辑层，且 Windows 版 Swift 工具链还是实验性质。
- Web 外壳（Tauri、Electron、Wails）：macOS 27 上已有面板点击会激活程序的回归（Electron #53889；Tauri 有类似报告，尚未定论，tauri-nspanel #123），每张卡片一个网页视图也太重。
- 两端都换成一个新界面库（Qt、Flutter、Compose）：Windows 版要整体重写。

### 2.2 这条路线的代价，以及本文怎样应对

| 代价 | 应对 |
|---|---|
| 约 5k 行细致逻辑写两份（撤销、时间解析、Markdown、抓取和隐私守卫、LLM 工具表、桌面编排） | §5 的一致性体系：向量、差分脚本、交替读写、台账。Swift 只和 C# 的输出比，不和人的理解比 |
| 有状态的桌面规则（撤销分组、编辑与 CLI 合并、提醒只响一次、读取竞争）最容易悄悄走样 | 决策函数抽成纯函数并生成向量；逐条转移表 + FakeHost 场景；影子期间录制你真实使用的轨迹并在 Swift 上重放（§5.10） |
| .NET 的字符串、正则、JSON、时间、取整语义与 Swift 不同 | DotNetCompat 模块按 .NET 导出的表实现，lint 禁止同名但语义不同的 Swift API（§5.2、§5.13、§6） |
| 以后每次逻辑改动都要做两遍 | 台账和一条命令重新生成向量，把 Swift 这边变成机械跟进；只卡 Mac 版的发布，不卡 Windows（§5.15）。实际成本约为只改 Windows 的 1.5–2 倍 |
| 工作量从 48 天增加到约 131 天（关键路径约 126 天） | 这是评审复核、再经第二轮核对补齐后的数字（§10）。§10.3 列出可以由你决定的裁剪 |

与打分表里的方案 (a) 相比，本文的变化是：从“只靠 golden”改成“由机器证明两份实现相同”（表驱动的 .NET 语义、差分脚本、交替读写、台账、数据字典），并把数据通用作为硬要求。所以天数也从 66–78 增加到约 131。

---

## 3. 总体架构

### 3.1 目录结构

```text
PinCC/
  build.cmd                 不变：dotnet run --project tests，然后两次 dotnet publish src（§5.16）
  .gitattributes            新增（W2）：*.cs text eol=lf；contracts/**/*.jsonl text eol=lf；*.gz binary
  src/                      Windows 程序，路径不变；只有 §3.5 列出的改动
  tests/                    现有测试项目（net10.0-windows，UseWPF），新增：
    Vectors/                向量生成器、.NET 基础语义表、数据表导出（Tables.cs）、正则清单、覆盖率统计
    Ledger/                 C# 成员扫描器（按词法处理字符串）和台账检查
    Oracle/                 ScriptDriver（操作脚本）、CliHarness（CLI 录制）、DataCompat（互开检查）、RealDataCensus
  oracle/wpf/               PinCC.OracleWpf.csproj：只用于测试的 WPF 基准，放在 tests/ 之外（§5.9），不进 build.cmd
  contracts/                语言无关的契约，C# 写、Swift 读，全部提交进 git
    DATA.md                 数据字典（附录 D 是它的初稿）
    settings-schema.json    每个设置键的作用域、取值语法、默认值、是否敏感
    tables/*.json、schema.sql  由 C# 导出的数据表和建表语句；mac-*.json 是 Mac 专有的平台行，mac-diffs.json 是 §1.4 各行的 id
    unicode/*.bin.b64       .NET 字符分类、大小写、JSON 转义、代码页表
    vectors/*.jsonl         纯函数测试向量（不压缩，每族约 5k 行）；deep/ 下只提交种子和哈希
    scripts/*.json          操作脚本；traces/ 下是 C# 轨迹，以及 W4c 真实轨迹去掉正文后的派生版本（§5.10）
    cli/*.json              CLI 录制
    fixtures/win-*/ mac-*/  两端各自生成的数据目录样例
    layout/ scenes/ md/ menus/   布局、卡片场景、Markdown、菜单向量
    desk-transitions.md desk-commands.json   桌面命令转移表
    ledger.json             移植台账
  mac/
    Package.swift
    Sources/                各目标见 §3.2
    Tests/
    scripts/bundle.sh       把 SwiftPM 产物组装成 PinCC.app 并签名
    Info.plist  PinCC.icns（预先生成，不需要 actool）
  tools/
    check.sh                WSL 里的总检查：C# 测试 + 向量新鲜度 + 台账 + swift test + lint + 交替读写
    sync.sh                 C# 改动后：重新生成指定的向量族、报告过期的台账条目和受影响的 Swift 标记
    gen-swift-tables.sh     由 contracts/tables/*.json 生成 mac/Sources/*/Generated/*.swift
    mac.sh                  ssh 到指定的 Mac：test [--intel] | fixtures | app | verify
    interleave.sh           C# 与 Swift 在同一数据目录上交替执行
    ui-snap.sh              Windows 屏幕外快照（替代 .probe/ui.sh，§5.14）
    replay-trace.sh         把 Windows 上录的 PINCC_TRACE 轨迹喂给 Swift 的 DeskCore
  docs/DESIGN.md（增加“平台差异”一节，由表生成）  docs/MACOS.md（本文）  docs/mac-port.md（P0 实测记录）
  docs/DATA-MOVE.md         给你看的数据搬迁说明（P11）
  artifacts/                基线、Mac 结果、真实数据副本和 W4c 的完整轨迹（含正文和起始快照），加进 .gitignore，从不提交
  .probe/                   不动
```

### 3.2 Swift 包的目标

`swift-tools-version` 取 §9.2 定下的版本；语言模式 Swift 6；`platforms: [.macOS(.v14)]`（Intel Mac 跑不了 Sonoma 时按 §9.1 的表改为 `.v13` 或 `.v12`；12 是 Xcode 27 能向下部署的下限，SMAppService 要求 13，所以 12 上要加 `#available` 退路）。

**可移植目标**：只用 Foundation 和标准库，在 WSL、两台 Mac 上都能编译和测试。平台差异只允许出现在两处：一行 `#if canImport(Glibc)` 的 import，以及一个 `Posix.swift` 垫片文件。垫片集中处理 Darwin 与 Glibc 写法不同的地方，例如 posix_spawn 的属性和文件动作类型（Darwin 上是可选指针，Glibc 上是结构体）、`stat` 的修改时间字段（`st_mtimespec` / `st_mtim`）。lint 不允许这两处之外出现 `#if`。

| 目标 | 内容 |
|---|---|
| `CSQLite`、`CZlib` | `.systemLibrary`，module map 通过 shim.h `#include <sqlite3.h>` / `<zlib.h>`，Linux 和 macOS 用同一个模块，不用 `import SQLite3` 和 Compression。可变参数的 C 函数包成 static inline 函数 |
| `DotNetCompat` | 按 .NET 导出的表重现 PinCC 依赖的 .NET 行为（§5.2、§6）：UTF-16 字符串操作、字符分类、不变大小写、NumberStyles 解析、银行家舍入、饱和转换、.NET 双精度文本、三种 System.Text.Json 写法和一个读取器、LocalDateTime（100 ns tick，带歧义夏令时标记）、.NET 时区规则、NetRegex（NSRegularExpression 的翻译工厂）、WinPath、NetUri、NetIP、HtmlDecode、代码页解码、SHA-1/SHA-256、有序字符串映射。表以 base64 字符串字面量嵌入，不用 `Bundle.module` |
| `PinCCCore` | Db、Store、Card、CardMeta、When、Markdown、TextDecode、HtmlExtract、JsonLd、UrlGuard、FileFacts + ZipReader、WebRoute（WebCore 路由里的纯函数部分）、Llm（纯函数部分）、Proc（posix_spawn，POSIX，可在 WSL 测）、DataRules（异机路径、设置作用域、数据特性）、Home（唯一的数据目录解析）、Bundle（export/import） |
| `PinCCCli` | 全部 CLI 命令，库 + `pincc` 可执行文件 |
| `PinCCDesk` | DeskCore（经 IClock、IScheduler、IScreens、IWindowHost、IClipboard、IShell、ISound、IFocus、IImageInfo、IHotkeys、IAutoStart 接口工作的状态机）、LayoutPass、Layout、Rules、CardPresenter → CardScene、EditorOps、EditSession、MdRender、HubModel、选择器 / 卡片库 / 设置模型、MenuSpec、KeyBindings、SettingsSchema、ClipIngest |
| `pincc-oracle` | Swift 端的脚本驱动和转储工具，供 interleave.sh 和 tools/mac.sh 使用 |
| `pincc-selftest` | 不依赖 XCTest 的自检程序：读取 contracts/，跑全部向量族、操作脚本和 CLI 录制，输出通过和失败清单。供不能编译的 Intel Mac 原生运行（§9.1） |
| 测试目标 | DotNetCompatTests、CoreVectorTests、ScriptReplayTests、CliTranscriptTests、DataCompatTests、DeskVectorTests、FakeHostScenarioTests、LedgerTests、LintTests |

**只在 Darwin 上编译的目标**：

| 目标 | 内容 |
|---|---|
| `PinCCMacPlatform` | URLSession 抓取（临时会话、重定向代理、可重置的 15 s / 60 s 总期限、与 Windows 相同的请求头）、WebKitRenderer、ImageIO（读像素尺寸、HEIC/HEIF/AVIF 转码）、登录 shell 环境捕获、DNS（getaddrinfo）、SMAppService、提示音 |
| `PinCCApp` | main.swift、AppDelegate、CardPanel、HubPanel、Toast、选择器（默认子面板，以 P0 的 A3 为准）、由 MenuSpec 生成的 NSMenu、NSStatusItem、Carbon 热键和录入、剪贴板、拖入拖出、设置与卡片库（放在程序自己的 NSWindow 里的 SwiftUI）、MacScreens（y 坐标翻转，`mac:` + 显示器 UUID） |

### 3.3 .app 的组成

```text
PinCC.app/Contents/
  Info.plist                LSUIElement、NSAllowsArbitraryLoads、bundle id、用途说明（§8.1）
  MacOS/PinCC               GUI 主程序；带 CLI 参数启动时也能直接走 CLI
  Helpers/pincc             只含 PinCCCli 的命令行程序，不链接 AppKit，冷启动快
  Resources/PinCC.icns 及其他资源（只通过 Bundle.main 读取）
```

- `pincc` 放在 `Contents/Helpers`，不放 `Contents/Resources`：Apple 的包结构规定 Resources 只放非代码资源，放可执行文件会让严格的 codesign 校验和将来的公证报错。签名顺序由内而外：先签 `Helpers/pincc`，再签整个 .app（§9.6）。
- “安装命令行工具”把 `Helpers/pincc` 链接到 `~/.local/bin` 或 `/usr/local/bin`。`pincc` 是独立的 Mach-O，通过符号链接调用不需要找 bundle，所以不再需要上一版计划里的 shell 垫片。

### 3.4 哪个进程做什么、线程怎样安排

- `MacOS/PinCC` 的 main.swift 先去掉系统注入的 `-NS*`、`-Apple*`、`-psn_*` 参数。剩下的参数若按 Program.cs:17 的规则算 CLI 调用（带任何参数，`--startup` 除外），就在进程内运行 PinCCCli 并 `exit()`，不启动 NSApplication，不闪 Dock 图标。
- `Helpers/pincc`、`MacOS/PinCC` 的 CLI 路径和 WSL 里的测试都用同一份 PinCCCli，所以 CLI 录制和交替读写能与 C# 基准并排运行。
- GUI 线程：
  - DeskCore 和所有视图都是 `@MainActor`，对应 WPF 的 Dispatcher；
  - 抓取、LLM、读文件和解码放在分离的任务里，结果回到主 actor；
  - 一个长期存在的 Db 连接，既用来写，也用来轮询 `PRAGMA data_version`；Db 用 FULLMUTEX 打开，外加一个 NSRecursiveLock，对应 C# 可重入的 Monitor；
  - 1 秒节拍放在 `.common` 运行循环模式、误差 100 ms，保持 C# 的 1 秒评估网格（同一次节拍里到期的提醒一起响一声）；另有一个对准下一个提醒时间的一次性唤醒，防止 App Nap 推迟节拍（§8.10）；
  - LayoutPass 的结果在一个关闭动画的 CATransaction 里一次应用。

### 3.5 Windows 端改动：哪些文件、为什么、怎样证明

在第一次改 src/ 之前，先录下基线 B0（§5.14）。之后每个 src/ 提交都必须重现 B0，只允许出现提交说明里列出的差异。发布到 dist 仍按现有规则：由你先把正在运行的 exe 改名为 `.old`。

| 编号 | 阶段 | 改动 | 为什么 | 证明 | 需要你确认 |
|---|---|---|---|---|---|
| W0 | P1 | 只动 tests/ 和 oracle/：向量生成器、台账、脚本驱动、CLI 录制、互开检查、真实数据普查、OracleWpf | 基准实现的基础设施 | src/ 没有任何改动 | 否 |
| W1a | P2 | `When.Clock`（`TimeProvider`，默认 `TimeProvider.System`）。`NowMs`、`LocalNow` 经过它；`Clock` 是 System 时 `LocalNow` 就是 `DateTime.Now` 本身（保留 Local 类型和歧义夏令时标记）。Core 里的调用点改走 When：Cli.cs:52、99、145、248，Store.cs:282（Purge 的文件时间下限）、291（备份日期） | 没有它，CLI、备份、清理和场景的向量都不确定 | 生产路径是恒等，可以逐行审阅；B0 不变 | 否 |
| W1b | P2 | UI 里全部 14 处 `DateTime.Now` 改为 `When.LocalNow`：App.cs:77，CardWindow.xaml.cs:437、483、528、529、633、641，Flyout.cs:94、113，ManageWindow.cs:149，Desk.cs:148、978、980、983 | 场景向量要固定时间 | 同上 | 否 |
| W1c | P2 | 把 App.cs:18-21 的语言覆盖和 Theme.xaml 合并抽成静态的 `App.Configure(Application)`，App 和 OracleWpf 都调用它 | OracleWpf 必须用与真程序相同的资源和 CJK 断行 | 逐行搬移，屏幕外快照不变 | 否 |
| W1d | P2 | C# 测试：src/Core 里除 When.cs 以外禁止 `DateTime.Now`、`DateTime.UtcNow`、`DateTimeOffset.Now/UtcNow`；W1b 之后 src/UI 禁止 `DateTime.Now` 和 `DateTimeOffset.Now`。Native.cs:46、49 的 `DateTime.UtcNow` 是屏幕缓存的 2 秒单调计时，与业务时间无关，列入白名单并注明原因（§5.9 用反射设 `_cachedAt` 的做法因此不变） | 防止新的时钟泄漏 | 测试本身 | 否 |
| W2 | P2 | 规范卫生，每项一个提交（§3.5.1），含图片行格式（l）和命令名常量（h） | 让规则与区域设置、排序实现无关，Swift 才能复现 | 你的 zh-CN 环境下所有 B0 向量不变，只有列出的边界向量变化（l 只改图片行）；真实数据普查无差异 | 是（§12 第 2、7 条；m 项见第 14 条） |
| W3 | P3 | L1 数据格式修正（§3.5.2） | 两端要共用数据 | 向量差异只在声明的情形；真实数据普查的每一处变化都要你签字 | 是（§12 第 3 条） |
| W4a | P6 | `src/UI/Rules.cs`：约 50 个纯静态函数从 Desk、CardWindow、HubWindow、Flyout、ManageWindow 移出，实例字段改为参数；`Layout.cs` 增加 `Trim`、`Spot`、`HubRect`。方法体内的字面量表（设置页选项 ManageWindow.cs:191-202、只定位不运行的扩展名 Desk.cs:765、菜单和图标 Desk.cs:1293-1351、提醒预设标签 CardWindow.xaml.cs:528-529）逐字节搬成具名静态成员。另加一个提交从 `Llm.Ask`（Llm.cs:195-217）抽出 `Llm.PreparePrompt(tool, prompt)`（gemini/qwen 的 `@` 转义、ollama 缺模型的报错）和 `Llm.Outcome(code, out, err)`（去 ANSI、Tail 300、“退出码 N，没有输出”、StripFence）。每个源文件一个提交。Rules.cs 不依赖 Desk、CardWindow 等窗口类型，加进测试项目的 Compile | 桌面规则的基准 | 移动前先录下特征输出（只调用不经过 DoLayout、不显示窗口的代码，§5.14），移动后必须重现；屏幕外快照不变 | 否 |
| W4b | P6 | `LayoutPass.Compute`（纯函数）从 Desk.cs:798-870、882-892 抽出，**影子模式**：旧的 DoLayout 仍然生效，新旧不一致时写入 `shadow.log`。LayoutPass 只依赖 Core、Layout 和 Native 里的几何类型，加进 `tests/PinCC.Tests.csproj` 的 Compile，供向量生成器调用 | 布局的基准 | 你日常使用 10 天，影子日志为零差异后再切换（P12） | 是（§12 第 6 条） |
| W4c | P6 | `PINCC_TRACE=<文件>` 录制器：在 Desk 的公开命令入口记录输入（含完整正文）、存储效果、撤销栈、布局输出、提醒批次，以及每次批量命令和每次布局时 `_cards` 的枚举顺序。开始录制时先把数据库 `VACUUM INTO` 一份起始快照。由你用 `setx PINCC_TRACE %LOCALAPPDATA%\PinCC-trace\trace.jsonl` 打开、重启 PinCC 后生效；文件不在数据目录里，不进备份和导出；按天分文件，总大小上限 50 MB；P12 之后 `setx PINCC_TRACE ""` 关闭。默认关闭 | 桌面编排的真实使用证据（§5.10） | 只记录，不改变行为 | 是（§12 第 6 条） |
| W4d | P12 | LayoutPass 的输入和批量命令（Tile、CollapseAll、PinScreen 等遍历 `_cards.Values` 的地方）改为按 Id 排序 | 让推开重叠卡片和批量操作的记录顺序可以复现，Swift 不必模拟 .NET Dictionary 的内部顺序 | 声明的行为变化，有向量；影子模式切换之后单独提交 | 是（§12 第 6 条） |
| W5 | P11 | Cli.cs 增加 `export`、`import` 两个命令（§4.4.1）；GUI 启动时另建一个按数据目录命名的互斥量 `Local\PinCC.Home.<哈希>`；`Cli.GuiRunning` 判断接缝 | L2a | 现有 CLI 录制除 `help`（Cli.cs:315-349 增加命令说明）和未知命令提示（Cli.cs:36 的命令列表）外不变，这两处变化写进提交说明；新命令有新录制 | 是（§12 第 5 条） |

除此之外 src/ 不改。Windows 界面的样式不动。Program.cs 不增加任何环境变量的读取（测试直接在进程内设置 `When.Clock` 和 `Cli.GuiRunning`，见 §5.3、§4.4.1）。唯一新增的环境变量是 W4c 录制器的 `PINCC_TRACE`，由 Desk 读取，默认关闭。

#### 3.5.1 W2 规范卫生（逐项提交）

| 项 | 位置 | 内容 | 对 zh-CN 输出的影响 |
|---|---|---|---|
| a | Markdown.cs:31、35、69、169，Cli.cs:123、209，Fetch.cs:109、124、255、272、391、475、566，Desk.cs:286，CardWindow.xaml.cs:796、937 | `StartsWith`/`EndsWith` 加 `StringComparison.Ordinal` | 只在字符串前面有可忽略字符（U+FEFF、U+200B、U+00AD 等）时变化，例如这类字符后面的 ` ``` ` 今天被当成代码块围栏，改后不是 |
| b | Store.cs:291、298、322，Cli.cs:52、107、112、239、261、265、266，Desk.cs:148、983，Flyout.cs:119-120，ManageWindow.cs:139、305，App.cs:77 | 日期格式和整数解析用 `CultureInfo.InvariantCulture` | 无（只在非公历区域或自定义时间分隔符下变化） |
| c | Card.cs:206 | `Card.Compare` 的提醒分支在 remind_at 和 created_at 都相同时再比 Id | 今天 `List.Sort` 在这种完全相同的情况下顺序不稳定 |
| d | Store.cs 备份清理 | 备份文件列表按序数排序 | 无 |
| e | Markdown.cs:203 | `int.Parse` 改为 `int.TryParse`，失败时不续列表 | 今天用全角数字的有序列表按回车会抛异常并弹错误提示 |
| f | Fetch.cs:154、355 | `Date` 截取前 10 个、`Desc` 截取前 300 个 UTF-16 单元时，不切断代理对（与 `Snippet` 的做法相同，Fetch.cs:258） | 只在切点正好落在代理对中间时变化 |
| g | Fetch.cs:392、400、401，CardWindow.xaml.cs:462、825-826，MarkdownView.cs:110 | 模式固定的内联 `Regex` 改为 `[GeneratedRegex]` | 无（同一引擎） |
| h | Cli.cs:315-349，以及 36、68、90、100、112、197、239 | `Cli.Help` 拆成 `HelpCommon` 加命名的平台片段（Cli.cs:342 的“从 WSL 调用”一行和 344-348 的结尾一段）；同时把作为命令名出现的“PinCC”（上列各行的消息和 HelpCommon 里的命令行）改为引用常量 `Cli.Cmd`，Windows 的值是“PinCC”。产品名（如 Cli.cs:52 的 `# PinCC 卡片`）不改。拼接结果逐字节不变；`Cli.Cmd` 由表导出，Mac 表里的值是“pincc” | 无 |
| i | 仓库根目录 | `.gitattributes` 固定 `*.cs` 为 LF，保证 Schema、Help、DefaultTemplate 等原始字符串的字节不随检出方式变化 | 无 |
| j（可选） | Db.cs、Cli.cs | Db 的异常带上 SQLite 结果码，CLI 按结果码判断 SQLITE_BUSY 和 SQLITE_LOCKED，不再查找 “(5)”“(6)” 子串（Cli.cs:126） | 无 |
| k | Fetch.cs:344 | 文件夹卡片的列表先按 `OrdinalIgnoreCase`（相同时再按 Ordinal）排序，再取前 40 项 | NTFS 本来大致按不分大小写的顺序返回，显示基本不变；APFS 的枚举顺序不同，不排序就无法比较 |
| l | Cli.cs:269 | `list --md` / `show --md` 的图片行从 `![](路径)` 改为 `![](<路径>)`（§12 第 7 条） | 声明的 CLI 输出变化：只有图片行变化，路径含空格时不再断开 |
| m（待定） | CardWindow.xaml.cs:762-775 | 编辑时按 LF 计算当前行（去掉行尾的 `\r` 再判断），让回车插入的 CRLF 不再阻止续列表。只在 §12 第 14 条选择“在 C# 里修”时做 | 今天在以 CRLF 结尾的行尾按回车不续列表，改后会续 |

#### 3.5.2 W3 数据格式修正（每项由你确认）

| 项 | 位置 | 内容 | 不做的话 |
|---|---|---|---|
| a | Card.cs:224 `PathRx` | 文件卡片的路径语法在两端相同：Windows 的 `X:\`、`\\server\`（与现有 PathRx 相同，不要求共享名段），加上 Mac 的 POSIX 绝对路径。建议用**固定前缀**：`/Users/`、`/Volumes/`、`/Applications/`、`/Library/`、`/System/`、`/private/`、`/opt/`。Mac 上的 `/tmp`、`/var` 实际在 `/private` 下，建卡时按真实路径写入。`/home/…`、`/mnt/…` 这类 WSL 路径在 Windows 上保持笔记，与今天相同（CLI 帮助让 LLM 工具从 WSL 调用 PinCC，钉卡脚本在路径不存在时会原样传入 POSIX 路径）。**不含 `~/`**：Mac 在建卡时就把 `~` 展开成绝对路径 | Mac 建的文件卡片在 Windows 上显示成笔记，CLI 的 kind、title、input_hash 两端不同 |
| b | Card.cs 新增 `ForeignPath(string)`；Desk.cs 新增 `LocalPath(card)` | 出入口分两层。`ForeignPath(string)` 的定义是“另一平台语法的绝对路径”：以 `file:` 开头的先取 `new Uri(x).LocalPath`（Card.cs:117 的 `file:///tmp/x.pdf` 也算）；然后在 Windows 上以 `/` 开头为异机，在 Mac 上匹配 `^[A-Za-z]:[\\/]` 或以 `\\` 开头为异机。网址、`img/…`、`#` 锚点和相对路径永远不是异机路径。`LocalPath(card)` 在内部调用它，异机路径返回 null。<br>卡片来源经 `LocalPath(card)`：Desk.cs:120、270-272（AutoSummarizes）、323、996-1011（RefreshFiles）、1062-1110（Enrich，含 1108 的 `Fetch.File`）、1172-1222（Summarize，含 1214 的 `Fetch.File`、1216 的 `File.Exists`、1219 的工作目录）、1300（菜单的定位），以及 CardWindow.xaml.cs:415（点来源标题时的 `Desk.Open(c.Src!)`）。<br>收字符串的入口自己判断 `ForeignPath`：`Desk.Open`、`Reveal`（Desk.cs:757-779）、`LoadImage`（Desk.cs:1262-1279）。`Desk.Open` 在 `file:`、`img/`、网址这几个分支（Desk.cs:762-764）之后再判断，所以网页链接和双击图片不受影响；向量里 `https://…`、`img/….png`、`#x` 都必须判为“不是异机”。它们还接收正文里的 Markdown 链接（MarkdownView.cs:111-112 → CardWindow.xaml.cs:230）、正文图片（CardWindow.xaml.cs:228）和双击图片（CardWindow.xaml.cs:876）。异机路径时 Open/Reveal 提示“文件在另一台电脑上”，LoadImage 显示占位图。CardWindow 显示“在另一台电脑上”标记。<br>源码扫描不按 `.Src` 字样判断：它列出 src/UI 里全部 `File.`、`Directory.`、`FileInfo`、`Fetch.File`、`Process.Start`、`Reveal`、`Open`、`BitmapImage.UriSource` 的调用点，每一处的路径参数必须来自 `LocalPath(...)` 的返回值或经过 `ForeignPath` 检查的变量，否则在白名单里写明来源（img/、数据目录、剪贴板文件、程序目录等）。向量增加正文链接 `[x](/Users/…)`、`![](/Users/….png)`、`[x](file:///Users/…)`。a 和 b 放在同一个提交 | .NET 在 Windows 上会把 `/Users/lpq/a.pdf` 解析成当前盘的 `C:\Users\lpq\a.pdf`。两台电脑用户名相同时，Windows 会读一个无关的本地文件，覆盖 Mac 写的 meta，把它的内容发给 LLM，或者打开错误的文件；正文里的链接和图片同样如此 |
| c | Store.cs:337 `ImgRefRx` | 图片引用的扩展名从 `\w+` 改为 `[A-Za-z0-9]+` | 引用后面紧跟中文等字母时（如 `img/….png说明`），捕获到的名字对不上真实文件，过了保留期图片会被当成无人引用而删除。这是今天就存在的隐患 |
| d | Desk.cs:672-673、711、1305，Desk.cs:599-604 | 引入“异机位置 = `Mon` 带另一平台的前缀”：在 Windows 上是以 `mac:` 开头；在 Mac 上是不以 `mac:` 开头的非空值（Windows 的 id 是设备接口路径或 `\\.\DISPLAYn`，Native.cs:92，从不以 `mac:` 开头）。平铺、菜单“回到平铺”、缩放图片只对 `Free` 且不是异机位置的卡片改 Free/Mon/X/Y；按屏幕置顶不改动异机位置的卡片。本机显示器暂时拔掉时（例如笔记本离开扩展坞），这些卡片保持今天的行为：平铺会把它们收回平铺流（Desk.cs:843 的注释） | 在 Windows 上滚一下滚轮或点“平铺显示”，就会把在 Mac 上摆好的位置清掉并写入历史 |
| e | Store.cs 打开数据库时 | 读取设置 `data.features`（逗号分隔的特性 id）。出现本版本不认识的特性时，与 `user_version` 过高一样按只读打开（Store.cs:61）。它只保护 cards、ops 和图片：`SetSetting` 不检查只读（Store.cs:325-331），只读时 Desk 和设置页照样写设置，所以设置语法不在它的保护范围内（§4.2.6） | 以后某一端改了哈希输入、哨兵值或 JSON 形状，另一端的旧版本会按旧规则读写，摘要在两台电脑之间来回变成“过时” |
| f（建议） | Card.cs:56、139，Store.cs 读取路径 | 单行解析失败（`images` 或 `meta` 不是合法 JSON、整数字段是 `1.0` 等）时，这一行被隔离：跳过并弹提示，而不是让 `Store.Active()` 整体抛异常 | Mac 端的一个写入缺陷就能让 Windows 的桌面启动失败、CLI 全部退出码 1，只能手工改 SQLite 修复 |

以下几项**从 W3 中去掉**，改为“照搬”或推迟：
- 下载文档的 TextHash 改为 `Hash("files/"+文件名)`（Desk.cs:1100）：收益很小（只影响“正文过少、改为下载”的链接卡片，而且只在重新读取时重算），却会让 Windows 上这类卡片第一次手动重读后摘要变“过时”。两端各自对本机绝对路径求哈希，登记在 §1.4 的“下载文档的 TextHash”一行。
- 预留操作名 `import`、`sync`，写 `data.last_host`，设备 id：L2a 用不到，推迟到 L2b。
- `db.id`：在第一次 `export` 时生成（W5），数据库只读时不生成；属于“本机”作用域（§4.2.4），导入不覆盖。

---

## 4. 数据通用

### 4.1 数据目录由什么组成、哪些能搬

| 路径 | 内容 | 两端通用 | 搬到另一台电脑 |
|---|---|---|---|
| `pincc.db`（+ `-wal`、`-shm`） | cards、ops、settings；WAL 模式 | 是：同一结构、同一 `user_version` | 只在所有 PinCC 进程都退出后搬，或用 `export` |
| `img/<sha1 前 20 位小写>.<扩展名>` | 卡片图片，按内容寻址 | 是 | 是，只增不删 |
| `files/` | 下载的文档，按内容寻址 | 是 | 可选 |
| `backup/pincc-YYYYMMDD.db` | 每日 `VACUUM INTO` 备份，保留 7 份 | 是：日期用不变区域的公历 | 可选 |
| `edge/`、`llm/`、`error.log`、`shadow.log`、`.instance` | 前四项是 Windows 本机的渲染配置、LLM 工作目录和日志；`.instance` 是 Mac GUI 持有 flock 的单实例锁文件 | 否 | 不搬。Mac 只在数据目录里建 `.instance`（单实例和导入判断用，§4.4.1、§8.1），不建其余几项；Mac 的缓存和日志在 `~/Library` 下 |

数据目录的位置：Windows `%LOCALAPPDATA%\PinCC`，Mac 正式版 `~/Library/Application Support/PinCC`、开发版 `~/Library/Application Support/PinCC.dev`（§8.16），两端都可以用 `PINCC_HOME` 覆盖（非空；相对路径按当前目录解析）。Swift 里只有 `Home.resolve()` 一个函数能决定数据目录，GUI、CLI、抓取的下载目录、LLM 的工作目录都用它；lint 禁止在别处拼数据目录（§5.13）。Mac 版 v1 不提供“移动数据目录”的界面选项，避免 GUI 和 `pincc` 看到不同的数据库。

### 4.2 L1：一个数据格式（必须做到）

完整的数据字典见附录 D，提交后就是 `contracts/DATA.md` 和 `contracts/settings-schema.json`。代码里出现任何未登记的列、设置键、操作名、meta 键或 State 值、文件夹时，C# 和 Swift 两边的完整性测试都会失败（§5.12）。

#### 4.2.1 SQLite 层

- 只用 `sqlite3_open_v2` 打开（Db.cs:26），所以新建的库都是 UTF-8 存储。建表语句从 Store.cs 导出到 `contracts/tables/schema.sql`，Swift 逐字节嵌入，`sqlite_master` 里的 SQL 文本两端相同。
- `user_version` 是一个共享常量 1，处理规则照搬 C#（Store.cs:60-63、71-79）：大于 1 时只读打开；小于 1（包括负数）时走 Migrate，执行建表语句并把它设为 1。`SetSetting` 与 C# 一样不受只读保护。如果想让异常值一律只读，先在 C# 里改（W3 另加一项，由你确认），再移植。
- 打开数据库时不写任何东西（否则只读的 CLI 命令会让所有其他连接的 `data_version` 变化）。写操作用 `BEGIN IMMEDIATE`。busy_timeout 与 C# 相同：GUI 2000 ms，CLI 5000 ms。
- **文本用 SQLite 的 UTF-16 接口读写**：C# 用 `sqlite3_bind_text16` 和 `sqlite3_column_text16`（Db.cs:119、142），Swift 也用这两个接口，以 `[UInt16]` 进出。这样遇到非法 UTF-8 或孤立代理项时，两端都由同一个 SQLite 做同样的转换，哈希和 CLI 输出才相同。显式传长度，内嵌的 NUL 保留。
- 毫秒时间和 id 一律是 Int64；布尔值是 0/1；REAL 列保持 REAL，永远不写 NaN 和无穷大。
- Mac 另外设置 `checkpoint_fullfsync`，只影响持久性，不改变文件格式。

#### 4.2.2 cards 各列

| 列 | 规则 | 跨平台处理 |
|---|---|---|
| `body` | 原样存储，读时不做任何规范化，不做 NFC/NFD 转换。只在 C# 也这样做的地方把 CRLF 换成 LF（编辑器提交、勾选任务） | Mac 编辑器输入 U+2028/U+2029 时换成 `\n`；关闭 NSTextView 的所有自动替换（§8.3）。哈希按存储的内容计算 |
| `images` | 空时为 NULL，否则是 System.Text.Json 默认写法的 JSON 数组（`/` 不转义，所以 Purge 的正则能找到引用）。引用格式 `img/<小写 sha1 前 20 位>.<扩展名>` | 扩展名规则与 C# 相同：拖入或粘贴的图片文件取文件名的扩展名转小写，只接受 `.png .jpg .jpeg .gif .bmp .webp .tif .tiff .ico`，其中 `.jpeg` 记为 `.jpg`，`.tif` 和 `.tiff` 保持原样（ClipData.cs:12、73）；CLI `--image` 和网页图片按文件头判断，只有 png、jpg、gif、bmp、webp（Cli.cs:187-191，Fetch.cs:435-443）。Mac 把 HEIC/HEIF/AVIF 先转成 PNG 或 JPEG（§1.4 登记），转码时把 EXIF 方向**写进像素**，不留方向标记（WPF 不看方向标记） |
| `summary` | 原样存储 | — |
| `meta` | CardMeta 的 JSON：写法同 C# 的 `WhenWritingNull`，12 个 PascalCase 键（附录 D），`Size`、`Mtime` 是整数 | 只保留 C# 已有的哨兵值：`FType = "文件夹"`，`State = "manual" / "blocked" / "missing"`，`SumHash = "source-changed"`。不允许新增持久化的状态。`meta.Src` 是写入 meta 时卡片当时派生出的 Src 字符串（逐字，不做路径规范化），也可以为 null；Src 由正文首行派生，本身不存储。首行改变后旧 meta 原样保留到重新读取为止，由 `SrcInfo`（Card.cs:142）判为不属于当前来源。已知不相等的情形：由网址变成的图片卡（Desk.cs:1144，meta.Src 是原网址，卡片已无 Src）、CLI 给尚未读取的链接或文件卡片写摘要（Cli.cs:165，meta 没有 Src）。严格读取器、写入前校验和 `pincc doctor` 都不把两者不相等当成错误。`meta.Error` 是不透明的系统文本。meta 是不进历史的缓存，Swift 只在 C# 也会重写的时候重写它 |
| `color`、`topmost`、`opacity`、`collapsed` | 与 C# 相同 | opacity 按 `Math.Round(x, 1)` 的银行家舍入存储 |
| `free`、`mon`、`x`、`y`、`w`、`h` | `mon` 是不透明的显示器 id：Windows 是设备接口路径或 `\\.\DISPLAYn`，Mac 是 `mac:` 加 CGDisplay UUID。`x/y` 是相对工作区（Windows rcWork / Mac visibleFrame）左上角、不取整的 DIP 偏移；`w/h` 用银行家舍入 | 不认识的 `mon`：卡片在主屏参与平铺，**不改写** `mon/x/y/free`；只有用户在这台电脑上移动它时才改写（W3d 之后，平铺、缩放、按屏幕置顶也不会改写异机卡片）。每张卡片只有一个位置，最后一次移动为准。每屏设置键 `top:<monId>` 两端不会冲突 |
| `remind_at`、`done_at`、`deleted_at`、`created_at`、`updated_at` | UTC 毫秒，向下取整。`updated_at = max(now, 上一次 + 1)`，保证单调 | 提醒时刻由本地时间换算时按 .NET 的规则（§6：歧义和跳过的本地时间取标准时间偏移，保留歧义标记） |

#### 4.2.3 ops

- `before` 是 Card 快照，写法同 System.Text.Json 默认：21 个 PascalCase 键，按声明顺序，写出 null，与 C# 逐字节相同（`Card.ToJson`，Card.cs:98）。
- `cols` 是逗号分隔的列名，按首次出现的顺序。新建和恢复时重新插入的记录写入时为 `''`；同一组随后对这张卡片的写入会把列并入这条记录（Store.cs:171-176），所以 before 为 NULL 的 `create`/`restore` 记录也可能有非空 cols（例如 Desk.cs:459-461：新建图片卡后同组写 w、h）。restore 对 before 为 NULL 的记录不看 cols。NULL 只出现在发布前的旧记录。
- 操作名只来自共享表（`create edit done delete restore remind summary meta move resize view color tile`）。Mac 不得引入新的操作名。
- 组号是这一组第一条记录的 id（AUTOINCREMENT）；`sqlite_sequence` 两端的推进方式相同，由操作脚本逐步检查（§5.5）。

#### 4.2.4 settings：四个作用域

| 作用域 | 键 | 规则 |
|---|---|---|
| 共享 | `color.mode`、`text.size`、`tile.gap`、`tile.corner`、`hub.corner`、`sound`、`retention.days`、`llm.auto`、`llm.prompt`、`data.features` | 两端读写同一个值。取值语法在 settings-schema.json 里声明，从 ManageWindow.cs:191-202 的选项导出：`color.mode` ∈ off/rotate/kind/tag；`text.size` ∈ 13/14/16/18（读取时接受 12–20 的小数，Desk.cs:74）；`tile.gap` ∈ 8/12/18（读取时用 `int.TryParse`，限制在 4–40）；`tile.corner`、`hub.corner` ∈ tl/tr/bl/br；`retention.days` ∈ 7/30/90/365；布尔键只写 `"1"`/`"0"`（Store.cs:323 只把 `"1"` 当真） |
| 本机 | `backup.last`（名字不变，Store.cs:291）、`db.id`、`local.handoff` | 描述这一台电脑自己的状态：本机 backup/ 目录的最后备份日、本库的 id、上一次交接时的操作序号（§4.4.1）。导出时不写进包，导入时保留本机值。两台 Mac 之间也不共享 |
| Windows 专有 | `hk.*`、`autostart`、`llm.where`、`llm.env`、`llm.distro`、`llm.custom`、`llm.tool`、`llm.model`、`llm.detected`、`top:<Windows 显示器 id>` | Mac 从不读写，没有例外：Windows 的工具可能装在 WSL 里（`llm.where=wsl`），或者是自定义命令（Llm.cs:115-116），在 Mac 上没有意义。Mac 首次运行时自己检测（§8.13） |
| Mac 专有 | `mac.hk.*`、`mac.llm.tool`、`mac.llm.model`、`mac.llm.custom`、`mac.llm.env`、`mac.llm.detected`、`mac.autostart.asked`、`mac.statusitem`（1/0，菜单栏图标）、`mac.welcomed`（1/0，欢迎提示和剪贴板提示已显示过）、`top:mac:*` | Windows 不认识这些键，自然忽略，不需要改 Windows |

- `llm.prompt` 的规则照搬 ManageWindow.cs:180：`Trim()` 后等于 `DefaultTemplate.Trim()` 就删除该键（恢复默认），否则**原样**存储，不做 CRLF 换 LF。这条规则在 W4a 里移进 `Rules.cs` 成为 `SettingsSchema.PromptToStore`，并有向量（CRLF、行尾空格、BOM）。
- 值为 NULL 表示删除这一行；`''` 是另一个值，两者不能混用。
- **共享设置的取值语法永不就地修改**：要改语法只能换一个新键名，旧键保留旧语法。原因：`SetSetting` 不检查只读（Store.cs:325-331），数据库因 `data.features` 或 `user_version` 只读时，旧版的 Desk（Desk.cs:157、191、709、934、1344）和设置页照样写设置，`data.features` 保护不了设置（§4.2.6）。另一种做法是让 `SetSetting` 在只读时拒绝写共享键，那是另一项 Windows 改动，本计划不采用。
- Swift 的设置接口只提供带类型的写入函数（`setBool` 写 `"1"`/`"0"`，`setInt` 写十进制整数，枚举键只接受声明的值）。调试和测试构建里，设置访问经过一个守卫：检查作用域，也检查取值语法。
- `llm.custom`、`llm.env`、`mac.llm.custom`、`mac.llm.env` 标为**敏感**：可能含 API 密钥或带密码的代理地址。`export` 默认不导出（§4.4）。

#### 4.2.5 文件卡片路径

- 一套与操作系统无关的语法（W3a）：Windows 的 `X:\`、`\\server\`（与现有 PathRx 相同，不要求共享名段，Card.cs:224），加上以固定前缀开头的 Mac 绝对路径（`/Users/`、`/Volumes/`、`/Applications/`、`/Library/`、`/System/`、`/private/`、`/opt/`）。`/home/…`、`/mnt/…` 这类 WSL 路径仍是笔记。`kind`、`src`、`title`、`input_hash` 和 CLI 输出在两台电脑上相同。
- **异机路径**（W3b 的 `ForeignPath`）：只是运行时状态，不写库。这台电脑从不对它 stat、读取、刷新、补充元数据、自动摘要或 `SaveMeta`；显示存储的 meta 和“在另一台电脑上”标记；打开时提示“文件在另一台电脑上”。
- **本机路径但文件不存在**：保持今天的 `missing` 规则。
- `path.map`（把同步文件夹的 Windows 前缀和 Mac 前缀对应起来）不在 v1 里。以后要做时必须是只读映射：允许打开和手动 ✨，但不写 meta、不自动摘要，存储的 meta 和“过时”状态保持不变。否则两台电脑的文件修改时间精度不同，会让 RefreshFiles 在每次换机后重读、重写 meta（Desk.cs:1011、1109、1153）。
- TextHash 的“src + 修改时间”回退（Desk.cs:1109）本来就是本机的；异机卡片从不重新补充元数据，所以不会来回翻转。

#### 4.2.6 语义格式版本

`user_version` 只管表结构。哈希输入、哨兵值、JSON 形状这类“语义格式”的变化，用共享设置 `data.features` 标记（W3e）。它只保护 cards、ops 和图片；设置在只读时照样会被写，所以共享设置的取值语法不靠它升级，而是遵守 §4.2.4 的“换新键名”规则：
- 每个数据格式变化都要有一个特性 id，并在 DATA.md 里登记；完整性测试检查这一点。
- 写入新格式的一端先把特性 id 加进 `data.features`；另一端不认识时按只读打开，并提示“数据来自更新版本的 PinCC”。
- 这也解决了“Windows 先发布、Mac 后跟进”期间的不一致：旧版 Mac 不会按旧规则改写新的卡片、历史和图片（设置见上）。

#### 4.2.7 损坏数据的防护

- Swift 写入前，用与 C# 兼容的严格读取器校验每个 JSON 列和数值列，校验失败就回滚这次写入并记日志。校验只看 C# 读取路径会拒绝的东西（JSON 形状、类型、整数是否严格），不检查 C# 允许的语义关系，例如 `meta.Src` 是否等于当前 Src（§4.2.2）。
- `pincc doctor`：按 C# 的读取规则检查一个数据目录，列出无法解析的行。
- 一个 C# 模糊测试把畸形的 `images`、`meta`、`before` 送进 C# 的读取路径，记录失败方式。
- W3f（建议采纳）：Windows 端逐行隔离解析失败的行。

#### 4.2.8 CLI

- 两端的命令、参数、JSON 字段顺序、`--md`、退出码 0–4、`-` 表示读标准输入都相同，对任一端写的数据都成立。
- 比较规则：输出统一把 CRLF 换成 LF，把数据目录替换成 `{HOME}`；命令名只在 `Cli.Cmd` 出现的位置替换成 `{CMD}`（W2h），产品名“PinCC”和正文里的同名文字不替换；图片路径分隔符只在 images 字段和 `![](…)` 行里替换成 `{SEP}`（Store.cs:315-316 把 `/` 换成本机分隔符）。替换之后 JSON 逐字节相同。
- 图片行是否统一写成 `![](<路径>)`，沿用上一版计划的待决定事项（§12 第 7 条）；同意时由 W2l 在 P2 先改 C#，CLI 录制按新格式录。

#### 4.2.9 WAL 和数据目录位置

- 运行中的数据库必须在本机磁盘上。WAL 要求所有连接在同一台主机上共享内存，网络文件系统不行；云同步客户端会上传不带 `-wal` 的数据库文件，还会产生“冲突副本”。
- Mac 启动时检查数据目录（只在 `PINCC_HOME` 指向别处时可能出问题，默认位置不在同步范围里）：
  - `statfs` 报告 smbfs、nfs、afpfs、exfat、msdos；
  - 路径在 `~/Library/CloudStorage/`（Dropbox、OneDrive、Google Drive 的 File Provider 位置）或 `~/Library/Mobile Documents/` 下；
  - 目录或任一上级目录的 `URLResourceValues.isUbiquitousItem` 为真（覆盖 iCloud“桌面与文稿”同步的 `~/Documents`、`~/Desktop`）。
  不用 `NSFileProviderManager` 列举同步域：它只返回调用方自己的 File Provider 扩展注册的域，拿不到 Dropbox、OneDrive 等其他程序的域；第三方同步盘由 `~/Library/CloudStorage/` 路径检查覆盖。
  命中任一项就显示警告，并链接到 DATA-MOVE.md。Windows 端的同样检查是可选的，放到以后。
- 直接复制数据目录只能在所有 PinCC 进程都退出之后进行：最后一个连接关闭时会做检查点，把 WAL 合并进主文件；如果还有 `-wal`、`-shm`，必须一起复制。其他情况一律用 `export`。

### 4.3 双向互开怎样验证

| 验证 | 在哪里 | 内容 |
|---|---|---|
| 操作脚本差分（§5.5） | WSL、两台 Mac | 同一脚本在 C# 和 Swift 上逐步转储，必须相同 |
| 交替读写（§5.6） | WSL | 同一数据目录上 C# 段和 Swift 段交替执行；包括 Swift 写图片后 C# 清理、C# 恢复 Swift 写的快照、`user_version = 2` 的库两端都只读、打开时不写库 |
| 双向样例目录（§5.7） | Windows、WSL、两台 Mac | `win-*` 由 C# 生成、`mac-*` 由 Swift 在 Apple 芯片 Mac 上生成，都提交；C# 检查 `mac-*`，两台 Mac 检查 `win-*` |
| CLI 录制（§5.8） | Windows、WSL、两台 Mac | 每条录制分别对 `win-*`、`mac-*` 样例运行 |
| 真实数据（§5.7） | WSL、两台 Mac | 你的备份 `backup/pincc-*.db` 的副本，不提交，只读打开 |
| 手工往返（P10） | 三台机器 | Windows → Mac → Windows 搬一次真实数据，检查卡片、图片、撤销、摘要不过时、各作用域设置不被改动、异机文件卡片不被改写 |

### 4.4 L2：Windows 电脑和 Mac 用同一份卡片（推荐方案，由你确认）

#### 4.4.1 L2a：导出和导入（与 Mac v1 一起做，约 7 天，即 P11）

- 命令：两端都有 `PinCC export <文件.pincc> [--with-files] [--include-secrets]` 和 `PinCC import <文件.pincc> --replace [--force]`。C# 先实现（它是基准），Swift 再实现。
  - v1 的 `import` 必须带 `--replace`；不带时返回退出码 1，提示“合并导入尚未支持（L2b）”。
  - `export` 默认不附带 files/，`--with-files` 才附带；img/ 总是附带。
  - 两个命令的参数、提示和退出码都进 CLI 录制和 HelpCommon。
- **包格式是一个 SQLite 文件**（Foundation 没有 zip 写入器；SQLite 文件是原子的，还能用 `integrity_check` 校验）：
  - cards、ops、settings、sqlite_sequence，建表语句与 `contracts/tables/schema.sql` 相同；
  - `bundle_meta(format, user_version, db_id, platform, exported_at, data_features)`，`db_id` 是导出方本机的 `db.id`；
  - `bundle_files(path, data, mtime)`：img/ 的全部文件，files/ 只在 `--with-files` 时附带。
- **包不直接用 `VACUUM INTO` 生成**：新建包文件，执行建表语句，`ATTACH` 当前库，逐表 `INSERT … SELECT`；cards、ops 插入之后执行 `DELETE FROM sqlite_sequence; INSERT INTO sqlite_sequence SELECT name, seq FROM src.sqlite_sequence`，因为插入只会把序列设为现有的最大 id，而源库在删除最新的卡片或操作记录（Forget、Purge）后序列更高（有专门的测试比较两边的 sqlite_sequence）。settings 在 SELECT 时就过滤掉 `secret: true` 的键（`--include-secrets` 时不过滤）和“本机”作用域的键。这样被排除的值从来没有写进包文件；如果先复制再 DELETE，旧值会留在 SQLite 的空闲页里（secure_delete 默认关闭，而且取决于编译参数，winsqlite3 和 macOS 的系统 SQLite 未必相同）。
- **敏感设置默认不导出**（settings-schema.json 里 `secret: true` 的键）。`--include-secrets` 才导出，并显示警告。导入时从不覆盖本机的敏感设置。包文件常常会经过云盘或聊天工具，里面不应该有 API 密钥。
- **GUI 在运行时拒绝导入**，提示“请先退出 PinCC”。判断按数据目录进行，两端规则一致：
  - Windows：W5 让 GUI 启动时另建一个互斥量 `Local\PinCC.Home.<数据目录完整路径转小写后的 SHA-1 前 16 位>`（只创建，不等待，不发信号）。原来的 `Local\PinCC.Instance`（Program.cs:10）属于整个登录会话、与数据目录无关，你每天开着的 PinCC 一直持有它，用它判断会让所有 `PINCC_HOME` 指向临时目录的导入测试都被拒绝，结果还取决于你当时开没开 PinCC。旧版 GUI 没有新互斥量，所以要先发布并运行 W5 的 GUI，再使用 import。
  - 判断经一个进程内接缝 `Cli.GuiRunning`，做法与 `When.Clock` 相同：静态委托，生产默认只调用 `Mutex.TryOpenExisting` 检查上面的按目录名字，只打开、不发信号。C# 测试、CLI 录制和交替读写在进程内替换这个委托，固定“GUI 在运行 / 不在运行”两种情况；测试从不创建、也不打开真实的互斥量，这样录制结果不依赖真实会话的状态；尤其绝不创建 `Local\PinCC.Instance`，否则你的 PinCC 在测试期间启动时会以为已有实例而直接退出（Program.cs:24-29）。
  - Mac：GUI 对数据目录里的 `.instance` 持有 flock，`import` 取不到锁就拒绝；Swift 测试同样经接缝注入。
  原因：运行中的 GUI 在内存里有撤销栈的组号、正在编辑的会话（op id 和 rev）、撤销条的闭包，以及按 id 和 rev 管理的卡片窗口（Desk.cs:27、39、219-224、491，CardWindow.xaml.cs:27、167-175）。导入替换数据后，同一个 id 可能是另一张卡片、rev 可能碰巧相同，自动保存会把旧卡片的正文写进导入的卡片，Ctrl+Z 会恢复另一台电脑的操作组。要支持在 GUI 运行时导入，需要 Desk 在检测到“数据代次”变化时整体重置（结束编辑、清空撤销栈、关闭并重建所有窗口、重读设置），那是另一项 Windows 改动，不在 v1 里。
- **交接检查**：`import --replace` 会整体替换本机数据。为了不悄悄丢掉这台电脑上还没导出的修改，每次 export 和 import 成功后，本机在“本机”作用域的 `local.handoff` 里记下当时 ops 的 `sqlite_sequence`。`import --replace` 之前比较：本机的 ops 序号超过记下的值（交接之后又有进历史的修改），或者本机有卡片却没有这条记录，就拒绝并提示“这台电脑上有还没导出的修改”，加 `--force` 才覆盖。只写 meta 的 `journal:false` 写入不产生 ops，不算修改。推荐用法写成“交接”：离开一台电脑前先 export，到另一台再 import --replace，同一时间只在一台电脑上改卡片。
- `import --replace` 的步骤：
  1. 包的 `user_version` 或 `data_features` 比本机新时拒绝；GUI 在运行时拒绝；交接检查不通过且没有 `--force` 时拒绝；
  2. 把当前数据库备份到 `backup/pincc-YYYYMMDD-import-HHmmss.db`。这个名字仍匹配 Store.cs:298 的 `pincc-*.db`，参与保留 7 份的轮换；不用每日备份的名字，因为同一天的 `Store.Backup()` 会先删除同名文件（Store.cs:295-296）；
  3. 把 img/、files/ 按并集写入，从不删除，文件修改时间设为现在（这样 Purge 不会马上把刚导入的文件当旧文件清掉）；
  4. 在一个事务里替换 cards、ops、sqlite_sequence，以及共享设置和对方平台的设置；保留本机平台、“本机”作用域（`backup.last`、`db.id`、`local.handoff`）和敏感设置；最后更新 `local.handoff`。
  id 和组号原样保留，所以撤销历史能带过去。
- 文档里同时写明手工方式：两端都退出 PinCC，复制整个数据目录。
- 测试：
  - Windows 导出 → Swift 导入，以及反过来，在 WSL 交替读写和两台 Mac 的样例上都跑；导入后的转储除本机作用域和本机平台的设置外与源相同；
  - GUI 运行时导入被拒绝（经接缝，有 CLI 录制）；交接检查的拒绝和 `--force`；不带 `--replace` 的退出码 1；
  - 在设置里放一个已知的假密钥，导出后按字节搜索整个包文件（包括空闲页），必须找不到；两端各跑一遍；
  - 只复制 `pincc.db` 而不带 `-wal` 会丢提交，这一反例测试证明 `export` 的必要。

#### 4.4.2 现在就写进格式、为以后留路的事

只保留影响格式或策略的几项，其余推迟到真正做 L2b 时：
- 设置分作用域（§4.2.4，只影响 Mac 端；“本机”作用域的 `db.id`、`local.handoff` 由 W5 首次写入，`backup.last` 沿用现有键名）；
- 导入从不重新编号；
- `updated_at` 单调递增（今天已经如此）；
- img/、files/ 按内容寻址，搬迁从不删除；
- `data.features`（W3e）；
- `db.id` 在第一次导出时生成，属于本机作用域，导入不覆盖，所以两个库的 id 始终不同，L2b/L2c 能区分数据来源。

#### 4.4.3 L2b：合并导入（以后，约 8–10 天，含表结构 v2）

写进 DATA.md 的设计约束（现在就定下，免得以后返工）：
- 增加 `card_origin(card_id, origin, origin_id)` 表，由触发器填写，启用时补齐旧数据。
- 按列合并：每一列以“最后改动这一列的时间”决定谁新，**不用行的 `updated_at`**。原因：`journal:false` 的元数据写入也会推进 `updated_at` 而不留操作记录（Store.cs:155-160，Desk.cs:1153），用它会让旧正文胜出。
- 这个时间也不能只用 `ops.at`：同一组对同一张卡的后续写入会合并进原记录，只更新 cols，`at` 仍是第一次写入的时间（Store.cs:171-179）；一次编辑会话的所有自动保存都在同一组里（Desk.cs:442，CardWindow.xaml.cs:681、735），10:00 开始、10:20 结束的会话会输给对方 10:10 的修改。所以 L2b 需要表结构 v2，增加 `ops.last_at`（Journal 每次合并时更新），与 card_origin 一起两端同时发布。在那之前，如果一个编辑会话跨过了对方的修改时间，这张卡整行交给你选择。
- 操作记录会按保留期清理（Store.cs:276，最短 7 天），`Forget` 也会删除（Store.cs:261）。水位线早于最早保留的操作记录时，合并拒绝按列进行，改为整行比较并让你选择。
- 合并通过 `Store.Mutate` 作为一个 `sync` 操作组写入，Ctrl+Z 和 `restore` 都能撤销；每个被覆盖的值都记进历史。
- meta 作为缓存，最后写入者为准。

#### 4.4.4 L2c：经同步文件夹持续同步（只有两台电脑都每天用时才做，约再加 12–15 天）

- 每台电脑保留自己的本地数据库。
- 同步文件夹里只放一次写入、不再修改的变更集文件 `PinCC-sync/<来源>/<序号>.pincc`（先写临时名再改名），以及一个只增不删的按内容寻址图片池。
- 新增 `gone` 墓碑表，由 AFTER DELETE 触发器填写，因为 Forget 和 Purge 是硬删除。
- 每个对端的水位线存为本机作用域的设置。
- Purge 不能删除图片池里的图片，也不能删除待处理变更集引用的图片。
- 需要表结构 v2，两端同时发布（旧程序遇到 v2 会只读，Store.cs:61）。

#### 4.4.5 否决的做法

- 把 `pincc.db` 或整个数据目录放进 iCloud、OneDrive、Dropbox 或网络共享：WAL 需要单一主机，同步客户端会上传不带 `-wal` 的数据库，还会出现冲突副本。
- 用 `sqlite_sequence` 给两台电脑划分 id 区间：改变了组号的含义，而且合并时仍然要处理冲突。

---

## 5. 一致性保障体系

### 5.0 总览

| # | 机制 | 保证什么 | 生成在 | 检查在 | 失败时挡住什么 |
|---|---|---|---|---|---|
| 5.1 | 单一来源的数据表 | 常量、文案、表格只有 C# 一份 | Windows | C#、WSL、两台 Mac | Mac 构建 |
| 5.2 | .NET 基础语义表 | 字符、大小写、转义、数字、代码页与 .NET 10 相同 | Windows | WSL、两台 Mac | Mac 构建 |
| 5.3 | 纯函数向量 | 每个确定性单元的输出相同 | Windows | WSL、两台 Mac | Mac 构建 |
| 5.4 | 正则清单 | 每个正则只有一个 Swift 对应，匹配范围相同 | Windows | WSL、两台 Mac | Mac 构建 |
| 5.5 | 操作脚本差分 | 存储层每一步的完整数据库状态相同 | Windows | WSL、两台 Mac | Mac 构建 |
| 5.6 | WSL 交替读写 | 数据格式真正互通，而不只是行为相同 | — | WSL | Mac 构建 |
| 5.7 | 双向样例目录 | APFS、Mac 的 SQLite 和 Foundation 上也互通 | 两端 | 三台机器 | Mac 构建 |
| 5.8 | CLI 录制 | CLI 字节、退出码、数据库效果相同 | Windows | 三台机器 | Mac 构建 |
| 5.9 | 布局与场景向量 | 内容和布局相同 | Windows（屏幕外 WPF） | WSL、两台 Mac | Mac 构建 |
| 5.10 | 桌面编排 | 有状态的桌面规则相同 | Windows | WSL、两台 Mac | Mac 构建 |
| 5.11 | 移植台账 | 任何一端的改动都不会被漏掉 | 两端 | 两端 | Mac 构建 |
| 5.12 | 数据字典完整性 | 数据格式不会悄悄漂移 | 两端 | 两端 | C# 测试、Mac 构建 |
| 5.13 | Swift lint | 语义不同的同名 API 混不进来 | — | WSL、两台 Mac | Mac 构建 |
| 5.14 | Windows 不回退 | 每天在用的程序不出问题 | Windows | Windows | 该 src 提交 |

### 5.1 单一来源的数据表

- **内容**：调色板（9 色 × 名称和 Back/Strip/Accent/Ink）、自动轮换顺序（Card.cs:193）、卡片列顺序、建表语句、操作名和中文标签、LLM 工具表、默认提示词模板、CLI 帮助（公共部分和命名的平台片段）、热键动作 / 默认值 / 已知冲突、提醒预设的标签、Metrics、正则清单、图标映射、菜单规格。评审补充的几张也在内：`Desk.Readable`（哪些扩展名自动交给 LLM，Desk.cs:265-272）、`ClipData.ImageExts`（ClipData.cs:12）、只定位不运行的扩展名表（Desk.cs:765）、设置页的选项集合（ManageWindow.cs:191-202）、CLI 的参数表（Cli.cs:16-28）。
- **做法**：测试项目今天只编译 src/Core 和 UI 里的 Layout.cs、Native.cs（tests/PinCC.Tests.csproj:14-15），所以表按来源分三处导出，都写成 `contracts/tables/*.json`：
  - **Core 的表**（`Card.Rotation`、`Store.Schema`、`Llm.Table`、`Cli.Help`、`Cli.Options`、`Cli.Cmd`、每个 `[GeneratedRegex]` 的模式和选项）：由 `tests/Vectors/Tables.cs` 反射读取；
  - **UI 里不依赖窗口的表**（`Hotkeys.Actions/Known`、`Palette.All`、`ClipData.ImageExts`）：P1 在 `tests/PinCC.Tests.csproj` 里显式加入 Hotkeys.cs、Palette.cs、ClipData.cs 三个文件（只改 tests/），由 Tables.cs 读取；
  - **依赖窗口的表**（`Desk.Readable`、菜单规格和图标）：由 OracleWpf 导出，它编译全部 src/。
  - **不是成员的表**：设置页的选项（ManageWindow.cs:191-202 的 `Segments(...)` 参数）、只定位不运行的扩展名（Desk.cs:765 的 `is ".exe" or …` 模式）、菜单和图标字形（Desk.cs:1293-1351）、提醒预设标签（When.cs:16-23、CardWindow.xaml.cs:528-529）反射读不到。P1 先用一个词法抽取器从源码抽出，台账记这几段源码的哈希；W4a 把它们逐字节搬成具名静态成员之后，改为反射读取。
  `tools/gen-swift-tables.sh` 把 JSON 生成 `mac/Sources/*/Generated/*.swift`；一个 Swift 测试在内存里重新生成并比较，所以手改生成文件会失败。
- **常量**：CardWindow.xaml.cs:22 的 `PillH`、`Thumb`、`MinW`、`MinH`、`RowH`、`PadTop` 已经是公开常量，直接导出。真正内联在 WPF 代码里的数字（0.6、26、+3、1.45、1.5、72/52 等）在 W4a 里成为 `Rules.cs` 的具名常量，同样导出。XAML 里的度量（CardWindow.xaml 的圆角、边距、行高）不是 C# 成员，由台账的整文件哈希跟踪（§5.11）。
- **集合字面量扫描**：一个 C# 源码扫描列出 src/ 里所有字符串数组、集合字面量、`is "…" or "…"` 模式和 switch 的字符串分支（例如 Desk.cs:765、Cli.cs:212、Fetch.cs:197、420，ManageWindow.cs:381，Native.cs:282），每一个都必须已经导出，或在白名单里注明“只属于 Windows”及原因。
- **Mac 专有的行**（PlatformText、KeyBindings.Mac、KnownConflicts.Mac、SF Symbol 名）放在 `contracts/tables/mac-*.json`。完整性测试检查：每个命令、图标、平台用词都有两端的对应，或明确标为“无”。

Metrics 当前的值（以 C# 为准，此表只供对照；来自上一版 §5.1 的梳理）：

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
| 自动高度上限 | max(64, min(360, 工作区高度 × 0.4))，单位 DIP（Desk.cs:257-261） |
| 卡片间距 G | 设置 `tile.gap`，默认 12，可选 8/12/18，读取时限制在 4–40 |
| 字号 | 设置 `text.size`，默认 14，范围 12–20 |

### 5.2 .NET 基础语义表（驱动 DotNetCompat）

生成器遍历：
- 全部 65,536 个 UTF-16 单元：`char.IsWhiteSpace`、`IsLetterOrDigit`、`IsLetter`、`IsDigit`、`IsControl`、`GetUnicodeCategory`、`ToLowerInvariant`、`ToUpperInvariant`；
- 全部码位：字符串级的不变大小写，以及 .NET 正则 `\w`、`\d`、`\s` 的成员；
- `JavaScriptEncoder.Default` 和 `UnsafeRelaxedJsonEscaping` 对每个标量是否转义；
- 10 万个随机加边界双精度数：`ToString()` 和 System.Text.Json 的数字文本；
- `Encoding.GetEncoding` 对 WHATWG 标签表的接受情况和代码页，以及 gb18030 的完整解码表（二字节表加四字节区间，带 .NET 的 `?` 回退）。big5、shift_jis、euc-kr、euc-jp 和单字节代码页见 §10.3 裁剪项 b。

输出按区间压缩成二进制，base64 嵌入 Swift。每个 DotNetCompat 函数另有自己的向量文件，由调用它所替代的 .NET 原语生成。这样 ICU、Foundation 的 Unicode 版本和区域数据都影响不到结果。

### 5.3 纯函数测试向量

- **覆盖**：Card 的派生状态、Hash、FNV、Tag、AutoColor；When 的 Parse、Presets、Format；Markdown 全家；Card 和 CardMeta 的 JSON 字节与宽容读取；TextDecode；FromHtml、MainText、JsonLd；UrlGuard；Host；WinPath；HtmlDecode；Llm 的 Tokens、Prompt、StripFence、Ansi；Cli 的 Split、OpFor、ImageExt、Line、Md、Dto、Iso；Fetch 的 Cap 和 Snippet；一个固定文件夹上的文件信息；W4a 之后每个 Rules 函数。
- **命令**：`dotnet.exe run --project tests -- vectors contracts/vectors [--expect 族名…]`。生成器进程固定 `CultureInfo` 为 zh-CN。
- **时间和时区**：
  - 测试进程在进程内设置 `When.Clock`（固定的 UTC 时刻），生产程序不读任何环境变量；
  - 时区**不经过接缝**：生成器在测试代码里用反射设置 `TimeZoneInfo` 缓存的本地时区（`s_cachedData._localTimeZone`），所以生产代码走的是真实的 Local 语义。它影响经 `TimeZoneInfo.Local` 的路径：`ToLocalTime()` 设置的歧义夏令时标记、`FromUnixTimeMilliseconds(...).LocalDateTime`（When.cs:10）、`new DateTimeOffset(local)` 对这个标记的处理（When.cs:9）、`"zzz"` 用的本机偏移（Cli.cs:303）、`DateTimeOffset.Now` 和 `TimeProvider.GetLocalNow()`；
  - **它不影响 Windows 上的 `DateTime.Now`**：那条路径经 `GetDateTimeNowUtcOffsetFromUtc` 调 Win32 的 GetTimeZoneInformation 读系统时区（dotnet/runtime TimeZoneInfo.Win32.cs）。所以生成器执行的代码里不能还有 `DateTime.Now`：W1b 之前，OracleWpf 的 B0 场景不放提醒和文件时间（CardWindow.xaml.cs:437、483 直接读 `DateTime.Now`），这类场景在 W1b 之后补录；
  - 每个向量单元前后都断言 `TimeZoneInfo.Local` 仍是设定的时区（`TimeZoneInfo.ClearCachedData()` 会换掉整个缓存，Desk.cs:905；OracleWpf 从不调用 Desk.Start，所以这个消息钩子不会挂上）；
  - 每个“现在”都用 `DateTime.SpecifyKind(utc, Utc).ToLocalTime()` 构造；
  - 时区网格：Asia/Shanghai（完整）、America/New_York 和 Europe/Berlin（夏令时切换日，包括回拨那一小时的两次出现）。
- **文件头**：`{unit, generator, 每个 C# 源文件的 SHA-256, .NET 版本 (RuntimeInformation.FrameworkDescription), CompareInfo.Version/SortVersion, ICU 版本, winsqlite3 的 sqlite3_libversion(), culture, tz}`，然后每行 `{in, out}`。
- **编码**：合法 Unicode 的字符串直接写 JSON；可能含孤立代理项的写 `{u16: 十六进制}`；字节写 `{b64}`；异常写 `{throws: 类别}`（共享的异常 → 类别表）。Swift 比较时用 `Array(s.utf16)`，从不用 String 的 `==`。
- **语料**：固定 SplitMix64 种子的语法模糊生成器。提交进 git 的每族约 5k 行（When、Markdown、Src/IsUrl、随机字节等），不压缩、LF、按稳定顺序排列，git 能做增量；`--deep` 语料（When 5 万行、Markdown 2 万行等）只提交种子和结果哈希，需要时重新生成。
- **覆盖率门槛**：C# 端统计每个正则的每个分组和分支、When 单位表和时段表的每个 switch 分支在语料里是否被命中，有任何一项为零就失败。这防止生成器的作者和移植者有同一个盲区（例如“礼拜”+“下”+全角冒号、大写的 `PM`、`Tomorrow`、带小数的“个小时”）。
- **实时差分模糊**：`tools/check.sh --fuzz` 在 WSL 里用基于时间的种子并排运行 dotnet.exe 和 Swift，发现的分歧种子作为向量提交。
- **重新生成的纪律**：
  - `--expect <族名…>`：其他族有任何变化就失败，防止一次改动悄悄改掉别的规则；
  - 每次重新生成都打印语义摘要（每族变化的行数、前 20 条的输入 / 旧值 / 新值），写进 `spec:` 提交说明；
  - 向量是普通文本，`git diff` 直接可读。

### 5.4 正则清单与翻译规则

- **清单**：47 个 `[GeneratedRegex]`（反射取得模式、选项、所属成员）加上 10 行内联 `Regex` 调用。一个源码扫描门槛要求 src/ 里含 `Regex.` 或 `new Regex(` 调用的**行数**等于清单里的内联项数量，`Regex.Escape` 不算调用点（Fetch.cs:468 一行里有 `Regex.Match` 和 `Regex.Escape`，按出现次数数会是 11 对 10）。W2g 把模式固定的内联正则改成 `[GeneratedRegex]`，之后剩 3 行。
- **运行时拼出的正则**，三处来源不同：
  - Fetch.cs:468：插入常量表 KnownContainers 的类名（Fetch.cs:463，经 `Regex.Escape`）；
  - Fetch.cs:570：插入代码给定的属性名（property、name、itemprop、content，Fetch.cs:137-138）；
  - Fetch.cs:471：插入从 HTML 里取到的标签名，即 468 行 `<(\w+)` 的分组；.NET 的 `\w` 含非 ASCII 字母。
  它们和 `Element()`、`Attr()` 一起在 Swift 里写成手写扫描器，并有含非 ASCII 标签名的向量。
- **每个 Swift 对应物带一个标记和模式哈希**，二选一：
  1. 基于 `[UInt16]` 和 .NET 字符表的手写扫描器。优先用于 Card 和 Markdown 的 TagRx、PathRx、Markdown 块分类、TimeRx、EnvLineRx，以及 HTML 的惰性块模式（JunkRx、ChromeRx、FormRx、Article、Main、Body），这些要重现最左优先的配对。
  2. `NetRegex`（NSString 上的 NSRegularExpression），按机械规则翻译：
     - `.` → `[^\n]`；非多行模式的 `$` → `(?=\n?\z)`；
     - `\s` → `[\t\n\x0B\f\r\x85\p{Z}]`；`\w` → 由表生成的 .NET 类；`\b` → 基于这个类加 U+200C/U+200D 的环视；
     - `\p{L}`、`\p{N}` 与 BMP 取交集，因为 .NET 按 UTF-16 单元判断；
     - IgnoreCase 只允许用在 ASCII 字面量上；
     - 替换一律用求值函数，不用从数据拼出的模板；
     - 始终打开 `.useUnixLineSeparators`；
     - 检查 `.internalError`（ICU 回溯上限 8 MB），出现时改用扫描器。
- **向量**：匹配范围（UTF-16 索引、长度、分组）和 Replace 结果。语料包含 CRLF、U+2028、U+0085、VT、NBSP、U+3000、全角和阿拉伯数字、辅助平面字母、`>` 后的组合符、ſ 和开尔文符号、2 MB 以上的 HTML。WSL 和两台 Mac 的 ICU 各不相同，所以三处都要跑。
- lint 禁止在可移植目标里使用 Swift Regex 和正则字面量。

### 5.5 操作脚本差分测试（存储层）

- **保证**：存储层的有状态语义相同。每一步之后的每一行、rev、历史记录、cols 顺序、组号、`sqlite_sequence`、图片文件集合、`data_version` 的副作用都相同。覆盖：打开和迁移、只读闸门、Insert / Mutate / Write / Journal / InTx 回滚、ifRev、`journal:false`、第二个并发连接、Restore 的所有分支、Forget、Purge（带文件修改时间）、Backup 保留、设置、SaveImage、CLI 命令。
- **脚本格式**（`contracts/scripts/*.json`）：`open(conn, busyMs)`、`clock(ms)`、`begin(g)`、`insert`、`mutate(id, set:{列:值}, op, ifRev, journal)`、`restore(grp)`、`forget`、`purge(days)`、`backup`、`setting`、`saveImage(b64, ext)`、`touchImg(name, mtime)`、`cli(argv, stdin b64)`、`close`、`reopen`、`failNext`（临时触发器）、`rawText(id, 列, 十六进制字节)`（用 `CAST(X'…' AS TEXT)` 写入非法 UTF-8），以及交替执行用的 `impl:cs|swift` 分段标记。
- **规范转储**：两端用同一个 dump.sql，由宿主代码打印成 `{typeof, 整数十进制 | 实数 IEEE 位的十六进制 | 文本 UTF-8 十六进制 | blob 十六进制}`，不用 `quote()`（不同 SQLite 版本的 REAL 文本格式不同）。另外每行打印经 UTF-16 接口读回的文本的十六进制、`Card.Hash(Body)` 和 `InputHash`，这样非法文本在两端解码后的差异也能看到。转储还包括 `PRAGMA encoding`、`user_version`、`journal_mode`、`sqlite_master` 的 SQL 文本、`sqlite_sequence`、img/ 文件列表和 `data_version` 的变化量。
- **驱动**：C# 的 `tests/Oracle/ScriptDriver.cs` 调用公开的 Store/Cli 接口；Swift 的 ScriptReplayTests 和 `pincc-oracle` 调用 PinCCCore。
- **随机脚本**：SplitMix64 种子，默认 300 个种子 × 60 步，`--deep` 5,000 个。卡片字段取自一个语料：CRLF、NUL、emoji、NFD、中日韩文字、`/`、引号，以及 0.1、−0.0、80.8 这样的双精度数；操作种类按权重抽取，包括两个连接交错和 ifRev 冲突。
- **提交什么**：精选脚本提交每一步的完整转储；随机种子只提交每一步的哈希，外加一个“找到第一处分歧”的工具，失败时打印第一个不同的步骤和表。这样仓库不会膨胀。

### 5.6 WSL 里的实时交替读写

- `tools/interleave.sh` 运行带 `impl` 标记的脚本：C# 段通过 dotnet.exe 在 Windows `%TEMP%` 下的数据目录上执行；关闭时最后一个连接做检查点；目录复制到 WSL 的 ext4 上执行 Swift 段，再复制回去。两个进程从不跨 drvfs 共享一个活的 WAL。每一步的转储必须等于提交的全 C# 轨迹。
- 专门的用例：
  - Swift 写图片卡片，把文件时间改到 400 天前，C# 运行 Purge，文件必须保留（反过来也测）；
  - C# 恢复 Swift 写的 `before` 快照（反过来也测）；
  - Swift 建的库由 C# 的 GUI 侧代码载入：OracleWpf 在一个不调用 Start 的 Desk 里载入卡片并构建场景（§5.9），不显示任何窗口；
  - 只读打开什么都不写；
  - `user_version = 2` 的样例两端都只读；`data.features` 含未知特性时两端都只读；
  - `Cli.GuiRunning` 接缝报告 GUI 在运行时，导入被拒绝（§4.4.1）；测试项目不编译 Desk，打开的编辑会话本身构造不出来；
  - 只复制 `pincc.db` 而不带 `-wal` 会丢提交（反例，证明 `export` 的必要）。
- **G1 闸门**（P1 第一天）：从 WSL 调 dotnet.exe 的方式先单独验证：输出一律写文件，从不接到 `head` 这类提前关闭的读取端（你的笔记记录过 WSL 互操作管道挂起）；每次调用有超时；连续启动 100 次不挂起；测出每次启动的开销。

### 5.7 双向样例目录与真实数据

- `contracts/fixtures/win-*` 由 C# 驱动生成：基础、发布前的旧库（ops 没有 cols）、v2 只读、CRLF 和 Unicode、带修改时间清单的图片、异机内容（Mac 路径、`mac:` 显示器 id、`mac.*` 设置）、每种操作、每种 meta 哨兵值、极端几何值（±1e300、±2^31）的“毒样例”，以及整数列（created_at、remind_at、color 等）里存了 ±1e300 这类 REAL 的行（C# 的 `Convert.ToInt64` 在这里抛异常，§6 第 9 条）。
- `tools/mac.sh fixtures` 在 Apple 芯片 Mac 上用 Swift 驱动生成 `mac-*` 并提交。**`mac-*` 的期望结果由 C# 在 Windows 上计算并提交**，两台 Mac 再验证 Swift 能重现，所以基准仍然是 C#。
- 检查内容：用两端的 CLI 列出和显示每张卡片、恢复每个操作组、Purge 空跑和实跑、确认 All/Info/Ops 都能解析、`--json` 输出相同。
- **G5 真实数据**：从 P4 起，把你 `backup/pincc-*.db` 中最新一份的副本（不提交、放在 artifacts/ 下）在 WSL 和两台 Mac 上由 Swift 只读打开并比较 CLI 输出。

### 5.8 CLI 录制

- `contracts/cli/*.json`：`{fixture, env(cwd), clock, argv, stdin b64}` → `{exit, usageLine, stdout, dump}`。约 120 条，覆盖每个命令、参数、四种整数解析方式、标准输入编码、退出码和“数据库忙”。
- C# 端在进程内运行 `Cli.Run`，外加 Program.RunCli 包装的 10 行副本（Fetch.Text 解码标准输入、兜底退出码 1）；台账对 Program.cs:37-66 做哈希，那里一改就能发现。从不执行 PinCC.exe。
- Swift 端既在进程内运行 PinCCCli，也作为 `pincc` 子进程运行，以检查真实的退出码和 stdout 编码。只有子进程测试允许用 `PINCC_NOW`，而且只有测试构建认这个变量。
- 每条录制分别对 `win-*` 和 `mac-*` 样例运行。.NET 和 SQLite 给出的消息文本不参与比较，只比较“错误：”前缀和类别。帮助除命名的平台片段外逐字节比较。比较前的规范化见 §4.2.8。

### 5.9 布局与场景向量

- **布局**：W4b 之后，C# 生成器把精选场景和随机场景（负坐标、缩放 1/1.25/1.5/1.75/2、四个角、障碍物、每种卡片状态）送进 `LayoutPass.Compute`，记录输出：整数移动、胶囊、隐藏、+N、悬停推迟、可见性。不少于 2,000 个场景。
- **场景**：`oracle/wpf/PinCC.OracleWpf.csproj` 为固定的卡片和视图状态构建 CardWindow、`MarkdownView.Render`、CardMenu、HubMenu 和 Flyout 面板，遍历可视树，输出 CardScene、MdRender、MenuSpec 的 JSON；实测值放在单独的块里。它从不调用 `Show`。
- **OracleWpf 从不调用会显示窗口或碰到真实环境的入口**：`Desk.Start`（它会 Maintenance 写备份和 `backup.last`、DoLayout 显示卡片和 + 按钮、注册全局热键、启动 1 秒节拍、对到期卡片响提示音、联网 Enrich、DetectLlm 启动子进程；`PINCC_HOME` 为空且没有 `autostart` 键时还会把 HKCU Run 改成 oracle 自己的路径，Desk.cs:97-130、184-191），以及任何会走到 `DoLayout` 的方法（NewCard、CreateFrom、Tile、ShowAll、ToggleHidden 等；DoLayout 先 `Screens.Invalidate()` 冲掉假屏幕，再 `Show()` 和 `Native.Front`，Desk.cs:798、886-889）。不调用 Start 时 `_layout` 计时器没有处理函数（Desk.cs:112），Relayout 什么也不做。
- **OracleWpf 的工程要求**（评审和第二轮核对发现的问题，全部采纳）：
  - `<Compile Include="../../src/**/*.cs" Exclude="../../src/obj/**;../../src/bin/**">`，`AssemblyName` 为 PinCC（App.cs:21 的 pack URI 写死了程序集名），XAML 用匹配的 Link 路径；
  - `<StartupObject>` 指向 OracleWpf 自己的入口（src/Program.cs 也有 `Main`，否则 CS0017）；
  - 照搬 src/PinCC.csproj 的 `<Using Include="System.IO" />`、ImplicitUsings、Nullable、LangVersion；
  - `<ApplicationManifest>../../src/app.manifest</ApplicationManifest>`，与真程序一样声明 PerMonitorV2；
  - 输出只进 `oracle/wpf/bin`，从不写 `src/bin`；
  - 放在 tests/ 之外，所以 `PinCC.Tests.csproj` 的默认通配不会把它编进来；不进 `build.cmd`；
  - 通过 W1c 的 `App.Configure` 得到与真程序相同的语言覆盖和 Theme；再调用 `Palette.ApplyChrome`；
  - **不建卡片的 HWND**：不调用 `EnsureHandle`，也不经过 `Desk.Adopt`（Desk.cs:194-199）；用反射把构造好的 CardWindow 放进 Desk 的 `_cards`。这样 `ScreenOf`、`MaxAutoHeight` 走 `Screens.ById(Mon) ?? Screens.Primary`（Desk.cs:259、263），只读伪造的缓存；有 HWND 时它们改读真实的 GetWindowRect 和 MonitorFromPoint，绕过假屏幕；
  - 对窗口的内容根 `Surface`（CardWindow.xaml:9 的 Border），而不是 Window 本身，按卡片尺寸显式 Measure/Arrange，做法同 SnapshotUi 的 Render（Desk.cs:1506-1512）：没有 HWND 的 Window 做 Measure/Arrange 不会布局子元素（dotnet/wpf Window.cs 的 MeasureOverrideHelper 在 IsSourceWindowNull 时返回 0,0）。然后把调度器推进到 ContextIdle，让 `MeasureAuto`（CardWindow.xaml.cs:338 以 Loaded 优先级排队）和 `CheckOverflow`（CardWindow.xaml.cs:612-628，`ActualHeight <= 0` 时直接返回）真的执行；
  - 固定屏幕：用反射把 `Screens._cache` 设为假屏幕、`_cachedAt` 设到很远的将来（Native.cs:40-52 的 2 秒缓存）；W4b 之后改为把屏幕作为参数传给 LayoutPass 和场景构建；
  - 断言是“从未显示、从未激活”，不是“进程里没有 HWND”：Desk 的字段初始化器会建一个 HwndSource（Desk.cs:24 的 MessageWindow）；
  - **哨兵检查**，每次生成都必须成立：40 行的样例卡片 `_overflow = true` 且 `AutoH == MaxAutoHeight`；每个场景期间每张卡片的 `Hwnd == IntPtr.Zero`；每个场景之后 `Screens.All()` 仍等于假屏幕；没有 `IsVisible` 的窗口、没有 Toast；`FrameworkElement.Language` 的默认值是 zh-CN；`TimeZoneInfo.Local` 仍是设定的时区；连续两次生成逐字节相同（G2）；再与一次 `tools/ui-snap.sh` 的屏幕外快照交叉核对一个场景。
- **实测值怎样比较**：
  - 只有原始文字高度允许差一行；`MeasureAuto` 的后处理（`Ceiling(PadTop + h + 3)`、夹到 MinH 和 MaxAutoHeight、只在编辑时变高、小于 1 的滞后）和 `CheckOverflow` 的 +2 余量作为原始高度的精确函数单独出向量；
  - C# 做一次敏感性分析：把每个布局场景的 AutoH 加减一行，列出结果会翻转（胶囊、隐藏、+N、✨ 标签）的场景。这些场景在 Mac 的核对里标为预期差异，其余场景必须完全相同。
- Mac 的 verify 模式把同样的场景画到屏幕外，生成与 WPF 并排的 PNG，由你人工检查结构、顺序、颜色和文字。

### 5.10 桌面编排一致

Desk.cs 不做整体抽取（那要 9 天左右，而且对每天在用的程序风险中等）。编排一致由五样东西保证：
1. W4a 抽出的每个决策函数都有向量：`CommitAction.Decide`、`MoveCommit.Decide`、`Due`/`Alarm`、`ReloadDiff`、`HotkeyPlan`、`StripText/StripRect/PeekRect`、`SummaryInput.Select`、`Stale` 等（完整列表见 §7）。
2. `contracts/desk-transitions.md` 和 `desk-commands.json` 逐条列出 Desk 的每个命令：操作名、写哪些列、分组策略、是否进撤销栈、立即还是防抖重排。台账对每个 Desk 方法做哈希，Windows 改了某个方法，对应的行就变成“过期”。
3. Swift 的 FakeHostScenarioTests（虚拟时钟、假屏幕、假窗口、真实的 Store）覆盖每一行。
4. **W4c 录制器在影子期间必须打开**（需要你同意，§12 第 6 条）。原因：FakeHost 场景和转移表出自同一个人的转写，同样的误解会同时出现在两边，只有真实轨迹能发现。
   - 很多转移取决于正文本身（编辑提交后的 Src 和 Kind、是否触发 Enrich、续列表、SummaryStale、InputHash），只有哈希的轨迹无法重算这些。所以录制器记完整正文，开始录制时另存一份数据库起始快照；这两样只留在你的 Windows 电脑上（录制目录和 artifacts/trace/），从不提交，也不拷到 Mac。`tools/replay-trace.sh` 在 WSL 里从起始快照开始重放，比较完整转储。提交进仓库的只有把正文换成哈希的派生轨迹，只用于不依赖正文的转移行。
   - P9 开始前，列出还没被真实轨迹命中的转移行，由你在 Windows 上按清单手工触发一遍（约 30 分钟，录制器开着）。P9 结束时仍然无法命中的行（例如拔插显示器、睡眠唤醒、全屏时提醒到期、编辑时 CLI 从外部写入、撤销条到期前已在别处撤销、热键重复）逐条写明原因，由你签字豁免，在 desk-transitions.md 里标为“只由 FakeHost 场景覆盖”。
5. **规范顺序**：W4d 让 LayoutPass 的输入和批量命令按 Id 排序。今天 `_cards` 的枚举顺序取决于进程自己的历史（按 `Store.Active()` 的显示顺序 Adopt，以及 Close、Rekey、临时负数 id，Desk.cs:104、194-216），几乎总与 Id 顺序不同，即使在两次 Windows 运行之间也不同，Swift 无法、也不应模拟它。LayoutPass 本身按调用方给出的顺序计算，向量把顺序作为输入，所以在 W4d 之前也能逐项比较。Swift 的 DeskCore 从一开始就按 Id 排序；为了让 W4d 之前的轨迹也能逐字节重放，W4c 同时记下每次批量命令和每次布局时 `_cards` 的枚举顺序，`replay-trace.sh` 把它作为输入交给 DeskCore 一个只在测试构建里可用的“注册表顺序”参数。W4d 落地（P12）之后，轨迹里的顺序就是 Id 顺序，这个参数随之删除。在此之前，两端正常运行时重叠自由卡片的推开结果和批量操作记录的先后顺序是 §1.4 登记的暂时差异。

### 5.11 移植台账

- `contracts/ledger.json`：`{id, cs:{file, member, sha256}, swift:{marker, sha256}, vectors:[…], status: ported | pending | platform-only(原因) | replicate(原因)}`，外加每个文件的覆盖表。
- **C# 成员扫描器**（`tests/Ledger/MemberScanner.cs`，零依赖）按词法工作：先识别普通、逐字、插值、原始字符串和字符字面量，再去注释、配括号；字面量内部不做空白规范化；成员前面的特性（如 `[GeneratedRegex(…)]`）算进成员范围。否则 Card.cs:117 里 `"file:///"` 之后的改动会被当成注释丢掉，CardWindow.xaml.cs:793、805 的两个空格字面量改了也看不出来。
- **变异自测**：对每个成员改一个字符，断言哈希随之改变。
- `dotnet run --project tests -- ledger check`：成员增加、删除、改变，或文件没有完全覆盖时失败。
- Swift 源码用 `// port: <id>` 标记区域。LedgerTests 用自己写的 SHA-256 重算每个区域的哈希，并要求可移植目标里的**每一行**都在某个 `port:` 或 `platform:` 区域里，所以共享的辅助函数也逃不掉。
- `ledger accept <id>` 记录新的哈希，只有该条目的向量在两端都通过时才成功。
- 覆盖范围：src/Core 的全部成员，src/UI 里移植部分的成员，Program.cs 的 CLI 路由。
- **非成员条目**：只看 C# 成员会漏掉两类会让向量过期的变化，台账另加两类条目，任一项变化都把相关向量族标为过期：
  - `src/UI/CardWindow.xaml`、`src/UI/Theme.xaml` 的整文件哈希，映射到 Metrics、CardScene、MdRender 族（圆角、内容区边距、编辑器底部留白、底部行高都在 XAML 里，CardWindow.xaml:9、17、19、26）；
  - 生成环境指纹：.NET 运行时版本、ICU 版本、winsqlite3 版本、zh-CN 的 `CompareInfo.Version`（Windows 更新或 SDK 更新会改变它们，src/ 却一行不变）；
  - P1 词法抽取的方法体内字面量表（§5.1）的源码段哈希。

### 5.12 数据字典完整性

C# 扫描 src/，Swift 扫描 mac/Sources，提取设置键字面量、SQL 列名、操作名字面量、CardMeta 属性、meta 的 State 字面量、数据目录下的文件夹名，与 settings-schema.json 和 DATA.md 比对。任何未登记的项都失败。settings-schema.json 同时声明每个共享键的取值语法和是否敏感；每个数据格式变化必须带一个 `data.features` 特性 id 和对应的样例。

### 5.13 Swift lint

LintTests 扫描可移植目标，遇到以下写法就失败：
- `String.count`、`prefix`、`suffix`、`split`（DotNetCompat 之外）；`lowercased()`、`uppercased()`、`caseInsensitiveCompare`；
- Store 和 Card 差量比较代码里（标记区域）String 之间的 `==`；
- `JSONEncoder`、`JSONSerialization`；
- 没有显式规则的 `.rounded()`；没有饱和辅助函数的 `Int(someDouble)`；
- 所有移植过来的整数运算（哈希、Px、Layout、LayoutPass）必须用 `&+`、`&-`、`&*`，与 C# 默认不检查溢出的回绕一致；
- `Calendar.current`、`DateFormatter`、`ISO8601DateFormatter`；
- Swift Regex 和正则字面量；`import SQLite3`、Compression、CryptoKit；
- 输出顺序有意义时遍历 Dictionary（必须带标记）；
- 在 `Home.resolve()` 之外拼数据目录；
- `Posix.swift` 和 `#if canImport(Glibc)` 那一行 import 之外的 `#if`（§3.2）；
- 视图文件里的中文字面量、几何数字字面量、对 Store 的直接访问（PinCCApp 目标）。

白名单文件要写明理由，只能减少，不能增加。

### 5.14 Windows 不回退

- **B0 基线**（P1，在第一次改 src/ 之前）：
  - `dotnet run --project tests` 的完整通过列表；
  - zh-CN 下的全部向量，另在不变区域和 de-DE 下各生成一遍，用来暴露区域泄漏；
  - CLI 录制；
  - OracleWpf 的卡片 PNG 和场景 JSON（每种状态的样例卡片），这是 B0 里能看到卡片的部分；
  - `tools/ui-snap.sh` 的屏幕外快照（管理窗口、选择器、菜单）。
- **tools/ui-snap.sh**（替代 `.probe/ui.sh`，不在 `.probe/` 下读写任何东西）：
  - 不依赖 WSLENV 传递变量：在同一个 cmd 进程里先设置变量、再启动程序，即 `cmd.exe /c "set PINCC_UI_SNAPSHOT=<目录>&& set PINCC_HOME=<目录>&& <PinCC.exe 路径>"`。启动前先用延迟展开执行 `cmd.exe /v:on /c "set PINCC_UI_SNAPSHOT=<目录>&& set PINCC_HOME=<目录>&& echo [!PINCC_UI_SNAPSHOT!][!PINCC_HOME!]"`，与预期的 Windows 路径逐字比较，不一致就拒绝运行（用 `%…%` 不行：同一行里的 `%VAR%` 在 `set` 执行之前就已展开）。运行之后再检查：ui-*.png 和 ui-errors.txt 出现在预期目录，pincc.db 只出现在临时的 PINCC_HOME 下。变量没传到时 `PinCC.exe` 会走 GUI 路径，找到互斥量并给你正在运行的实例发“显示全部”信号（Program.cs:24-29），或者打开你真实的数据目录；
  - `PINCC_HOME` 指向 Windows `%TEMP%` 下的新目录；
  - 屏幕外快照不加载卡片（Desk.cs:1461-1492 用的是一张从不显示的示例卡片），所以它只覆盖辅助界面，卡片由 OracleWpf 覆盖。
- C# 测试框架在任何 `Cli.Run` 之前断言 `Store.DefaultHome` 不是真实的 `%LOCALAPPDATA%\PinCC`。
- **G3**：在你的机器上记录 `CultureInfo.CurrentCulture`、`CurrentUICulture` 和 ICU 版本，基准固定为实测值（PinCC.csproj 没有开 InvariantGlobalization，程序也没有设置区域）。
- **真实数据普查**：每个 W2、W3 提交都要跑 `RealDataCensus`：打开最新一份备份的副本（从不打开活的数据库），对每张卡片分别用旧代码和新代码计算派生状态（Src、Kind、Title、Note、NoteLine、InputHash、SummaryStale、Progress、块类型、Tag），打印每一张有变化的卡片。任何非空的差异都要你签字，否则收窄规则。
- **先录特征再移动**：W4a 的每个源文件，先由 OracleWpf 对未修改的代码录下输出，移动后必须重现。只调用不显示窗口、不经过 DoLayout 的实例方法（§5.9 的限制）；会调用 Show 或 DoLayout 的路径（NewCard、CreateFrom、Ingest 新建卡片、Tile、CollapseAll、ShowAll、PinScreen 等）只做源码哈希和人工审阅，等 W4a 抽出纯函数后再出向量，批量命令的布局结果在 W4b 之后经 `LayoutPass.Compute` 录制。分文件提交，每个提交各自试用。
- **影子只留给 LayoutPass**：CommitAction、MoveCommit 等决策和副作用（结束编辑、写库、提示、提示音）交织在一起，“新旧并行”最后比较的其实是新代码和新代码，所以不做。LayoutPass 的影子输出写进单独的 `shadow.log`：有大小上限、去重、卡片正文只记哈希，不进备份和导出，不写进你看得到的 error.log。
- 自动检查从不启动你的实例，也不给它发信号；`.probe/` 从不读写。

### 5.15 变更流程

以后每次改 Windows 逻辑，C# 先改，它始终是基准：
1. 在 Windows 上照常修改 src/ 和 C# 测试。
2. 运行 `tools/sync.sh`（或 `tools/check.sh`）。它报告两件事：
   - 与新行为不符的向量和轨迹。用 `dotnet.exe run --project tests -- vectors --expect <族>` 重新生成；contracts/ 里的差异就是这次规范变化的审阅记录；
   - C# 成员哈希变了的台账条目，标为“过期”，并列出受影响的 Swift 标记和向量文件。
3. C#、contracts 和台账放在一个 `spec:` 提交里。Windows 版可以立即发布，从不被 Swift 卡住。
4. 在 Swift 里跟进：WSL 里 `swift test` 直到所有向量、轨迹、录制和 lint 都通过；改到 Store、CLI 或数据代码时再跑 interleave.sh；然后 `ledger accept <id>`。Swift 跟进可以攒起来批量做（例如每周一次）。
5. 在两台 Mac 上运行 `tools/mac.sh test`。数据相关的改动还要运行 `tools/mac.sh fixtures`，提交重新生成的 `mac-*` 样例，由 C# 的互开检查在 Windows 上验证。
6. 只要台账有过期条目、这台 Mac 上有向量失败、数据字典检查失败，或者最近一次 check.sh 不是对当前 HEAD 跑的，`tools/mac.sh app` 就拒绝构建 Mac 版。走样的 Mac 版发布不出去。

特别规则：
- **数据格式变化**（新列、设置键、操作名、meta 键或状态、JSON 形状、目录结构、表结构版本）：同时更新 DATA.md 和 settings-schema.json，分配 `data.features` 特性 id，两端各生成新样例，并写明两端的发布顺序。表结构版本升级必须两端都发布之后才能写 v2 数据（旧程序会只读，Store.cs:61）。
- 新的 C# 成员在台账里显示为“未映射”，必须映射，或标为 platform-only 并写明原因。
- 只改 Windows 界面样式（XAML 颜色、不在 Rules 或表里的间距）不进成员台账；但 CardWindow.xaml、Theme.xaml 的整文件哈希会把 Metrics、CardScene、MdRender 族标为过期（§5.11），重新生成后如果场景内容不变，直接 accept；变了就按正常流程跟进。
- 改 Rules.cs 的函数或 Desk 的转移行，要同时更新 desk-transitions.md，并重新审阅对应的 FakeHost 场景。
- Mac 专有的工作（AppKit 代码、Mac 表、PlatformText、KeyBindings.Mac、KnownConflicts.Mac、`mac.*` 设置）可以自由修改，但只能在平台目标和 Mac 表里。改了可移植的 Swift 目标而 C# 没有对应改动，会让 Swift 侧的台账哈希失败。
- 在 Mac 上发现、但 C# 也有的问题，先在 C# 修（或明确记为“照搬”），再走正常流程。
- 预计成本：一次逻辑改动约为只改 Windows 的 1.5–2 倍。

### 5.16 在哪里跑、什么时候跑

| 入口 | 内容 | 预算 |
|---|---|---|
| `build.cmd` | 保持现状：现有测试，加一个有硬性时间上限（约 +10 s）的冒烟检查。数据字典、集合字面量扫描、时钟禁用（W1d）、LocalPath 扫描（W3b）这些只看 C# 源码的规则检查也在默认测试集里：它们失败说明 Windows 代码本身违反了规范，应当挡住。**从不因向量或台账过期而失败** | 构建时间增加不超过约 10 s |
| `tools/check.sh` | C# 测试（dotnet.exe）、向量新鲜度（重新生成后比较输出）、台账（比较成员哈希、XAML 整文件哈希和生成环境指纹，§5.11）、`swift test`、lint、交替读写；`--deep`、`--fuzz` 按需。每次运行把结果和当时的 HEAD 记到 artifacts/ | 默认几分钟 |
| `tools/mac.sh test` | 两台 Mac 上的 `swift test`；`--intel` 把 `pincc-selftest` 复制到不能编译的 Intel Mac 上原生运行（§9.1） | — |
| `tools/mac.sh app` | Mac 发布闸门：台账无过期、全部向量通过、数据字典通过，并且最近一次 `tools/check.sh`（按输出判断新鲜度）的记录对应当前 HEAD，才构建 | — |

不设强制的 git pre-push 钩子：仓库没有远端，唯一的推送目标是 Mac 上的裸仓库，而推送本身就是为了在 Mac 上测试；Windows 上的 git 客户端也没有 swift。仓库体积设预算（G6），contracts/ 的增长在每个阶段结束时检查。

---

## 6. C#→Swift 语义差异与兼容层

下表中“规则”一列是 Swift 端必须遵守的写法，“保障”一列是发现违反的机制。

| # | 差异 | C# 代码位置（举例） | Swift 默认行为 | 规则 | 保障 |
|---|---|---|---|---|---|
| 1 | 长度和索引 | 所有 `.Length`、`[..n]`：Fetch.cs:154、355；Cli 的 Line 把标题截到 60 个单元（Cli.cs:252），`#Id` 和 log 的列宽填充（Cli.cs:107、245），Md 不截断；完成条标题 14 个单元（Desk.cs:490）；摘要失败信息 120 个单元（Desk.cs:1243）；卡片库历史预览 40 个单元（ManageWindow.cs:145） | `String.count` 数字素簇 | 一律按 UTF-16 单元：`Array(s.utf16)`、DotNetCompat.Utf16 | 向量，lint |
| 2 | 字符串相等 | Store.cs:151-153 列差量 | `==` 按规范等价，NFC 与 NFD 相等 | 序数比较（UTF-16 逐单元） | 操作脚本（只改 NFC/NFD 的编辑仍要写库），lint |
| 3 | CRLF | Card.IsUrl 的 `Contains('\n')`（Card.cs:212），Markdown 分行 | `"\r\n"` 是一个 Character | 在 UTF-16 上分割、查找 | 向量（CRLF 正文） |
| 4 | Split 保留空项 | `Split('\n')` | `split` 默认丢弃空项 | DotNetCompat 的 split | 向量，lint |
| 5 | Trim 的空白集合 | `Trim()` 用 `char.IsWhiteSpace` | `whitespacesAndNewlines` 集合不同（U+200B、U+FEFF、U+180E） | 按 .NET 表 | 向量 |
| 6 | 大小写 | `ToLowerInvariant`（Card.Tag、When.Parse） | `lowercased()` 做完整映射，受 ICU 版本影响（İ、ß、Σ） | 按 .NET 表做简单映射 | 向量，lint |
| 7 | 取整 | Desk.cs:601-602、639-640（w/h），CardWindow.xaml.cs:640（透明度一位小数） | `.rounded()` 四舍五入远离零 | `.toNearestOrEven`；`Math.Round(x, d)` = `(x·10^d).rounded(.toNearestOrEven)/10^d` | 向量，lint |
| 8 | 整数溢出 | Card.Fnv（Card.cs:217-222），Native.cs:9-17 的 Px 运算 | 溢出陷阱，程序崩溃 | `&+`、`&-`、`&*`；FNV 遍历 `utf16` | lint，毒样例 |
| 9 | 双精度转整数 | 各处 `(int)`、`(long)` | NaN 或越界时陷阱 | `(int)`、`(long)` 强制转换：饱和（.NET 9+ x64 的行为）。`Convert.ToInt64(double)`（Card.cs:76-77，读整数列里的 REAL）：半数取偶，越界时抛 OverflowException，不饱和；Swift 同样报错，走 W3f 的单行隔离 | 向量，lint，整数列的毒样例（§5.7） |
| 10 | 整数解析 | `int.TryParse`（Store.cs:322，Cli 参数） | `Int(String)` 规则不同 | NumberStyles.Integer / None 的四种解析方式，含 zh-CN 当前区域的写法 | 向量（`" 12"`、`"+5"`、`"12.0"`、`"true"`） |
| 11 | 双精度文本 | CLI 文本和 JSON | `description` 写 `260.0`、`1e-05` | NetDouble：`260`、`1E-05` | 向量 |
| 12 | JSON 写入 | Card.cs:47、98，CardMeta.ToJson（Card.cs:249），Cli.cs:305 | JSONEncoder：`/` 转义成 `\/`、键顺序随机、小写 `\u` | 三种 StjWriter：默认紧凑写出 null（Card 快照、images）；默认加 WhenWritingNull（CardMeta）；缩进加 UnsafeRelaxed（CLI）。转义集合来自 .NET 表，十六进制大写 | 字节向量，操作脚本，lint |
| 13 | JSON 读取 | Card.FromJson、Store.Restore | Codable 的键匹配、数字宽容度不同 | StjReader：PascalCase 区分大小写，整数严格，重复键取最后一个。孤立代理项转义（单独的高代理，或以低代理开头）与 STJ 一样判为错误：STJ 抛 InvalidOperationException，经序列化器成为 JsonException（Card.FromJson、`Card.Info` 随之抛出），不替换成 U+FFFD | 宽容读取向量，含这两种情形，期望 `{throws: json}` |
| 14 | 字典顺序 | `_cards`、llm.env 白名单 | Hasher 每个进程随机 | 有序映射；需要顺序的地方按 Id 排序（W4d） | lint，布局向量 |
| 15 | 排序稳定性 | `List.Sort` 不稳定，LINQ `OrderBy` 稳定 | `sort` 稳定 | W2c 让 Card.Compare 成为全序；Layout 的 OrderBy 显式加索引打破平局；`MaxBy`/`min(by:)` 取第一个 | 向量 |
| 16 | 本地时间换算 | When.cs:9-11、33、81，Cli.cs:303 | `Calendar.date(from:)` 歧义时间取第一次（夏令时），跳过的时间按未开始处理 | LocalDateTime 带歧义标记：从 UTC 转换时设置，`Date`/`Add*` 保留，按 tick 构造时丢弃；换成 UTC 时有标记用夏令时偏移，无标记的歧义和跳过时间用标准偏移 | 时区网格向量 |
| 17 | 日历 | `DayOfWeek`、`AddYears` | Calendar 在 1582 年前是儒略历 | 前推公历、周日 = 0、`AddYears` 的 2 月 29 日夹紧；年份越界映射为 nil 或退出码 1 | 向量 |
| 18 | 正则方言 | 47 + 10 处正则 | ICU 的 `.`、`$`、`\s`、`\w`、`\b` 不同，有回溯上限 | §5.4 的翻译规则或手写扫描器 | 正则清单 |
| 19 | Uri | Card.cs:116-117 `Uri.TryCreate`、`LocalPath` | `URL(string:)` 宽容度、IDN、反斜杠处理不同 | NetUri 只实现用到的子集 | URL 向量 1 万条以上 |
| 20 | 路径 | `Path.GetFileName` 等 | Foundation 按 `/` 处理 | WinPath：同时认 `\` 和 `/` 的 Windows 语义 | 向量 |
| 21 | IP 地址 | Fetch 的 IsPublic | `inet_pton` 不接受 inet_aton 形式 | NetIP 接受 inet_aton 形式和 IPv6；否则 Mac 会放行 Windows 挡住的主机（安全分歧） | URL 向量 |
| 22 | HTML 实体 | `WebUtility.HtmlDecode` | NSAttributedString 的 HTML 解析完全不同 | HTML4 实体表 + 数字实体的怪癖 | 向量 |
| 23 | 代码页 | Fetch.cs:185-229，Proc 的输出解码 | 没有 GB18030 常量；ICU 73+ 按 GB18030-2022 改了部分映射 | .NET 导出的解码表和 `?` 回退；标签表也从 .NET 导出 | 随机字节向量 |
| 24 | SHA-1 十六进制 | Card.Hash 大写取 16 位；图片名小写取 20 位 | — | 两个辅助函数分开 | 向量 |
| 25 | SQLite 文本 | Db.cs:119、142 用 `*16` 接口 | 常见写法用 UTF-8 接口 | 同样用 `bind_text16`、`column_text16` | 非法 UTF-8 行的转储（§5.5） |
| 26 | 异常与退出码 | Cli 按异常类型映射退出码 | Swift 的 `throws` 没有类型层次 | 显式的错误枚举 → (退出码, 是否打印用法) | CLI 录制 |
| 27 | 孤立代理项 | 剪贴板、命令行参数、截断 | String 不能表示 | 经 SQLite UTF-16 接口，两端同样转换。W2f 只处理 Fetch.cs:154、355。Cli.cs:252 的截断仍可能留下孤立代理项，CLI 输出把它写成 EF BF BD，Swift 照搬；Desk.cs:490、1243、ManageWindow.cs:145、CardWindow.xaml.cs:332 只影响显示，登记在 §1.4 | 向量，CLI 录制 |
| 28 | 子进程环境 | Proc 合并环境变量 | `Process.environment` 是替换 | posix_spawn，自己合并环境 | Proc 契约测试 |
| 29 | SIGPIPE 和 FileHandle | — | 写已关闭的管道会杀死进程；FileHandle 抛 ObjC 异常 | 忽略 SIGPIPE；用 `write(2)`/`read(2)` | Proc 契约测试 |
| 30 | 文件修改时间 | Store.cs:284、Fetch.cs:353 | `FileManager` 返回 Date（双精度） | `stat` 的整数运算得到毫秒 | 向量 |
| 31 | 目录枚举顺序 | 文件夹卡片的列表 | APFS 与 NTFS 顺序不同 | 先排序再截取（W2k） | 文件夹样例向量 |

---

## 7. 逐模块移植清单

| C# | Swift | 做法 | 验证 | 重点 |
|---|---|---|---|---|
| .NET 基础库（字符串、字符、大小写、NumberStyles、Math.Round、转换、DateTime/TimeZoneInfo、System.Text.Json、Regex、Path、Uri、IPAddress、WebUtility、Encoding、SHA1） | `DotNetCompat/*` | 按 .NET 导出的表重写一小部分，不经 ICU 和 Foundation | 每个辅助函数一个向量文件 | §6 全部 |
| `Db.cs`（打开、pragma、绑定和读取、Tx、Scalar、错误、data_version） | `PinCCCore/Db.swift`，经 CSQLite | `sqlite3_open_v2(READWRITE\|CREATE\|FULLMUTEX)`；busy_timeout 2000 / 5000；`journal_mode=WAL; synchronous=NORMAL`（Mac 加 `checkpoint_fullfsync`）；`enum SQLValue`；**文本用 `bind_text16`/`column_text16`**；NSRecursiveLock；Tx 为 BEGIN IMMEDIATE / COMMIT / ROLLBACK，包括 C# 回滚时掩盖异常的行为；Scalar 返回第一个非 NULL；错误带 rc | 类型边界样例（NUL、CRLF、emoji、双精度、接近 2^63 的整数）；操作脚本；第二个连接持有 BEGIN IMMEDIATE 时（SQLITE_BUSY）和共享缓存加锁时（SQLITE_LOCKED）退出码都是 4，CLI 录制分别覆盖 | UTF-8 存储；Int64 毫秒；可重入锁；每连接的 data_version；defer 里 finalize；“忙”的判断在 W2j 之前照搬 Cli.cs:126：消息含“(5)”或“(6)”都算忙，W2j 之后按结果码判断这两种 |
| `Store.cs:29-99`（Schema、打开、Migrate、PreRelease、ReadOnly、DefaultHome、Active/All） | `Store+Open.swift`，`Generated/Schema.swift` | 同样的判断顺序；建表语句逐字节生成；最新版本打开时不写；Migrate 在 BEGIN IMMEDIATE 里复查；数据目录只经 `Home.resolve()`；每次打开都建 img/；排序用全序的 `Card.compare`；`data.features` 检查（W3e） | 新库、v1、发布前、v2、未知特性样例 → 转储 + 只读标志 + data_version 变化量；交替读写 | 无 ORDER BY 时保持 rowid 顺序 |
| `Store.cs:105-194`（Insert、Mutate、Write、Journal、InTx、Group） | `Store+Write.swift` | 同样的 SQL 和语句顺序：先 INSERT grp=0 再 UPDATE grp=id；列差量用序数比较和 .NET 的 `double.Equals`；`updated_at = max(now, prev+1)`；cols 按首次出现合并；InTx 保存和恢复 Group 状态；`journal:false` 推进该组的 rev；写入前校验 JSON 列（§4.2.7） | 操作脚本 300 个种子（`--deep` 5,000），含双连接和 ifRev；交替读写 | `''` 与 NULL 的 cols；RevConflict → 退出码 3 |
| `Store.cs:203-264`（Ops、Restore、Forget） | `Store+History.swift` | 逐行移植；快照用 StjReader 读（所有字段可缺省，缺省值同 C#）；id 按首次出现返回；行已不存在时重新插入且 rev 为 1；撤销新建即设置 deleted_at；负数 LIMIT 原样传下 | 覆盖每个恢复分支；交叉恢复；cols 为 NULL 的旧样例 | Forget 不留墓碑（L2 要注意） |
| `Store.cs:270-338`（Purge、ImgRefRx、Backup、SaveImage、ResolveImage、设置） | `Store+Files.swift`、`Store+Settings.swift` | Purge 用同一个正则（W3c 之后两端同时修正）、已用文件名集合按 OrdinalIgnoreCase、整数修改时间、时间下限经 When.Clock（W1a）；Backup：VACUUM INTO（引号加倍）、不变区域公历日期、保留 7 份、序数排序、删除失败不报错；SaveImage：小写 SHA-1 前 20 位 + 扩展名，文件不存在时原子写入；设置只提供带类型的写入 | Purge 向量（转义与不转义的斜杠、扩展名后的中文、修改时间）；交叉 Purge 保留测试；备份向量；设置解析向量 | 转义斜杠导致图片被删；img/ 里的 .DS_Store 和 `._*` 文件；导入文件的修改时间 |
| `Card.cs:14-99`、`228-250`（字段、Columns/Values/FromRow/With、ToJson/FromJson、CardMeta） | `Card.swift`、`CardMeta.swift` | 21 个属性，默认值相同；列数组由表生成；`values()` 的 images 写 NULL 或默认 StjWriter；`init(row:)` 的 NULL 回退、标志 `!= 0`、`Int32(truncatingIfNeeded:)`；Meta 在快照里保持为不透明字符串 | ToJson、CardMeta.ToJson 字节向量；宽容读取向量；C# 写的行由 Swift 读出再写回必须逐字节相同 | PascalCase 顺序；`/` 不转义；大写 `\u`；整数不写成 `1.0`；`[]` 与 NULL |
| `Card.cs:103-137`、`169-184`、`212-213`、`224`（FirstLine、Src、PathRx、file URI、IsUrl、Note、NoteLine、Kind、Title、ImageOnly） | `Card+Source.swift`、`Card+Derived.swift` | UTF-16 上按 10 分割保留空项、.NET Trim、只对 ASCII 的 `Trim('"','<','>')`；PathRx 手写扫描器，用 W3a 的语法；`file:///` 经 NetUri.localPath（Windows .NET 语义）；IsUrl 是 NetUri 的接受器；Title 用 WinPath.getFileName，字面量“图片”；`ForeignPath` | Windows 上生成的 1 万条以上向量（URL、IDN、含盘符 / UNC / `/Users` / `/tmp` 的 file URI、W3a 各固定前缀、仍是笔记的 `/home/…` 和 `/mnt/…` WSL 路径、引号、U+3000、CRLF 正文） | Uri 的宽容；以 `/` 开头的笔记的保护 |
| `Card.cs:104`、`139-167`、`186-222`（Info、SrcInfo、InputHash、SummaryStale、Hash、Tag、Fnv、AutoColor、ShownColor、Progress、Compare、Overdue） | `Card+Hash.swift`、`Card+Color.swift` | Hash 是 UTF-8 上的 SHA-1、大写、前 16 位；TagRx 手写扫描器，只认 BMP 的 `\p{L}\p{N}`，`{1,32}` 按 UTF-16 数；FNV-1a 遍历 utf16 并用 `&*`；轮换用 `max(Id,0) % 7`，未知模式走默认分支；Compare 含 W2c 的 Id 打破平局；Overdue 用 When.nowMs | 哈希语料；含 İ、Σ、ß、NFD、辅助平面字符和 33 单元标签的向量；AutoColor 全模式；Compare 的符号和平局集合 | 大小写十六进制；简单与完整映射 |
| `When.cs` | `When.swift`，基于 LocalDateTime | 模仿 DateTime 的朴素 tick 运算；只在 ToMs/FromMs 换算（§6 第 16 条），1–9999 年之外抛出并映射为 nil 或退出码 1；Parse 按同样的流程：Trim、简单小写、序数替换，再依次 RelRx、DateRx、DayWordRx、WeekRx、TimeRx；Unicode `\d` 匹配但解析失败时整体为 nil；Format 手工拼（HH:mm，月日不补零） | 语法模糊 × 时间网格 × 三个时区；每小时、每天的 Presets；天数差 −400..+400 的 Format；纽约和柏林的夏令时切换日，含回拨小时的两次出现 | 夏令时；Sunday = 0；double.Parse 得到无穷大；全角数字；分支顺序 |
| `Markdown.cs` | `Markdown.swift` | 在 `[UInt16]` 上逐行移植；块分类用 .NET 的 `\s`、`\d` 表手写扫描；围栏判断用序数（W2a）；行内锚定匹配用 NetRegex 的 `.anchored` 或扫描器；IsWordEdge 按 UTF-16 单元用 CharInfo；UrlRx 的 IgnoreCase 只对 ASCII；Plain 按同样顺序替换；ToggleTask 只规范 CRLF，单独 CR 的毛病照搬；ContinueList 的全角数字按 W2e | 2 万行模糊（`--deep`）/ 5k 行提交：ZWSP、BOM、U+00AD、NUL、U+2028、U+0085、VT、U+3000、辅助平面、emoji ZWJ；Inlines、Plain、ToggleTask、ContinueList（含 `١.`） | RuleRx 的回溯性能 |
| `Cli.cs` + `Program.cs:37-66` | `PinCCCli`（库 + `pincc`）+ GUI 进程内路由 | 有序参数数组（报告第一个坏参数，重复时取最后一个）；四种整数解析；在事务之前按同样顺序解析输入；ImageExt 用 CLI 自己的规则；文件存在检查区分目录；显式错误枚举；Line 和 Md 按 UTF-16 截断和填充；Dto 用缩进 + UnsafeRelaxed 的 StjWriter，嵌套 CardMeta 写出 null；Iso 手工写 ±HH:MM（不用 Z）；Help = HelpCommon + 平台片段；`export`/`import`（W5 之后） | CLI 录制 × {win, mac} 样例 × {C#, Swift 进程内, Swift 子进程}；解析器、OpFor、ImageExt 向量 | `--` 和 `-x` 当位置参数；取值参数吞掉 `--x`；标准输入在 NUL 处截断；GB18030 回退；透明度 `1` 与 `1.0` |
| `Fetch.cs:187-229`（Decode、Text、StrictUtf8、Utf8Complete、CharsetRx）+ `Proc.cs:68-94` 的输出解码 | `TextDecode.swift` | 按字节嗅探 BOM（与 StreamReader 相同，含 UTF-32LE `FF FE 00 00`）；手写严格 UTF-8 校验（接受 NUL 和非字符）；再用 .NET 的 GB18030 表和 `?` 回退；charset 标签表从 .NET 导出；前 4 KB 按 ASCII 扫描，≥0x80 视为 `?`；走指定编码时保留 BOM | 1 万个随机字节串 + 手写用例 | 整段变乱码的情况也照搬 |
| `Fetch.cs:14-30`、`134-155`、`232-259`、`463-517`、`556-593`（Page、FromHtml、M/Attr、MainText、KnownContainers、JsonLd、Snippet、Cap、Clean、Host、HtmlDecode） | `HtmlExtract.swift`、`JsonLd.swift`、NetUri.host | 在 NSString/UTF-16 上逐步移植；惰性块模式用扫描器；`Element()`、`Attr()` 和运行时拼出的正则用扫描器；MaxBy 取第一个最大值；JsonLd 用类似 STJ 的严格解析器（深度 64、拒绝 BOM、重复键取最后、单块出错时保留已得结果）；Date 取前 10 个 UTF-16 单元（W2f）；在主线程之外运行 | 保存的网页语料（微信、知乎、CSDN、GitHub、GBK、新闻）→ Title、Desc、Site、Date、Text、Thin、Hash(Text)；对抗性合成页面、2 MB 页面、实体表 | 阈值按 UTF-16；`>` 后接 U+0338；ICU 回溯上限；`''` 会截断 `??` 链；写进正文的 Host() 逐字节相同 |
| `Fetch.cs:267-330`（SafeToAuto、IsPublic、SecretQuery、LocalSuffixes）和 `277-294` SafeToAutoAsync | `UrlGuard.swift`；`PinCCMacPlatform/Dns.swift` | NetUri（小写 scheme、UserInfo、DnsSafeHost、Query）、NetIP、逐字节移植 IsPublic；查询串按 `&` 和 `;` 分割，先把 `+` 换成空格再宽容地反转义；DNS 用 getaddrinfo 解析 IDNA 主机，4 秒超时 | 数千条 URL 向量加现有 FetchTests 列表 | inet_pton 放行 Windows 挡住的主机；`removingPercentEncoding` 返回 nil；ICU 完整大小写折叠（paßword） |
| `Fetch.cs:335-458`（File、FileText、OfficeText、DocExt、FileName、IsPdf、ImageExt、SaveDownload） | `FileFacts.swift`、`ZipReader.swift`（CZlib 原始 inflate）、`WebRoute.swift` | stat 的整数修改时间；FType 用简单大写映射和字面量“文件夹”；文件夹列表先排序再取 40 项（W2k）；Desc 前 300 个 UTF-16 单元（W2f）；ZIP 读取器解析中央目录，支持 stored 和 deflate、数据描述符，60 MB 上限，Deflate64 标为不支持；Office 文本用同样的正则步骤，幻灯片顺序由 `int.TryParse` 溢出为 0 决定；二进制判断是前 8000 字节里有 NUL；任何错误返回 `''`；SaveDownload 用小写 SHA-1 20 位、原子写入；Mac 的 .app、.rtfd、iWork 包算文件 | 固定修改时间的文件语料 → CardMeta.ToJson、文本、TextHash；slide10 排在 slide2 前、平局、ZIP64 的 Office 样例 | 下载文档的 TextHash 照搬各自的绝对路径（§3.5.2） |
| `Fetch.cs:33-131`、`158-184`、`523-553`（HTTP 客户端、GetChecked、WebCore 路由、ReadCapped、Rendered/Edge） | `PinCCMacPlatform/Web.swift`（URLSession）、`WebKitRenderer.swift`；纯路由在 `WebRoute.swift` | 每个 C# 客户端一个临时会话，忽略缓存，请求头字节与 Windows 相同（User-Agent、Accept、Accept-Language、Accept-Encoding）；重定向代理停止自动跟随，自己数 8 跳、拒绝 https→http、自动读取时每一跳先检查；可重置的总期限（15 s，然后 60 s）；Content-Type 和 Content-Disposition 用 .NET 兼容的语法解析，不用 `mimeType`、`suggestedFilename`；上限按解压后的字节；超时文案“超时”；`NSAllowsArbitraryLoads`；Rendered 优先用已安装的 Chrome 或 Edge，否则 WKWebView（不持久化，不在数据目录里） | Mac 测试里的回环录制服务器（原始状态、头和正文：gzip/br，上限 max−1/max/max+1，401/403/429/503，1–9 跳重定向，RFC 5987/2047）；路由决定在 WSL 里出向量 | ATS；嗅探的 MIME；压缩后的 Content-Length；空闲超时与总超时 |
| `Llm.cs`（Table、Tokens、构造规范化、Resolve/Save/Problem、Prompt、Defang、OneLine、DefaultTemplate、StripFence、Ansi、Detect/Scan 解析、LoginEnv 解析；W4a 从 `Ask` 抽出的 `PreparePrompt`、`Outcome`） | `PinCCCore/Llm.swift`（纯）；`PinCCMacPlatform/ToolHost.swift` | 工具表和模板由 C# 生成；并发上限 2（Llm.cs:51）和原生 150 秒超时（Llm.cs:210，WSL 的 180 秒只属于 Windows）写进数据表；ToolHost 调用 `PreparePrompt`（gemini/qwen 的 `@` 转义、ollama 缺模型的报错）和 `Outcome`（去 ANSI、Tail 300、“退出码 N，没有输出”、StripFence），不自己重写这些规则；Tokens 作为真正的 argv 传递（保留空项）；Prompt 手工单遍替换，ReplaceLineEndings 按 .NET 的集合（不含 VT）；StripFence、ANSI 去除逐行移植；环境白名单按插入顺序；Mac 解析器只读 `mac.llm.*`，捕获登录 shell 环境，与进程环境合并，搜索 PATH 候选；工作目录是文档所在文件夹或 `~/Library/Caches/PinCC/llm`，并检查存在 | Prompt 向量（CRLF 模板，值里含 `{content}`、`$1`、`\`）、各工具各模式的 Tokens、StripFence、Ansi、构造规范化、Resolve、PreparePrompt、Outcome；Mac 专有的解析器测试 | Windows 的 `llm.*` 从不读写；Llm.cs:197 的 `/` 工作目录捷径不照搬（Mac 路径都以 `/` 开头） |
| `Proc.cs`（Run、Sink、Job 对象） | `PinCCCore/Proc.swift`（POSIX，WSL 可测） | posix_spawn + POSIX_SPAWN_SETPGROUP；忽略 SIGPIPE；后台线程用 `write(2)` 写标准输入；两个管道都读完；8 MB / 1 MB 上限；一个总期限覆盖写标准输入；killpg(SIGTERM) 再 SIGKILL；3 秒管道宽限；合并而不替换环境；启动错误映射到同样的“吞掉”类别。Job 的 KILL_ON_JOB_CLOSE 用 applicationWillTerminate 里的 killpg 近似（尽力而为，已记录；调用 setsid 的孙进程收不到） | 共享契约测试，用一个很小的辅助子进程（回显、睡眠、忽略标准输入、非法 UTF-8、持有管道的孙进程），WSL 和两台 Mac 都跑 | SIGPIPE；FileHandle 的 ObjC 异常；信号退出码；孤儿进程 |
| `Layout.cs` + `Native.cs:7-35`（Px、Screen、Tile、Place、PushOut）+ W4 之后的 Trim/Spot/HubRect | `PinCCDesk/Layout.swift`、`Geometry.swift` | Int 运算直接移植，`&+` 等；Screen.Px 用 NetRound；OrderBy 的稳定性用索引显式保证；`min(by:)` 取第一个最小值；保留 `W == 0` 表示“没有命中”；整数除法截断；保留 ClampInto | 现有 Layout 测试转为向量，加 C# 生成的随机场景；毒样例 | 奇数像素的半数取偶 |
| `Desk.cs:798-892` DoLayout（W4b 之后是 LayoutPass.Compute） | `PinCCDesk/LayoutPass.swift`；应用步骤在 PinCCApp（一个 CATransaction） | 纯函数，输入按 Id 排序（W4d）；忙碌的卡片只向下长到下一个障碍物；自由卡片逐个推开；未知显示器按主屏平铺；悬停推迟按显示器，保留上一次的溢出状态；到期折叠规则；图片胶囊在右边；可见性规则；重排的同步与 100 ms 防抖留在 DeskCore | LayoutPass 向量（≥2,000）；Windows 影子日志 10 天为零；Mac verify 渲染同样的场景 | DIP 与像素的取整；Mac 的 y 翻转只在 MacScreens 里做一次 |
| `src/UI/Rules.cs`（W4a）：NaturalSize、ZoomSize、ZoomTarget、Budget、MaxAutoHeight、PicturePlan、CardGeometry（DipSize、Expanded、MinSize）、Size、Breakable、LinkCaption、FileFacts、Chips/Actions 规格、Frame、ShownOpacity、AutoHeight（后处理）、Overflow（+2 余量）、EditorOps.Apply、CommitAction.Decide、OnExternal、ReloadDiff、StripText、StripRect、PeekRect、MoveCommit.Decide、Due、Alarm、FileChanged、LinkMeta、FileTextHash、SumHashFor、NeedsSummary、SummaryInput.Select、Stale、HotkeyPlan、ReminderOptions、ReminderHint、Library 的 Match/In/Facts/HistoryLine、NextColor、ColorOf、IsCliInvocation、DeskSettings.Parse、SettingsSchema.PromptToStore、HubRect、Dial、Badge、SnapCorner、HitTest、KeepAspect、EffFree（W3d） | `PinCCDesk/Rules.swift` | 每个静态函数一对一移植，台账逐个映射 | 每个函数一个向量文件（固定时钟、时区、假屏幕） | 半数取偶（W/H、行高、透明度一位小数、缩放边带）；百分比和数字格式按 zh-CN 写死（`P0` 为 `80%`，`0.#` 远离零舍入）；UTF-16 截断；墙上时间的稍后提醒；过期结果丢弃；SumHash 哨兵 |
| `Desk.cs` 的编排（Start、Maintenance、Change、Push、Gesture、Undo、NewCard、CreateFrom、Ingest、粘贴、编辑会话、完成与撤销条、单卡命令、临时展开、缩放、OnUserMoved、批量命令、Open、Hub、Tick、RefreshFiles、Poll/Reload、Enrich、Summarize、热键、菜单、Notify、Spot） | `PinCCDesk/DeskCore.swift`（+ EditSession、UndoStack、GestureClock、MenuSpec 构建） | 按 desk-transitions.md 和 desk-commands.json 编写；所有决定调用 Rules.swift；操作名只来自表；卡片注册表按 Id 有序；定时器经 IScheduler，常量来自表；提醒按 1 秒网格评估；Mac 专有的规则（`mac.*` 设置、SMAppService、异机路径的运行时状态）在接口后面 | FakeHost 覆盖每一行转移；PINCC_TRACE 重放；操作效果用脚本转储格式检查（FakeHost 用真实的 Store） | 撤销去重和移到栈顶；600 ms 手势组；编辑中 CLI 写入另起一组；撤销条保留整张卡片的矩形作为障碍物；每进程只响一次；每批一声；enrich 的 `_reenrich` 标记；`journal:false` 的 meta；自动摘要不进撤销栈 |
| `CardWindow.xaml.cs:137-653` + `MarkdownView.cs` + `Palette.cs`（1040-1076 的 HitTest、KeepAspect 归入下面 Rules 一行；1002-1037 的 WndProc 在台账里标为 platform-only） | `CardPresenter.swift` → CardScene；`MdRender.swift`；`Generated/Palette.swift`；`PinCCApp/CardPanel`、`CardContentView`、`MdAttributed` | Presenter 用 Rules 和表构建 CardScene；AppKit 视图只负责画；MdRender 按 MarkdownView 的规则（缩进 16；段间距 10、2、6；标题字号；• 和 ◦；引用条 2.5；代码块样式；方框 14）；颜色在 sRGB 里创建 | OracleWpf 的 CardScene、MdRender 向量；Mac 屏幕外渲染与 Windows 并排比较 | 置顶标签的 `P0` 百分比；Size 的 `0.#`；图片 `0.6×room`（以代码为准，不是注释）；文字胶囊用 W 而不是 `max(W,160)` |
| `CardWindow.xaml.cs:692-702`、`747-855`（编辑操作和光标） | `PinCCDesk/EditorOps.swift`；`PinCCApp/EditorTextView`（NSTextView） | `EditorOps.apply(文本, UTF-16 选区, 操作)` → 结果；向量用 **WPF 形状的文本**生成：回车插入的是 CRLF（CardWindow.xaml:20 AcceptsReturn，WPF 插入 Environment.NewLine），续列表插入的是 LF；界面保存过的正文是 LF（CardWindow.xaml.cs:711、735），但经 CLI（Cli.cs:222-235）或剪贴板、拖入（Desk.cs:368-372、390-396）写入的正文保留 CRLF，进入编辑时原样载入（CardWindow.xaml.cs:683）；CurrentLine 只按 `\n` 分割（CardWindow.xaml.cs:762-769），所以以 CRLF 结尾的行保留 `\r`，续列表在那里停止（775 行）。向量的初始文本同时覆盖 LF 正文和整篇 CRLF 正文。Mac 编辑器载入时不规范行尾；回车插入什么由 §12 第 14 条决定：选“在 C# 里修”时 W2m 让 C# 按 LF 计算当前行，Mac 回车插入 LF；选“照搬”时 Mac 回车也插入 CRLF，缓冲区按 WPF 的形状保存。P7 开始前必须有答案 | EditorOps 向量（续列表、缩进和取消缩进、包裹、前缀切换、`[]` 快捷输入）；提交规则向量；P0 输入法闸门 | 列表模式里显式的 .NET `\s`；序数 StartsWith |
| `HubWindow.cs`、`Flyout.cs`、`Toast.cs`、`ManageWindow.cs`（模型）、`Hotkeys.cs` | `HubModel`、`PickerModels`、`LibraryModel`、`SettingsSchema`、`KeyBindings`；`PinCCApp/HubPanel`、选择器（默认子面板）、Toast、设置和卡片库（SwiftUI）、`MacHotkeys`（Carbon）+ 录入 | 模型从 Rules 函数移植；Mac 用 `mac.hk.*` 和自己的写法（⌃⌥⇧⌘）、Mac 已知冲突表，三种状态相同（`''` 未绑定，无此行为默认）；角标问题文字保留“被”“与”前缀的约定 | 模型向量（速拨顺序、角标优先级、提醒选项和提示、卡片库匹配）；命令、绑定、图标的表完整性测试 | Mac 从不写 `hk.*`；卡片库搜索按 UTF-16 的 OrdinalIgnoreCase；历史预览 40 单元 |
| `ClipData.cs` + Desk 的 CreateFrom/Ingest/粘贴 + Native/App/Program（Win32 外壳） | `PinCCDesk/ClipIngest.swift`（可移植的规划）+ `PinCCApp/MacPasteboard`、`DragIn`、`DragOut`、`main.swift`、`AppDelegate`、`MacScreens`、`FullscreenMonitor` | ClipPayload{files, text, sourceUrl, png} → ClipIngest.plan，照搬 Desk 的规则（12 个文件上限、用 NetUri Host 得到来源后缀、一个操作组）；Mac 的扩展名集合增加 heic、heif、avif，一律转码；单实例用 flock + 分布式通知（ShowAll 语义） | ClipIngest 向量；P0 剪贴板和 accessBehavior 检查；Mac verify 场景 | 来源网址尽力而为（已记录）；剪贴板隐私提示；启动参数过滤 |
| （新，W5）Cli 的 export/import 和数据搬迁规则 | `PinCCCore/Bundle.swift` + PinCCCli 命令 | 先在 C# 实现，再移植；包是带 bundle_meta 和 bundle_files 的 SQLite 文件；导入：GUI 运行时拒绝、先备份、文件并集写入且修改时间设为现在、一个事务、保留本机作用域和敏感设置 | CLI 录制：Windows 导出 → Swift 导入以及反向（WSL 交替读写和两台 Mac 的样例）；转储除本机设置外与源相同 | WAL 撕裂的复制；更新的表结构；Purge 清掉导入的文件；覆盖本机设置；保留 id 和撤销历史；泄漏密钥 |

---

## 8. Mac 端实现要点

本节沿用上一版（`git show 7e24435:docs/MACOS.md`）§8 已核对的 AppKit 做法，改写为 Swift，并加入 macOS 27 的变化（汇总见 §8.17）。.NET 专有的部分换成 Swift 的对应物：`[UnmanagedCallersOnly]` → `@convention(c)` 的 Carbon 回调；SynchronizationContext → `@MainActor`；workload / AOT → 无；CLI 垫片脚本 → 原生的 `Helpers/pincc`。

### 8.1 程序外壳与生命周期

- **Info.plist**：

| 键 | 值与说明 |
|---|---|
| `CFBundleIdentifier` | 自用正式版 `com.<你>.pincc`，开发版 `com.<你>.pincc.dev` |
| `LSUIElement` | YES：没有 Dock 图标，也不出现在 ⌘Tab 里 |
| `CFBundleDevelopmentRegion` | zh-Hans |
| `LSMinimumSystemVersion` | 14.0（Intel Mac 跑不了 Sonoma 时按 §9.1 的表取 13.0 或 12.0，§9.2） |
| `NSAppTransportSecurity` / `NSAllowsArbitraryLoads` | YES：抓取任意 http 网址，与 Windows 相同（隐私守卫仍由 UrlGuard 负责） |
| `NSDesktopFolderUsageDescription`、`NSDocumentsFolderUsageDescription`、`NSDownloadsFolderUsageDescription` | 访问这三个文件夹时向你说明用途 |

- **main.swift**：
  1. 去掉 `-NS*`、`-Apple*`、`-psn_*` 参数后，如果按 Program.cs:17 的规则是 CLI 调用，就用 UTF-8（无 BOM）的 stdout 运行 PinCCCli 并 `exit()`，在 NSApplication 启动（连接 WindowServer）之前返回，所以不会闪出 Dock 图标。
  2. 如果设置了屏幕外渲染模式（`PINCC_UI_SNAPSHOT` 或 Mac 的 verify 场景变量），直接进入屏幕外渲染：不加锁，不发送也不监听显示通知，不显示任何窗口，写完结果就退出。
  3. 否则对 `Home.resolve()/.instance` 加 flock（这个锁同时供 `import` 判断 GUI 是否在运行，§4.4.1）。已有实例在运行时，发送显示通知后退出：
     - 通知名带上 bundle id（例如 `com.<你>.pincc.Show`），开发版和正式版互不唤醒；两者的默认数据目录也不同（§8.16），所以各自持有自己的锁；
     - 持有者拿到锁后把自己的 bundle id 写进 `.instance`；拿不到锁、而文件里是另一个 bundle id 时（例如两个版本都被 `PINCC_HOME` 指到同一个目录），在 stderr 和一次提示条里说明，而不是静默退出；
     - 用 `DistributedNotificationCenter` 的 `postNotificationName(_:object:userInfo:deliverImmediately: true)`，或者观察方注册时用 `suspensionBehavior: .deliverImmediately`。AppKit 在程序不在前台时会暂停分布式通知的投递，而 PinCC 几乎总在后台。
  4. 以 `.accessory` 激活策略启动。启动前不调用任何 TIS（输入法）接口。
- **AppDelegate**：
  - `applicationDidFinishLaunching`：DeskCore.start；
  - `applicationShouldHandleReopen`（从访达、启动台或 Spotlight 再次打开）：ShowAll；如果菜单栏图标已被关掉或被系统隐藏，同时打开设置窗口（里面有“退出 PinCC”）；
  - `applicationShouldTerminate` 和 `willPowerOff`：Flush；
  - `applicationWillTerminate`：Shutdown、对 LLM 子进程组 killpg，然后关闭 Store；
  - 不在 Info.plist 里声明 `NSSupportsSuddenTermination`，注销和关机时一定会走 applicationShouldTerminate → Flush；
  - 未捕获的错误写入 `~/Library/Logs/PinCC/error.log`；
  - 设置窗口或卡片库打开期间，临时切换为 `.regular` 激活策略。
- **隐藏的主菜单**：agent 程序不显示菜单栏，但没有主菜单的话，文本框里的 ⌘C、⌘V、⌘Z 都不起作用。
  - App 菜单：关于 PinCC、设置… ⌘,、安装命令行工具…、退出 PinCC ⌘Q；
  - 编辑菜单：撤销 ⌘Z、重做 ⇧⌘Z、剪切、拷贝、粘贴、全选。
- **菜单栏图标**（NSStatusItem，SF Symbol 模板图，默认显示，可关掉）：
  - macOS 27 上状态项的点击改由系统内部处理：action 仍会发出，但此时 `NSApp.currentEvent` 可能是 AppKitDefined(13) 或 MouseMoved(5)，不是 LeftMouseDown（Wails #6147 评论里的实测）；常驻在 item 上的菜单会吞掉左键（tray-icon #355、ai-usagebar #237）；
  - 做法：target/action 设在 `statusItem.button` 上（`NSStatusItem.target/action` 本来就已弃用）；不按 `NSApp.currentEvent` 的类型或 `clickCount` 区分左右键，左右键弹同一个菜单；菜单只在弹出前临时挂到 item 上、关闭后去掉（tray-icon 0.25.1 的做法）；
  - 读 `clickCount` 前先检查事件类型：对非鼠标事件读它会抛异常（GoldenDict #3080 的崩溃栈）。macOS 27 SDK 新增的 `NSStatusItemExpandedInterfaceSession` 在 P0 A6 一起比较；
  - macOS 26 起你可以在“系统设置 › 菜单栏”隐藏第三方图标，所以设置和退出同时保留在 + 按钮的右键菜单里，再次打开程序时如果图标不可见也会打开设置窗口。

### 8.2 卡片面板（CardPanel）

- **创建一次，样式不再修改**：NSPanel 子类，`init` 时就传入 `[.borderless, .nonactivatingPanel]`，之后绝不改动，也不通过重写 `styleMask` 的 getter 伪造。macOS 27 上已确认：重写 `styleMask` getter 伪造这个标志的 Electron 面板，点击会激活程序（Electron #53889）。Tauri 面板有类似报告（tauri-nspanel #123），但维护者在 macOS 27 上实测创建后 `add_style_mask` 的面板“成为 key、别的程序仍在前台”，把问题归到整组替换样式；报告者说改了仍不行，结论未定。AppKit 会拒绝某些运行中的样式替换（tauri-nspanel 67212d83）。创建时就设定的面板目前没有受影响的报告，但 PinCC 的具体情形（能成为 key 的卡片、别的程序在前台时点击并打字）仍要 P0 实测。
- **焦点相关的三个属性**：
  - `canBecomeKey = true`：无边框窗口默认不能成为 key，不改的话编辑器收不到键盘输入；
  - `hidesOnDeactivate = false`：NSPanel 默认是 true，不改的话切到别的程序时所有卡片都会消失；
  - `becomesKeyOnlyIfNeeded = false`（保持默认）：单击卡片任何位置，卡片就成为 key，但面板不激活程序，前一个程序仍在前台。这对应 Windows 上卡片被点击后激活。设成 true 的话只有点到文本视图才成为 key，Return、⌘⌫、⌘C、⌘V、⌘Z、⌘N、⌘F、⌘` 这些卡片快捷键就收不到，“选中”的强调色描边也不会出现；
  - 但 Apple 文档还说：不激活面板只在被点中的视图 `needsPanelToBecomeKey` 返回 true 时才成为 key，而 NSView 默认返回 false。所以卡片的内容视图和各个自绘子视图都覆写 `needsPanelToBecomeKey` 返回 true，或者在 CardPanel 的 `sendEvent(_:)` 里遇到 leftMouseDown、卡片还不是 key 时显式调用 `makeKey()`（不激活程序）。P0 A1 两种做法都测，点正文、角标、图片、空白处都必须成为 key；
  - 悬停不改变 key；开始编辑时同样调用 `makeKey()`；
  - 卡片带 `.ignoresCycle`，系统的 ⌘` 窗口轮换不包含卡片，⌘` / ⇧⌘` 由 KeyBindings 在面板的 keyDown 里处理，被切换到的卡片调用 makeKey。
- **外观**：`isOpaque = false`，`backgroundColor = .clear`；`hasShadow = true`，阴影在点击区域之外，每次形状变化调用 `invalidateShadow()`；`animationBehavior = .none`；`isExcludedFromWindowsMenu = true`。
- **层级与桌面空间**：
  - 生效置顶（包括到期）的卡片用 `.floating`，其余用 `.normal`；**正在编辑的卡片不高于 `.floating`**，因为输入法候选窗按 `NSTextInputClient.windowLevel` 定位，只在它高于 `.floating` 时才抬高；或者在编辑器上实现 `windowLevel`；
  - `collectionBehavior = [.canJoinAllSpaces, .ignoresCycle]`，到期时加 `.stationary`，“显示桌面”和调度中心不会把它推开；
  - 永远不加 `.fullScreenAuxiliary`，全屏应用所在的空间里不会出现卡片。
- **移动窗口**：置前用 `orderFrontRegardless()`（不激活程序）；退后用 `order(.below, relativeTo:)`；放置用 `setFrame(_:display: false)`，同一次布局的所有窗口放在同一个 `CATransaction.setDisableActions(true)` 里。
- **内容视图**：用 layer 绘制，8 pt 连续曲率圆角（`cornerCurve = .continuous`），1 px 描边。描边按 CardScene 的边框样式：到期 2 px 红色，选中为强调色，普通为 ink 0.12。各部分按 CardScene 生成。
- **悬停和第一次点击**：
  - NSTrackingArea 选项 `[.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect]`。没有 `.activeAlways` 的话 PinCC 不在前台时悬停按钮不会出现，而不在前台是常态。`.activeAlways` 不支持 `.cursorUpdate`，所以光标在 `mouseMoved` 里设置；
  - `acceptsFirstMouse(for:) = true`，否则点 ○、📌 的第一下只会激活程序；
  - 事件监听返回的 token 要强引用保存（KeyboardShortcuts 3.1.0 发现 macOS 26/27 上弱引用的 token 会被释放）。
- **移动与缩放**：手写鼠标跟踪循环。macOS 27 上 `isMovableByWindowBackground` 不再能拖动 `mouseDownCanMoveWindow` 为 false 的无边框面板，所以卡片拖动必须显式实现：在跟踪循环里手动改 frame。不用 `performDrag(with:)`：它把拖动交给 WindowServer，方法立即返回，mouseUp 可能收不到（Apple 文档），`OnUserMoved` 就不会在松开时调用，3 pt 阈值和 Rules 的规则也管不到。
  - 移动阈值 3 pt；
  - 缩放边带由 Rules 的 `HitTest` 决定（内侧 6 pt，角上 12 pt）；光标在 mouseMoved 里按边带调用 `NSCursor.set()`：边上用 resizeLeftRight / resizeUpDown，角上 macOS 15 及以上用 `NSCursor.frameResize(position:directions:)`，macOS 14 及更早用程序自带的光标图片（`#available(macOS 15, *)` 判断）；后台时系统可能不允许改光标，P0 实测，不行就接受“后台时不显示缩放光标，但照样能拖动缩放”；
  - 图片卡片用 Rules 的 `KeepAspect` 锁定比例；
  - 松开鼠标时只调用一次 `OnUserMoved`；双击下边带恢复自适应高度；
  - ⌘-拖动保留给系统的“移动后台窗口”。
- **动画和效果**：溢出淡出用叠在内容上面的普通 CAGradientLayer，不用 mask（屏幕外截图用的 `render(in:)` 不画 mask，附录 B）；CABasicAnimation 做到期红框闪三下，“减少动态效果”打开时跳过；角标单独一个图层。
- **外观模式**：浅色卡片强制 `.aqua`，墨炭强制 `.darkAqua`；“降低透明度”打开时透明度固定为 1。

### 8.3 编辑器与中文输入法

- **NSTextView 设置**（创建时设定，并有单元测试断言这些标志）：
  - `isRichText = false`、`importsGraphics = false`、`allowsUndo = true`；
  - 关闭：`isAutomaticQuoteSubstitutionEnabled`、`isAutomaticDashSubstitutionEnabled`、`isAutomaticTextReplacementEnabled`、`isAutomaticSpellingCorrectionEnabled`、`isContinuousSpellCheckingEnabled`、`isAutomaticTextCompletionEnabled`、`smartInsertDeleteEnabled`、`isAutomaticLinkDetectionEnabled`、`isAutomaticDataDetectionEnabled`，以及自动首字母大写和双空格句号（用户级的文本替换设置由上面几项覆盖）。
  - 原因：WPF TextBox 不做这些替换（CardWindow.xaml:19-20，SpellCheck 关闭）。不关的话同样的按键在 Mac 上会存出不同的正文：`"C:\x\a.pdf"` 变成弯引号后 `Trim('"','<','>')`（Card.cs:115）去不掉，卡片就不是文件卡片；`---` 变成 `—-` 后 RuleRx 不再匹配；`--if-rev` 变成破折号。Hash(Body) 和 Kind 都会与 Windows 不同。
  - 一个 verify 场景把 `"a" -- --- ...` 经 `insertText` 输入，存储的正文必须与 WPF 的结果相同。
- **行尾**：U+2028、U+2029 在输入时换成 `\n`。回车插入 LF 还是 CRLF 由 §12 第 14 条决定（§7 EditorOps 一行、§1.4）；载入的正文不规范行尾。提交时按 C# 规则做 CRLF→LF 加 TrimEnd，自动保存不裁剪。
- **按键处理**：Return、Tab、⇧Tab、Esc 在 `textView(_:doCommandBy:)` 里处理（`insertNewline:`、`insertTab:`、`insertBacktab:`、`cancelOperation:`）。这个回调只在输入法提交之后触发，所以拼音选词时按回车不会误续列表。`[] ` 快捷改写用 `hasMarkedText()` 保护。
- **可撤销**：EditorOps 的修改依次调用 `shouldChangeText`、`replaceCharacters`、`didChangeText`，⌘Z 能撤销。
- **粘贴**：重写 `paste(_:)` 处理图片和文件。插入点和选区颜色取卡片的强调色。
- **不绑定 ⌃+字母**：保留 NSTextView 自带的 Emacs 式按键（⌃A、⌃E、⌃K 等）。
- **焦点方案 A（首选）**：卡片是能成为 key 的不激活面板，打字时前一个程序仍在前台。按 Esc 时要把键盘还给原程序，同时卡片留在屏幕上。AppKit 没有“只放弃 key”的公开接口（`resignKey()` 是回调，不能直接调用；Spotlight 类面板靠关闭面板交还焦点）。具体做法 P0 验证，例如在同一个 CATransaction 里先 `orderOut` 再 `orderFrontRegardless`，确认原程序重新收到键盘、卡片不闪。
- **焦点方案 B（A 不通过时）**：编辑时激活 PinCC；按 Esc 时调用 `NSApp.yieldActivation(to:)` 再激活之前的程序（macOS 14 的协作式激活）。部署目标退到 13 或 12 时（§9.1），这一步放在 `#available(macOS 14, *)` 里，更早的系统直接对之前的程序调用 `NSRunningApplication.activate(options:)`。
- 两种方案都在 `IFocus` 接口后面，DeskCore 不用改。

### 8.4 Markdown 渲染（MdAttributed）

- 由 MdRender 生成 NSAttributedString。字体：系统字体（SF Pro，中文自动回退到苹方）和等宽系统字体（SF Mono），字号 13/14/16/18 与 Windows 相同；段落行高 `min/maxLineHeight = round(1.5×字号)`。
- 列表用 headIndent 加制表位；链接用 `.link` 属性；任务方框用 NSTextAttachment，按字符位置判断点击；引用条和代码块背景按 MdRender 的样式标记绘制。
- 双击时把光标放到对应位置：每段带 `pincc.line` 属性记录源码行号，再结合 `characterIndexForInsertion(at:)`。

### 8.5 + 按钮、提示条、选择器、菜单

- **HubPanel（+ 按钮）**：
  - 每个屏幕一个不激活面板，`canBecomeKey = false`，层级 `.floating`，`[.canJoinAllSpaces, .stationary, .ignoresCycle]`；
  - 手写拖动，松开时按 Rules 的 `SnapCorner` 吸附到最近的屏幕角；
  - 悬停速拨：NSTrackingArea（`.activeAlways`），进入 300 ms、离开 350 ms 延时，弹出“平铺显示 / 全部折叠·展开 / 最小化全部·恢复显示”。项目、图标和提示来自 HubModel，贴底部角时顺序反转；面板尺寸按 `HubRect`，包含速拨区域；`acceptsFirstMouse = true`；
  - `registerForDraggedTypes` 接收拖入；
  - macOS 26 及以上用 NSGlassEffectView，更早用 NSVisualEffectView。Liquid Glass 只用在控件层，卡片属于内容层，不用。
- **ToastPanel（提示条）**：不激活，`canBecomeKey = false`，材质背景，位置取 `Layout.Spot`；显示 4 秒，悬停时暂停，点击关闭，与 Windows 相同。提示留在程序内，不用系统通知中心，保证两端布局一致。
- **提醒和颜色选择器**：默认用**自己的不激活子面板**（`addChildWindow`，样式同卡片，可以成为 key）。NSPopover 的窗口不是 nonactivatingPanel；macOS 27 上有锚在状态项上的 NSPopover 显示时拿走系统键盘焦点的报告（Doll #75），但 PinCC 的选择器锚在卡片上，不是同一种情形。所以 P0 的 A3 比较两者：只有 NSPopover 在 PinCC 不在前台时能打字（含拼音）、又不激活程序，才改用 NSPopover；否则用子面板。卡片失去 key 时，如果新的 key 窗口是自己的子面板，就不退出编辑（对应 Windows 的 IsOwnedBy）。“设定”是回车默认按钮。
- **菜单**：MenuSpec 生成 NSMenu，通过 `menu(for:)` 提供，⌃点按和双指点按也能打开；SF Symbols 图标，快捷键显示来自 KeyBindings.Mac；单选项 `state = .on`；透明度改成“透明度 ▸”子菜单（100/90/80/70/50，当前值打勾）。

### 8.6 设置与卡片库

- 两个窗口都是程序自己的 NSWindow，内容用 NSHostingController 承载的 SwiftUI Form（不用 SwiftUI 的 Settings scene）。
- **设置（⌘,）**：通用、快捷键、AI 摘要、外观四页，全部由 SettingsSchema 生成；改动立即生效；⌘W 关闭；记住上次打开的页。
  - 只显示作用域为共享和 Mac 的项；不显示 WSL 和发行版，改为显示捕获到的登录 shell（例如“/bin/zsh 登录环境”）；
  - 新增“命令行”一行，用来安装 `pincc`；
  - 所有写入经带类型的设置接口（§4.2.4）。
- **卡片库（⌘F）**：NSSearchField 搜索，带计数的分段过滤，列表（历史可以展开）。Esc 清空搜索框（Mac 上 Esc 不关闭窗口）。
- 两个窗口打开期间都是 `.floating` 层级，居中显示在鼠标所在屏幕的 visibleFrame 上。

### 8.7 全局热键与录入

- **注册**：`import Carbon`，`RegisterEventHotKey` 带 `kEventHotKeyExclusive`。组合已被别的程序独占时返回 −9878，界面显示“被占用”，相当于 Windows 的 1409。`InstallEventHandler(GetApplicationEventTarget(), …)` 的回调是 `@convention(c)` 闭包，通过 userData 找回对象。不需要任何系统权限。
- **存储**：`mac.hk.*` 设置键，写法 ⌃⌥⇧⌘ 顺序。与 Windows 相同的三种状态：`''` 表示未绑定，无此行表示用默认值。
- **冲突检查**：`CopySymbolicHotKeys`（系统快捷键）、自己的菜单、Mac 已知冲突表（⌘Space、⌥⌘Space、⌃Space、⇧⌘3/4/5、⌃⌘A、⌥Space 等）。
- **规则**：默认和录入的热键都必须含 ⌘ 或 ⌃（macOS 15.0–15.1 拒绝只用 ⌥ 或 ⌥⇧ 的组合）。
- **录入框**：不实现 NSTextInputClient 的第一响应者视图（不经过输入法），加本地事件监听（token 强引用）；录入期间先注销自己的全部热键；不用 CGEventTap（会触发“输入监控”权限）。能报告的冲突：系统快捷键和别的程序用 Carbon 注册的组合；用事件钩子实现的快捷键检测不到，只能靠已知表提示。
- **默认值**：“新建卡片”为 ⌃⌥⌘N（待确认，§12 第 10 条），其余四个动作默认不绑定，与 Windows 相同。

### 8.8 剪贴板、拖入、拖出

- **读取顺序**：
  1. 文件 URL；
  2. `public.url` 加 `public.url-name`（从 Safari 拖出的链接带标题）；
  3. `org.chromium.source-url`（Chrome/Edge 复制时带的来源网址）；
  4. `com.apple.webarchive` 里 WebMainResource 的 WebResourceURL（Safari 复制文字时的来源网址）；Firefox 用哪种类型未查证，P10 实测；
  5. png / tiff / heic，统一转码；
  6. 纯文本。
  结果统一成 ClipPayload，交给 ClipIngest.plan。来源网址是尽力而为，已记录为差异。
- **剪贴板隐私**：macOS 15.4 起有 `NSPasteboard.accessBehavior`，默认“程序读取时询问”；“既是用户发起、又与粘贴相关”的读取不提示。全局热键在后台“用剪贴板新建”很可能不算粘贴相关，会弹提示。做法：读取前检查 `accessBehavior`，先用 detect 接口判断内容类型；首次运行的提示条告诉你可以在系统设置里选“始终允许”；优先引导用 + 按钮后 ⌘V 这类粘贴路径。哪一版 macOS 开始实际弹提示未查证，P0 实测。
- **图片转码**：HEIC/HEIF/AVIF 用 CGImageSource / CGImageDestination 转成 PNG（或 JPEG），**把 EXIF 方向应用到像素**，不保留方向标记，这样 Windows 显示的方向与 Mac 相同（WPF 忽略方向标记，Desk.cs:334-336、1273-1283）。
- **写入**：先 `clearContents()`；纯图片卡片写 PNG 数据加文件 URL；其余卡片写纯文本（正文 + 摘要），与 Windows 的 Desk.Copy 相同；编辑状态下的 ⌘C 交给 NSTextView。
- **截图流程**：⌃⇧⌘4 截到剪贴板，点 + 新建卡片，按 ⌘V。
- **拖入**：+ 按钮和卡片注册 `.fileURL`、`.URL`、`.string`、`.png`、`.tiff`、`.html`、`org.chromium.source-url`，与粘贴共用 ClipPayload。
- **拖出图片**：⌥-拖动（Mac 上 ⌃点按等于右键，⌥ 在拖动时本来就表示拷贝）。拖动会话携带文件 URL，操作类型 `.copy`。

### 8.9 打开文件、在访达中显示、提示音、登录项

- **打开**：`NSWorkspace.shared.open(url)`。**在访达中显示**：`activateFileViewerSelecting`。异机路径不打开，提示“文件在另一台电脑上”。
- **只定位不运行**：`.app .command .sh .tool .terminal .workflow .pkg .mpkg .scpt`，以及没有扩展名的可执行文件。这张表放在 `contracts/tables/mac-*.json`，与 Windows 的表（Desk.cs:765）一起做完整性检查。
- **提示音**：`AudioServicesCreateSystemSoundID` 载入 `/System/Library/Sounds/Glass.aiff`，用 `AudioServicesPlayAlertSound` 播放，走提醒音量；失败用 `NSSound.beep()`。
- **登录项**：`SMAppService.mainApp.register()` / `unregister()`（macOS 13+）。开关显示实时的 `.status`；`requiresApproval` 时提供跳到 `openSystemSettingsLoginItems()` 的按钮；由你选择开启，首次运行的提示条上给一个按钮（记在 `mac.autostart.asked`）。签名身份必须稳定（§9.6），否则每次重新构建都要重新批准。只有 Intel Mac 停在 macOS 12 时（§9.1），SMAppService 放在 `#available(macOS 13, *)` 里，12 上退回写 `~/Library/LaunchAgents` 的 plist（约 1 天，从 P13 预留支出）。
- **`--startup` 的对应**：Windows 上 `--startup` 只用来在开机启动且没有卡片时不弹欢迎提示（Desk.cs:129）。SMAppService 启动时不带参数，Mac 用启动 Apple 事件里的 `keyAELaunchedAsLogInItem` 或开机时长判断，P0 验证哪种可靠；判断不了时欢迎提示只在首次运行时显示。

### 8.10 定时、提醒与省电

- **1 秒评估网格**：DeskCore 的 Tick 与 C# 一样按 1 秒网格评估到期、提醒只响一次、同一批一声。节拍放在 `.common` 运行循环模式，误差 100 ms，菜单打开和拖动时也会触发。
- **到点唤醒**：App Nap 会降低整个进程的定时器频率，普通的一次性 DispatchSourceTimer 同样会被合并和推迟。卡片全在别的桌面空间（全屏应用在前）或全部最小化时，PinCC 最容易被小睡，这正是提醒最可能迟到的时候。DeskCore 提供 `nextWake()`（最近的提醒时间）；Mac 用 `DispatchSource.makeTimerSource(flags: .strict)`、leeway 很小的一次性定时器对准它，并在到点前约 60 秒开始持有一个 `.userInitiatedAllowingIdleSystemSleep` 活动（`ProcessInfo.beginActivity`），Tick 之后立即结束。评估规则不变，只是不被推迟。唤醒、系统时间或时区变化、日期变化、数据变化时重新设定。P9 在全屏应用前台时实测提醒延迟。
- 除到点前的短暂活动外，只在 LLM 运行期间持有 `beginActivity`。
- **系统事件**：`didChangeScreenParametersNotification`、`NSSystemClockDidChange`、`NSSystemTimeZoneDidChange`（同时重置时区缓存）、`NSCalendarDayChanged`；`didWakeNotification` **必须**在 `NSWorkspace.shared.notificationCenter` 上监听。
- 数据变化：每次节拍轮询 `PRAGMA data_version`。可选（P12）：用 DispatchSource 监视 `pincc.db-wal`，让 CLI 的修改立即显示。
- 目标：30 张卡片时空闲 CPU 约为 0%。

### 8.11 全屏与桌面空间

- Mac 的全屏应用在单独的桌面空间里。卡片不带 `.fullScreenAuxiliary`，自然不会出现在那里，Windows 上“退到全屏窗口后面”在 Mac 上不用额外实现。
- 全屏检测只用于隐藏 + 按钮和推迟提醒声：由 `activeSpaceDidChange`、`didActivateApplication` 触发；检查卡片的 `isOnActiveSpace`，并用 `CGWindowListCopyWindowInfo` 看其他进程的第 0 层窗口是否铺满整个屏幕（只读边界，不读标题，不需要“屏幕录制”权限）。判断错了只是提醒声提前响。
- “显示器具有单独的空间”关闭时，只有主屏有菜单栏，全屏应用会占用所有显示器（P0 实测）。

### 8.12 图片

- 用 `CGImageSourceCopyPropertiesAtIndex` 读像素尺寸，不解码，不按 EXIF 方向或 DPI 换算（与 WPF 的 `PixelWidth/PixelHeight` 相同，Desk.cs:334-336），所以卡片的宽高比两端相同。
- **显示时不应用 EXIF 方向**，与 WPF 一致：直接用 `CGImageSourceCreateImageAtIndex`（或缩略图时不带 `kCGImageSourceCreateThumbnailWithTransform`）。这样同一张带方向标记的 JPEG 在两端显示相同（都是未旋转的像素）。是否改成两端都应用方向，是另一项需要 Windows 改动的决定（§12 第 12 条）。
- 按档位解码（最大边为显示宽度 × backingScaleFactor），结果放在有成本上限的 NSCache；`viewDidChangeBackingProperties` 时重新解码。
- 缩放：触控板捏合（每次捏合一个撤销组，增量累计成与滚轮相同的档位）或滚轮；1:1：⌘0、双指轻点两下，或三键鼠标中键；双击用“预览”打开。
- “1:1 像素”的换算写成 `像素 ÷ backingScaleFactor` 个 point；共享代码里用 `Work × PixelScale ÷ Scale` 的形式，两端都对（ZoomActual、NaturalSize、解码宽度）。

### 8.13 AI 工具（ToolHost.Mac）

- **必须捕获登录 shell 的环境**：从访达、Dock 或登录项启动的程序，PATH 只有 `/usr/bin:/bin:/usr/sbin:/sbin`。做法：从 `getpwuid` 取你的 shell（默认 /bin/zsh）；执行 `<shell> -l -i -c 'printf 标记; /usr/bin/env'`，关闭 stdin，期限 15 秒；用与 WSL 相同的标记解析器和白名单（PATH、NVM_BIN、代理等）提取，保存为 `mac.llm.env`。启动时在后台做一次，点“检测”时再做一次。
- **启动顺序**：首次检测、补做摘要和自动摘要都要等环境捕获完成，或先用上次保存的 `mac.llm.env`。捕获失败时退回“进程 PATH + `/etc/paths`、`/etc/paths.d/*` + 下表目录”，这次不写 `mac.llm.detected`。
- **查找工具**：按 `:` 分割 PATH 并检查可执行权限，另外探测：

| 位置 | 说明 |
|---|---|
| `/opt/homebrew/bin` | Apple 芯片上的 Homebrew |
| `/usr/local/bin` | Intel 上的 Homebrew；Ollama.app 也装在这里 |
| `~/.local/bin` | Claude Code 原生安装位置 |
| `~/.claude/local`、`~/.npm-global/bin`、`~/.volta/bin`、`~/.bun/bin`、`$NVM_BIN` | 其他常见安装位置 |
| Ollama.app 内置路径 | Ollama 自带的命令行 |

- **启动子进程**（Proc.swift）：npm 装的工具是 `#!/usr/bin/env node` 脚本，直接执行；自定义命令用 `[shell, -c, 命令]`；缺 LANG 时补 `LANG=en_US.UTF-8`；保留 NO_COLOR、TERM=dumb、PYTHONUTF8、PYTHONIOENCODING；去掉 WSL_UTF8 和 WSLENV；超时 150 秒；子进程在自己的进程组里，超时和退出时 killpg。
- **隐私权限（TCC）**：读 `~/Documents`、`~/Desktop`、`~/Downloads` 会弹一次授权，子进程的访问也算在 PinCC 头上；拖入文件属于隐含同意；RefreshFiles 和 CLI 添加的文件卡片会触发授权。拒绝授权时卡片显示“需要授权”，不显示“文件缺失”，摘要也不标为过时（运行时状态，不写 meta）。
- **不开 App Sandbox**：沙盒会继承给子进程，claude、codex 读不了自己的配置。所以不上 Mac App Store。

### 8.14 网页渲染（WebKitRenderer）

- 首选已安装的 Chrome 或 Edge（按 bundle id 查找），带 `--use-mock-keychain` 执行 `--dump-dom`，与 Windows 的 Edge 路径最接近。
- 否则用系统 WebKit 在进程内渲染。WKWebView 必须挂在窗口上，但 WebKit 判断页面可见还要求 `window.isVisible` 且窗口没被遮挡（WebKit PageClientImplMac.mm:206-226 的 `isViewVisible`），所以从不显示或完全在屏幕之外的窗口都算“隐藏页面”：`document.visibilityState` 为 hidden，定时器被节流，requestAnimationFrame 停止。做法：无边框 NSWindow，`WKWebsiteDataStore.nonPersistent()`；自定义 User-Agent；`mediaTypesRequiringUserActionForPlayback = .all`；等页面稳定的逻辑按节流后的节奏设计：didFinish 之后用 MutationObserver 等页面稳定（最长 8 秒不变），稳定后取 `document.documentElement.outerHTML` 交给同一个 FromHtml；同时只渲染一个页面；只在主线程使用。P0 A6 记录 visibilityState，并比较“排到屏幕上、alphaValue 接近 0、ignoresMouseEvents”的窗口能否算作可见、又不被你看到或点到。所以 Chrome/Edge 仍是首选。
- 两种渲染得到的 DOM 可能不同，已登记为差异（§1.4）。

### 8.15 命令行

- `Helpers/pincc` 是独立的 Mach-O，只链接 PinCCCli；`MacOS/PinCC` 带 CLI 参数时也在进程内走 CLI。都始终输出 UTF-8，退出码直接交给 shell。
- “安装命令行工具…”：把 `Helpers/pincc` 符号链接到 `~/.local/bin`（并在 `~/.zprofile` 加一行 PATH），或 `/usr/local/bin`（需要管理员授权）。
- 帮助的平台片段：`pincc` 和 zsh 的示例，数据目录写 `~/Library/Application Support/PinCC`。
- P0 测量冷启动时间，并确认不闪 Dock、输出 UTF-8、退出码正确。

### 8.16 数据、日志与缓存目录

- 数据：`Home.resolve()`。正式版默认 `~/Library/Application Support/PinCC`；开发版默认 `~/Library/Application Support/PinCC.dev`；两者都可用 `PINCC_HOME` 覆盖。是否开发版是**构建时常量**：`tools/mac.sh app` 生成 `BuildChannel.swift`（或传 `-Xswiftc -DPINCC_DEV`），GUI 和 `Helpers/pincc` 来自同一次构建，`Home.resolve()` 只读这个常量，运行时不读 bundle id（`Helpers/pincc` 是独立的 Mach-O，经 `~/.local/bin` 的符号链接调用时也看不到 .app 的 bundle id）。否则开发版和正式版共用一个数据库：正式版运行时开发版拿不到锁，发出的 `….dev.Show` 没人接收，直接退出；正式版没运行时开发版改的是真实数据，开发中的新格式一旦写进 `user_version`，正式版只能只读打开（Store.cs:61）。
- Mac 只在数据目录里建 `.instance`（§4.1），不建 edge/、llm/、error.log、shadow.log。
- 日志：`~/Library/Logs/PinCC/error.log`、`shadow.log`（若有）。
- 缓存：`~/Library/Caches/PinCC`（渲染器配置、LLM 工作目录），Time Machine 按惯例不备份。
- 程序不在沙盒里，GUI 和 CLI 共用同一个数据库。
- 启动时的位置检查见 §4.2.9。

### 8.17 macOS 27 带来的变化（汇总）

| 变化 | 影响 | 做法 | 依据 |
|---|---|---|---|
| 重写 getter 伪造 `.nonactivatingPanel` 的面板点击会激活程序；事后添加的情形有报告、未定 | 卡片 | 只在 init 里传入，从不修改 | Electron #53889、tauri-nspanel #123（附录 C） |
| 状态项点击时 `NSApp.currentEvent` 不再是鼠标事件；常驻菜单吞掉左键；对非鼠标事件读 `clickCount` 抛异常 | 菜单栏图标 | button 的 target/action，不看 currentEvent 的类型，临时挂菜单 | Wails #6147（评论实测）、tray-icon #355、ai-usagebar #237、GoldenDict #3080 |
| 锚在状态项上的 NSPopover 显示时拿走键盘焦点 | 选择器 | 默认用不激活的子面板，以 P0 A3 为准 | Doll #75（单一来源，情形与锚在卡片上不同） |
| `isMovableByWindowBackground` 不再移动无边框面板 | 卡片拖动 | 显式实现拖动 | meeting-reminder #39（单一来源） |
| 输入法候选窗按 `windowLevel` 定位 | 编辑中的卡片 | 编辑时不高于 `.floating`，或实现 `windowLevel` | Apple NSTextInputClient 文档、tauri-nspanel 8ed8f642 |
| 事件监听 token 弱引用会被释放 | 录入、悬停 | 强引用保存 | KeyboardShortcuts #241 |
| 第三方菜单栏图标可被隐藏（macOS 26 起） | 设置和退出的入口 | + 菜单里也有；重新打开时打开设置 | 上一版 §8.1 |
| 剪贴板读取可能提示 | 热键新建 | 检查 accessBehavior，引导“始终允许” | Apple NSPasteboard 文档 |
| Xcode 27 只在 Apple 芯片 + macOS 26.6+ 上运行 | 构建 | §9.1 | Xcode 27 发行说明 |
| Carbon 热键、SMAppService 在 macOS 27 上可用 | 热键、登录项 | 按计划使用 | PortKiller 4、OpenInTerminal、KlangLadder |

### 8.18 平台适配对照表

| 方面 | Windows（保持现状） | macOS |
|---|---|---|
| 实现语言 | C# / WPF（.NET 10） | Swift 6 / AppKit，不含 .NET |
| 卡片窗口 | 无边框分层 WPF 窗口，8 DIP 圆角，1 px 描边（到期 2 px 红），无阴影，WS_EX_TOOLWINDOW | 不激活的无边框 NSPanel，8 pt 连续曲率圆角，同样的描边规则，系统阴影（在点击区之外） |
| 字体、图标、颜色 | 微软雅黑 UI / Segoe UI、Cascadia Mono、Segoe Fluent 图标，界面外框只有浅色 | SF Pro + 苹方、SF Mono，字号和行高与 Windows 相同，SF Symbols；外框用系统语义色跟随深浅色；卡片配色两端相同，浅色卡片强制浅色外观（墨炭用深色） |
| 程序在哪里 | 不在任务栏和托盘，每个显示器一个 + | 没有 Dock 图标，每个屏幕一个 +；隐藏主菜单；菜单栏图标可选；再次打开时显示全部卡片 |
| 编辑器与输入法 | WPF TextBox | NSTextView，关闭全部自动替换；Return/Tab/Esc 经 doCommandBy 处理 |
| 鼠标与触控板 | Ctrl+滚轮调透明度，滚轮缩放，中键 1:1，Ctrl+拖出图片，DragMove | ⌘+滚动调透明度（累计触控板增量，忽略惯性），捏合或滚动缩放，⌘0 或双指轻点两下 1:1，⌥-拖出图片，显式的拖动跟踪 |
| 全局热键 | RegisterHotKey，默认 Ctrl+Alt+Shift+N，低级键盘钩子录入，存 `hk.*` | Carbon 热键，默认 ⌃⌥⌘N（待定），不需要权限，录入不用事件钩子，存 `mac.hk.*` |
| 快速记录后的焦点 | 记下之前的前台窗口，Esc 后切回 | 方案 A：不激活的 key 面板；方案 B：激活后再交还 |
| 设置与卡片库 | 一个管理窗口（卡片 / 设置两个标签），置顶，Esc 关闭 | “设置…”（⌘,）和“卡片库”（⌘F）两个窗口，⌘W 关闭 |
| 开机 / 登录启动 | HKCU Run，首次运行自动开启并提示 | SMAppService 登录项，由你选择开启 |
| 提醒与定时 | 每秒一次的 DispatcherTimer；PlaySound “Notification.Reminder” | 同样的 1 秒评估网格 + 到点唤醒；Glass 用 AudioServicesPlayAlertSound 播放 |
| 全屏 | 每秒轮询前台窗口，卡片退到全屏窗口后面，隐藏 + | 全屏应用在单独的空间里，卡片天然不出现；检测只用来隐藏 + 和推迟提醒声 |
| 屏幕与缩放 | 物理像素，PerMonitorV2，跨 DPI 两步放置，DeferWindowPos | point，翻转一次坐标，Work 取 visibleFrame，一个 CATransaction |
| CLI | `PinCC.exe`：管道和文件输出 UTF-8，交互式控制台用 UTF-16；HKCU PATH 安装 | `pincc`（Helpers 里的独立程序），始终 UTF-8，链接到 `~/.local/bin` 或 `/usr/local/bin` |
| AI 工具 | 注册表 PATH、.cmd 走 cmd.exe、可选 WSL、Job 对象，存 `llm.*` | 登录 shell 环境、探测 Homebrew、直接执行、进程组 + killpg，存 `mac.llm.*` |
| 抓取 | HttpClient；`msedge --headless=new --dump-dom` | URLSession（同样的规则）；Chrome/Edge 或屏幕外 WKWebView |
| 数据 | `%LOCALAPPDATA%\PinCC`，同一数据格式 | `~/Library/Application Support/PinCC`，同一数据格式；受保护文件夹首次访问时弹授权 |
| 分发 | 单文件 exe（依赖框架版和独立版） | 通用 .app，稳定的自签名身份；给别人用时需要 Developer ID 和公证 |

### 8.19 快捷键对照

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
| 设置 | 右键 + → 管理… →“设置”标签 | ⌘, |
| 在卡片间切换 | Ctrl+Tab / Ctrl+Shift+Tab | ⌘` / ⇧⌘` |
| 退出 | 右键 + → 退出 | ⌘Q |
| 置顶卡片的透明度 | Ctrl+滚轮；置顶图标上滚动；右键菜单的透明度一行 | ⌘+滚动；置顶图标上滚动；菜单“透明度 ▸”（⌃+滚动是系统的缩放辅助功能） |
| 图片缩放 / 1:1 | 滚轮 / 中键 | 捏合或滚轮 / ⌘0、双指轻点两下、三键鼠标中键 |
| 拖出图片 | Ctrl+拖动 | ⌥-拖动 |
| 加粗 / 斜体 | Ctrl+B / Ctrl+I | ⌘B / ⌘I |
| 删除线 / 行内代码 | Ctrl+T / Ctrl+E | ⇧⌘X / ⌥⌘C（P9 在两台 Mac 上核对是否被占用） |
| 任务清单项 / 普通列表项 | Ctrl+L / Ctrl+Shift+L | ⇧⌘L / ⇧⌘8 |
| 续列表 / 缩进 / 取消缩进 | Enter / Tab / Shift+Tab | Return / Tab / ⇧Tab（输入法提交后才生效） |
| 双击下边缘 | 恢复自适应高度 | 同样恢复自适应高度 |

Mac 上额外的规则（由表完整性测试检查）：卡片和编辑器都不绑定 ⌃+字母；⌘D、⌘H、⌘M 不用于删除或隐藏（⌘H 会连到期卡片一起隐藏，所以“最小化全部”在程序内部实现）；在卡片上按 ⌘W 不做任何事；所有全局热键都必须含 ⌘ 或 ⌃。

---

## 9. 构建、测试、签名、分发

### 9.1 每台机器的工具链

| 机器 | 装什么 | 做什么 | 不做什么 |
|---|---|---|---|
| Windows（经 WSL 调用 dotnet.exe） | .NET SDK 10.0.401（已有） | C# 测试；生成向量、轨迹、样例；交替读写和互开检查的 C# 一侧；OracleWpf | 不装 Swift |
| WSL（Ubuntu 24.04，x86_64） | swiftly（2026-09-22 起最新是 1.2.0；装当时的最新版即可，它的版本不影响所装的 Swift 版本）安装与 §9.2 相同的 Swift 版本，另装 6.4.0 作为第二个检查版本；`sudo apt install libsqlite3-dev zlib1g-dev`（目前缺这两个头文件包；运行库 libsqlite3 3.45.1、zlib 1.3 已有） | 内循环：DotNetCompat、PinCCCore、PinCCCli、PinCCDesk 及其测试；Proc 契约测试（posix_spawn 在 Linux 上可用）；交替读写 | 不编 AppKit 目标 |
| Apple 芯片 Mac | macOS 26.6+ 或 27，Xcode 27（Swift 6.4，macOS 27 SDK）；开启“远程登录”并保持图形界面登录；关闭自动睡眠、开启“唤醒以供网络访问”；固定 IP 写进 WSL 的 `~/.ssh/config`（WSL2 默认的 NAT 网络通常解析不了 `.local`） | 唯一构建发布版的机器；`swift test`；生成 `mac-*` 样例；屏幕外 verify；签名 | 不装 .NET |
| Intel Mac | 取决于型号和系统（下表） | 原生运行通用 .app；能装完整 Xcode 时也跑 `swift test`，不能时原生运行 `pincc-selftest`；远程访问的准备同上 | 不装 .NET |

两台 Mac 都不装 .NET SDK：
1. 基准必须是 Windows 上的 .NET。Uri、Path、时区数据、区域和 ICU 行为、winsqlite3、NTFS 在 macOS 版 .NET 上都不同，Mac 上跑出的“基准”是另一个错误的基准。
2. Mac 需要的东西都已提交：向量、轨迹、录制、`win-*` 样例。Mac 生成的样例经 git 回到 Windows 由 C# 验证。
3. 保持“Mac 上没有 .NET”的承诺，也免得在系统和 Xcode 都受限的 Intel Mac 上维护 SDK。

放弃的只有“在 APFS 上实时交替读写”，由 WSL 的交替读写加 APFS 上的样例往返覆盖。

**Intel Mac 的分支**（P0 第一天确定型号和系统版本）：

| Intel Mac 的系统 | 最新 Xcode / Swift | 做法 |
|---|---|---|
| macOS 26.2+（只有 4 个 Intel 型号能装 Tahoe） | Xcode 26.4.1–26.6 / Swift 6.3 | 装完整 Xcode（不只 CLT），跑全部 `swift test` |
| macOS 15.6–26.1 | Xcode 26.0–26.3 / Swift 6.2.x | 同上，tools-version 取 6.2 |
| macOS 14.5–15.5 | Xcode 16.x / Swift 6.0–6.1 | 同上，tools-version 取 6.0 或 6.1 |
| macOS 14.0–14.4 | Xcode 15.3–15.4 / Swift 5.10，没有 Swift 6 语言模式 | 先把 Intel Mac 升到 14.5 以上（能装 14.0 就能装最新的 14.x），再按上一行 |
| macOS 13.x | 13.5+：Xcode 15.2 / Swift 5.9；13.0–13.4：Xcode 14.3.1 / Swift 5.8.1；都没有 Swift 6 语言模式 | Intel Mac 只运行通用 .app，不编译。它自己的系统 SQLite、ICU 和 Foundation 由 `pincc-selftest` 检查：通用构建里另外产出这个不依赖 XCTest 的可执行文件，`tools/mac.sh test --intel` 把它复制到 Intel Mac 上原生运行（§3.2）。Rosetta 只用来提前发现 x86_64 的编译问题，它用的是 Apple 芯片 Mac 上 macOS 27 的系统库。部署目标 13；macOS 14 才有的接口（`yieldActivation(to:)` 等）一律加 `#available` |
| macOS 12.x | Xcode 14.2 / Swift 5.7 | 同上，部署目标降到 12（Xcode 27 能向下部署的下限）；SMAppService（13.0）加 `#available`，12 上退回 `~/Library/LaunchAgents` 的 plist（§8.9）；AppKit 闸门 A1–A6 在 12 上重测 |
| macOS 11 或更早 | — | Xcode 27 构建的程序不能运行（向下部署的下限是 12）。先升级系统；不能升级时，这台 Intel Mac 不在 v1 的支持范围内（由 §12 第 1 条的回答决定） |

只装 Command Line Tools 时能否运行 XCTest / Swift Testing 未查证，所以 Intel Mac 需要编译时装完整 Xcode，P0 验证每台 Mac 用自己的工具链能跑测试。

### 9.2 版本钉住

- `swift-tools-version` = 三台机器都能用的最低版本：Intel Mac 编译时取它的 Swift 版本；Intel Mac 只运行时取 WSL 与 Apple 芯片 Mac 共同的 6.3。
- 语言模式 Swift 6；不用只有 6.4 才有的语言特性（SE-0521、SE-0522 `@diagnose`、SE-0491 模块选择器、SE-0516、SE-0527 等）。
- 共享代码不用比 macOS 14 新的 API：用锁而不是 `Mutex`（macOS 15），不用 `InlineArray`（macOS 26）。
- 部署目标 macOS 14；Intel Mac 跑不了 Sonoma 时按 §9.1 的表取 13 或 12（12 是 Xcode 27 的向下部署下限）。这时 macOS 14 才有的接口（`yieldActivation(to:)` 等）和 13 才有的 SMAppService 都放在 `#available` 里，并各有退路。
- WSL 装同一个 Swift 版本，另用 6.4.0 做第二次检查。

### 9.3 WSL 内循环

```sh
swift build --package-path mac --scratch-path ~/.cache/pincc-swift-6.3 --build-system native
swift test  --package-path mac --scratch-path ~/.cache/pincc-swift-6.3 --build-system native
tools/check.sh            # C# 测试 + 新鲜度 + 台账 + swift test + lint + 交替读写
tools/check.sh --deep     # 5,000 个脚本种子 + deep 语料
tools/check.sh --fuzz     # dotnet.exe 与 Swift 并排的实时差分模糊
```

- 主检查版本（例如 6.3）和第二检查版本 6.4 各用自己的 `--scratch-path`（`~/.cache/pincc-swift-6.3`、`-6.4`），脚本显式传 `--build-system`：6.3 默认 native，6.4 默认 Swift Build，两者的产物目录和中间文件不能混在一起。
- 构建目录放在 Linux 文件系统（`~/.cache`），测试数据目录放在 ext4（`/tmp` 或 `~/.cache`），从不放在 `/mnt/d`。
- Linux 上的 Foundation 是 swift-corelibs-foundation，官方只承诺“尽力兼容”，所以 WSL 通过不代表 Mac 通过，同一套测试必须在两台 Mac 上也跑。设计上对 Foundation 的依赖尽量少（NSRegularExpression、FileManager、Data、ProcessInfo）。
- 调用 dotnet.exe 的规则见 §5.6 的 G1。

### 9.4 用 SwiftPM 组装 .app（scripts/bundle.sh）

1. `swift build -c release`（通用构建见 §9.5）；产物目录用 `swift build … --show-bin-path` 取得，不写死路径。
2. 建 `PinCC.app/Contents/{MacOS,Helpers,Resources}`，复制 `PinCC`、`pincc`、`Info.plist`、预先生成的 `PinCC.icns`（不需要 actool，actool 只随 Xcode 提供）。
3. 应用目标不声明任何 SwiftPM 资源：原生构建系统生成的 `Bundle.module` 会在 .app 根目录找 `<包>_<目标>.bundle`，放在根目录会破坏签名封装。可移植目标的表以 base64 字面量嵌入；其他资源放进 `Contents/Resources`，用 `Bundle.main` 读取。Apple 芯片 Mac 上的 Swift 6.4 实际用的是 Swift Build（§9.5），这条依据在 P0 S2 按 Swift Build 重新确认；不声明资源的做法与构建系统无关。
4. 签名（§9.6）。
5. `codesign --verify --deep --strict` 必须通过（P0 闸门 S2）。

### 9.5 通用二进制

- 首选：`swift build -c release --arch arm64 --arch x86_64`。Swift 6.4（Xcode 27）起 SwiftPM 默认就用 Swift Build，单架构构建也一样（swift-6.4.0-RELEASE 的 Options.swift:557）；6.3.x 及以前默认 native，多于一个 `--arch` 时改用 `.xcode`（XCBuild，需要完整 Xcode，swift-6.3.3-RELEASE 的 Options.swift:561）。bundle.sh 用 `--show-bin-path` 取产物目录。
- 退路：分别 `--triple arm64-apple-macosx<部署目标>` 和 `--triple x86_64-apple-macosx<部署目标>` 构建（部署目标按 §9.1 取 14.0、13.0 或 12.0，与 Package.swift 的 platforms 相同），再 `lipo -create`。
- 只装 CLT 时能否生成通用二进制、产物路径在哪里，都未查证，P0 实测。
- `lipo -archs` 必须显示 x86_64 和 arm64；x86_64 那一份以在 Intel Mac 上原生运行为准，Rosetta 只作辅助（Rosetta 的完整支持只到 macOS 27）。

### 9.6 签名与分发

- **自用（两台 Mac）**：
  - 在 Apple 芯片 Mac 上建一个**专用的构建钥匙串**，里面只放一个自签名的“代码签名”证书（或免费 Apple ID 的 Apple Development 证书）。钥匙串有自己的密码，存在 Mac 上权限为 0600 的文件里，不进 WSL，不进仓库。
  - P0 在 Mac 前一次性建好（ssh 里做不了的步骤只有这些）：`security create-keychain` 建 `pincc-build.keychain`，`security set-keychain-settings` 关闭自动锁定；导入身份时加 `-T /usr/bin/codesign`；执行 `security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k <密码> pincc-build.keychain`，让 codesign 不经弹窗访问私钥；`security list-keychains -d user -s pincc-build.keychain login.keychain-db` 把它加进搜索列表（或 codesign 时传 `--keychain`）；确认 `security find-identity -v -p codesigning pincc-build.keychain` 能列出这个身份，列不出时在“钥匙串访问”里把证书设为“代码签名：始终信任”。
  - `tools/mac.sh app` 在同一条 ssh 命令里先 `security unlock-keychain`，再 codesign。只解锁、不设分区列表时，ssh 里同样会报 errSecInternalComponent 或挂起（需要弹窗授权却没有图形界面）。P0 的 S2 必须经 ssh 完成，签名过程中不能出现任何弹窗。
  - 不开 hardened runtime。身份稳定，所以文件夹授权和登录项批准在更新后仍然有效；ad-hoc 签名每次构建都变，授权会重新弹。
  - 签名顺序由内而外：`Contents/Helpers/pincc` → `Contents/MacOS/PinCC` → 整个 .app。
  - 用 scp 或 rsync 拷到 Intel Mac，不带隔离属性，Gatekeeper 不拦。macOS 27 上本地构建、ad-hoc 签名、无隔离属性的程序能直接启动，SMAppService 也能注册（KlangLadder #7 的报告）。
- **给别人用**（不在本计划内）：Apple Developer Program（每年 99 美元）、Developer ID Application 证书、hardened runtime、安全时间戳、notarytool 公证并 staple、dmg。Swift 程序不需要 JIT 权限。无沙盒、有 Carbon 热键、SMAppService、WKWebView 的程序预计不需要额外的 entitlement，公证时以日志为准。
- **不沙盒，不上 App Store**（§8.13）。

### 9.7 tools/mac.sh

| 模式 | 做什么 |
|---|---|
| `test` | 把代码推到指定 Mac 的裸仓库（未提交的改动用 rsync，带 `--filter=':- .gitignore'`，排除 artifacts/、bin/、obj/、dist/、.probe/），ssh 运行 `swift test` |
| `fixtures` | 在 Apple 芯片 Mac 上用 `pincc-oracle` 生成 `mac-*` 样例，取回到仓库 |
| `app` | 发布闸门检查（§5.16）→ 通用构建 → bundle.sh → 签名 → 严格校验；默认 bundle id 带 `.dev`，`--personal` 用正式 bundle id |
| `verify` | 先 app，再用 `open -W -g -j -n --env … --stdout <日志> --stderr <日志>` 以屏幕外模式跑完所有场景，取回 layout.json 和 PNG；需要该用户在 Mac 上已登录图形界面。通过 ssh 渲染失败时，改为在 Mac 的终端里直接运行 |

### 9.8 Xcode 27 / macOS 27 要点

- Xcode 27（Swift 6.4、macOS 27 SDK）只在 Apple 芯片上运行，要求 macOS 26.6 或更高；能部署到 macOS 12–27；macOS 27 SDK 仍能向 macOS 12 及以上部署通用（Intel + Apple 芯片）程序；只有部署目标 ≥ 27.0 时 ARCHS_STANDARD 才去掉 x86_64。
- Intel Mac 永远跑不了 Xcode 27 和 Apple 的 Swift 6.4。
- Swift 6.4 的变化里与本项目有关的：Swift Build 成为 SwiftPM 的默认构建系统；`swift test` 可以在 `@Test` 里用 XCTAssert，在 XCTest 里用 `#expect`（ST-0021）。Subprocess 1.0 是单独的包，不用（零依赖）。
- CSQLite / CZlib 的 module map 在 macOS 27 SDK 上是否与 SDK 自带的 SQLite3 / zlib 模块冲突，P0 闸门 S1 验证（用 shim.h 间接包含，而不是直接拥有系统头文件）。

---

## 10. 实施阶段（工作量约 131 个工作日，关键路径约 126 个工作日）

### 10.1 各阶段

#### P0 · 工具链与 AppKit 闸门（5 天；在 Mac 上，丢弃式；与 P1 并行，不占关键路径）

**前提**：你对 §12 第 1、9 条的回答（第 10 条的默认热键可以等到 A6 时再定）。

**工作**
- **第一天**：记下 Intel Mac 的型号和 macOS 版本，按 §9.1 的表确定 Intel 的角色、`swift-tools-version`、部署目标和 WSL 的 Swift 版本。
- WSL：swiftly 安装选定的 Swift 版本和 6.4.0；`sudo apt install libsqlite3-dev zlib1g-dev`；构建目录放 `~/.cache`。
- Apple 芯片 Mac：Xcode 27（macOS 26.6+ 或 27）；开启“远程登录”，按 §9.1 设好电源和固定 IP；建 git 裸仓库；按 §9.6 建专用构建钥匙串、导入自签名证书、设分区列表和搜索列表；写好 `tools/mac.sh`。
- Intel Mac：按表安装完整 Xcode（或确定为只运行）。
- 在仓库外的 `~/spikes` 里建临时工程，逐项验证。

**Swift 工具链闸门**
- S1：只用 Foundation 的包，带 CSQLite、CZlib 系统库目标（shim.h 间接包含），在 WSL 和两台 Mac 上 `swift test` 通过；记录各处的 `sqlite_version`；`bind_text16`/`column_text16` 往返 emoji、NUL、非法 UTF-8；macOS 27 SDK 上没有模块冲突。
- S2：bundle.sh 用 SwiftPM 产物组装 PinCC.app（Info.plist 带 LSUIElement、预生成的 .icns、`Contents/Helpers/pincc`），用构建钥匙串里的稳定身份由内而外签名，`codesign --verify --deep --strict` 通过。
- S3：通用构建（`--arch arm64 --arch x86_64`，或分别构建再 lipo）在 Intel Mac 上原生运行。
- S4：CLI 冷启动时间；不闪 Dock、输出 UTF-8、退出码正确。
- S5：`ssh … open -W -g -j -n --env` 把视图渲染成 PNG（`displayIfNeeded()` 之后用根图层的 `render(in:)`，附录 B），图里必须看得到 8 pt 圆角、1 px 描边（到期 2 px 红）和溢出淡出。
- S6：每台 Mac 用自己的工具链能跑 Swift Testing 和 XCTest。

**AppKit 闸门**（Apple 芯片 Mac 上的 macOS 27，Intel Mac 上它自己的系统）
- A1：init 时传入 `.nonactivatingPanel` 的无边框面板：Safari 在前台时点击不激活 PinCC；点正文、角标、图片、空白处都能让面板成为 key（覆写 `needsPanelToBecomeKey` 和在 `sendEvent` 里 `makeKey()` 两种做法都测，§8.2），能收到 Return 和 ⌘⌫；`acceptsFirstMouse` 和 `.activeAlways` 悬停有效；后台时在 mouseMoved 里能否设置缩放光标；显式拖动有效。
- A2：输入法：面板里的 NSTextView 用苹果拼音和搜狗都正常（候选窗位置、回车上屏、Esc）；隐藏的编辑菜单让 ⌘C/V/Z 可用；Esc 把键盘还给之前的程序且卡片不闪（方案 A 或 B）；自动替换全部关闭后输入 `"a" -- ---` 原样保存。
- A3：选择器：NSPopover 与不激活子面板，各自在 PinCC 不在前台时能否打字（含拼音）、是否激活程序。
- A4：桌面空间：显示桌面、调度中心、台前调度，“显示器具有单独的空间”开和关，到期卡片的 `.stationary`，ShowAll。
- A5：30 和 50 个面板的物理内存（footprint 或 vmmap）和空闲 CPU，两台 Mac 都测。
- A6：Carbon 注册 ⌃⌥⌘N 成功、重复注册返回 −9878；NSStatusItem 经 button 的 target/action 工作，左右键都弹菜单，不读 currentEvent 的类型，与 `NSStatusItemExpandedInterfaceSession` 比较（§8.1）；稳定身份下 SMAppService 能注册，登录启动的判断方法；WKWebView 渲染时记录 `document.visibilityState`，比较不显示的窗口和“排到屏幕上、alphaValue 接近 0”的窗口能否取出 JS 页面的 DOM（§8.14）；后台热键读剪贴板时 `accessBehavior` 是否弹提示。

**验证**：`docs/mac-port.md` 记录每一项的通过或失败、所用命令、测得的数字（30/50 面板的内存和 CPU、CLI 冷启动、各机器的 sqlite_version）。

**完成标准**
- S1、S2（经 ssh、无弹窗）、S3、S6、A1 必须通过；A2 里的拼音上屏必须通过，Esc 交还失败时采用焦点方案 B。
- A3 决定选择器用子面板还是 NSPopover（默认子面板，§8.5）。
- S5 失败时，verify 改为在 Mac 的终端里直接运行（§9.7），不阻塞后续阶段。
- A4、A6 只记录结果，分别用来确定 §8.11 的桌面空间设置和 §8.9 判断登录启动的方法、§8.14 的渲染窗口做法。
- A5：30 个面板时内存 ≤150 MB、空闲 CPU 约 0%；不满足就先重新设计图层，再继续。
- S1–S3、S6、A1 或 A2 的拼音上屏失败，且没有办法绕过时，停在 P0，重新评估。
- 只新增 `tools/mac.sh` 和 `docs/mac-port.md`，不改任何现有文件。

#### P1 · 基准实现的基础（13 天；只动 tests/ 和 oracle/，Windows 不变）

**前提**：你对 §12 第 11 条的回答。它决定 §5.2 是否生成 big5 等代码页表，以及 §5.3 是否生成纽约和柏林的夏令时网格。

**工作**
- 第一天：G1（从 WSL 调 dotnet.exe 的方式，§5.6）；G3（记录区域和 ICU 版本，§5.14）；G6（build.cmd 的时间预算和仓库体积预算）。
- `contracts/` 目录；`artifacts/` 加进 .gitignore。
- `tests/Vectors` 框架：JSONL 写入、UTF-16 和 base64 编码、文件头（含 ICU、winsqlite3、运行时版本）、SplitMix64、`--expect`、语义摘要、覆盖率统计。
- .NET 基础语义表（§5.2）。
- 数据表导出（§5.1）：在 `tests/PinCC.Tests.csproj` 里加入 Hotkeys.cs、Palette.cs、ClipData.cs；方法体内字面量表的词法抽取器；集合字面量扫描（含 `is … or …` 模式和 switch 的字符串分支）。正则清单和按行计数的源码扫描门槛（§5.4）。
- 首批向量族：Card 派生状态、Hash、Tag、AutoColor；Card 和 CardMeta 的 JSON 字节和宽容读取；Markdown 全家；When（这时只做显式传入“现在”的单元）；TextDecode；UrlGuard；Host；WinPath；HtmlDecode；FromHtml 和 MainText（保存的网页语料）；固定文件夹上的文件信息；Llm 纯函数；用反射调用的 Cli 私有辅助函数。
- `tests/Ledger`：按词法工作的成员扫描器、变异自测、初始台账（全部标为 pending 或 platform-only），以及 XAML 整文件哈希、生成环境指纹、词法抽取段哈希这几类非成员条目（§5.11）。
- 数据字典扫描（C# 一侧）。
- `oracle/wpf`：OracleWpf 的卡片渲染（§5.9 的工程要求和哨兵检查：不调用 Desk.Start 和任何会走到 DoLayout 的方法，不建卡片的 HWND，对 `Surface` 做 Measure/Arrange；导出 `Desk.Readable`、菜单规格和图标；W1c 之前先在 OracleWpf 里临时复制 App.cs:18-21 的两行，W1c 之后改为调用 `App.Configure`，台账跟踪这两行）。
- `tools/check.sh`、`sync.sh`、`ui-snap.sh`；`RealDataCensus`。
- 录下 B0（§5.14），以当前 HEAD 为准。

**验证**：`dotnet run --project tests` 通过且确定：连续两次生成逐字节相同；OracleWpf 的哨兵检查通过（含每张卡片 `Hwnd == IntPtr.Zero`、没有可见窗口和 Toast）、连续两次输出相同；`git status` 显示 src/ 没有任何改动；build.cmd 的时间在预算内。

**完成标准**：约 25 个向量族提交；台账覆盖 src/Core 的全部成员；B0 存在 `artifacts/baseline-win`，哈希提交在 contracts/。

#### P2 · Windows 确定性接缝与规范卫生（4 天；W1、W2）

**前提**：你对 §12 第 2、7、14 条的回答（第 7 条决定 W2l，第 14 条决定 W2m）。

**工作**：W1a–W1d；W2 各项，每项一个提交，含 W2h 的 `Cli.Cmd` 常量、W2l 的图片行（若同意）、W2m 的续列表行尾（若选择在 C# 里修）；用固定时钟重新生成与时间有关的向量（CLI、Backup）。

**验证**（每个提交之后）：C# 测试；zh-CN 下的全部 B0 向量不变，只有提交说明列出的边界向量变化；CLI 录制除 W2l 的图片行外不变；OracleWpf 卡片 PNG / 场景和 ui-snap 快照相同；真实数据普查为空或已由你签字；审阅确认 System 时钟路径是恒等。

**完成标准**：W1、W2 全部合入，各有差异清单；不再需要时间遮罩的确定 CLI 录制已提交；你把重建的 dist 版（先改名 `.old`）用于日常，几天内没有回退（日历上的试用期，与 P3、P4 重叠）。

#### P3 · L1 数据格式规范、W3、存储基准（9 天）

**前提**：你对 §12 第 3、4 条的回答。

**工作**
- `contracts/DATA.md`、`settings-schema.json`：每一列、每个设置键的作用域 / 取值语法 / 是否敏感、每个操作名、每个 meta 键和 State 值、每个文件夹、WAL 规则、`data.features` 特性表。
- W3a–W3f 各自提交（经你同意）。
- 操作脚本格式、C# ScriptDriver、随机脚本生成器（300 个种子 × 60 步；`--deep` 5,000）、提交轨迹。
- CLI 录制：约 120 条。
- `win-*` 样例，包括异机内容样例、毒样例、图片修改时间清单。
- C# 的 Purge 保留测试；对 C# 读取路径的畸形数据模糊测试。

**验证**：C# 测试通过；数据字典完整性通过；W3 的向量差异只在声明的情形；真实数据普查的变化已签字；`LocalPath`/`ForeignPath` 的源码扫描通过；异机内容检查（OracleWpf 从不调用 `Desk.Start`，§5.9）：
1. OracleWpf 进程先在进程内设置 `PINCC_HOME` 指向样例副本，副本里预置 `autostart=0`、`llm.detected=1`；样例的 Mac 路径用 `/Users/<随机名>/…`，在 Windows 上会解析到一个不存在的路径，所以任何一次漏网的 stat 都会把 meta 写成 missing，在转储里现出来；
2. 用反射把样例卡片放进一个没有 Start 的 Desk 的 `_cards`（不建 HWND，§5.9），`Llm` 用反射设为一个指向临时目录里记录程序的自定义命令；
3. 用反射调用 `RefreshFiles`、`Enrich`、`AutoSummarizes`、`Summarize`（userAsked 为 false，走与手动 ✨ 相同的文件分支，只是不弹提示），再对正文里的 `![](/Users/….png)` 调用 `LoadImage`；
4. 要求：转储前后相同（零 SaveMeta）；记录程序从未被启动；LoadImage 返回占位图；卡片场景带“在另一台电脑上”标记；每次运行前后断言没有 `IsVisible` 的窗口、没有 Toast、HKCU\…\Run\PinCC 的值不变、没有注册热键。
5. `Desk.Open`、`Reveal` 和手动 ✨ 在异机路径上按设计会经 Notify 弹提示条，这是一个真实窗口，所以不在这里运行：它们由 W3b 的源码扫描（入口处先判断 `ForeignPath`）和 `ForeignPath` 向量（含点来源标题的 `c.Src`、正文链接 `[x](/Users/…)`、`[x](file:///Users/…)`）覆盖，再由你在 W3 发布后的试用里用 CLI 加一张 `/Users/…` 卡片，点来源标题和 ✨ 各一次确认，然后删掉它。

**完成标准**：存储和 CLI 的基准完整且确定；W3 的各项决定已由你批准（或改为在 DATA.md 里记录的“照搬”）；**W3 的 Windows 版在 Swift 写你的真实数据之前（最迟 P8 开始前）发布**。

#### P4 · Swift 基础：DotNetCompat 与存储核心（14 天；WSL 快循环）

如果 P2、P3 在等你的批复，DotNetCompat 部分可以在 P1 之后直接开始。

**工作**：`mac/Package.swift` 和全部目标；DotNetCompat 全部内容；PinCCCore 的 Db（UTF-16 文本接口）、Store 全部、Card、CardMeta、Home；`pincc-oracle`；`tools/interleave.sh`；LintTests、LedgerTests（全行覆盖）；Swift 一侧的数据字典扫描；`tools/mac.sh fixtures` 生成 `mac-*`，C# 计算期望结果；G5 真实数据只读检查。

**验证**：WSL 里 DotNetCompat 向量 100% 通过；所有种子的操作脚本每一步都相同；交替读写全部通过（含交叉 Purge、交叉 Restore、只读打开）；两台 Mac 上同样通过；C# 的互开检查在 `mac-*` 上通过；你的备份副本由 Swift 列出的结果与 C# 相同。

**完成标准**：存储层在 ext4 和 APFS 上双向证明一致；台账里 Db、Store、Card 已移植并接受。

#### P5 · Swift core 其余部分与 CLI（13 天）

**工作**：When；Markdown；TextDecode；HtmlExtract 和 JsonLd；UrlGuard；FileFacts 和 ZipReader；WebRoute；Llm（纯函数）；Proc 和辅助子进程契约测试；PinCCCli 和 `pincc`；`pincc doctor`；main.swift 的 CLI 路径；`pincc-selftest`（不依赖 XCTest，跑全部向量族、操作脚本和 CLI 录制，供不能编译的 Intel Mac 原生运行，§3.2、§9.1）。

**验证**：核心向量族在 WSL 和两台 Mac 上全部通过（Intel Mac 不能编译时经 `tools/mac.sh test --intel` 运行 `pincc-selftest`）；CLI 录制 × {win, mac} 样例 × {C#, Swift 进程内, Swift 子进程} 在规范化后相同；Proc 契约测试在 WSL 和两台 Mac 上通过。

**完成标准**：src/Core 的每个成员在台账里都是 ported 或 platform-only，没有过期条目；Swift CLI 能在 Mac 上操作一份复制过去的 Windows 数据目录。

#### P6 · Windows 桌面基准（10 天；只在 P0 结论为“继续”之后开始）

**前提**：你对 §12 第 6 条的回答。

**工作**
- 先由 OracleWpf 对未修改的代码录下特征输出，只调用不显示窗口、不经过 DoLayout 的实例方法（§5.14）。
- W4a：Rules.cs，每个源文件一个提交（Desk、CardWindow、HubWindow、Flyout、ManageWindow），加 Layout 的 Trim、Spot、HubRect 和具名常量；方法体内的字面量表搬成具名静态成员，§5.1 的导出改为反射读取；另一个提交从 `Llm.Ask` 抽出 `PreparePrompt`、`Outcome`。
- W4b：LayoutPass 影子模式，`shadow.log`。
- W4c：PINCC_TRACE 录制器（若你同意）：完整正文、起始快照、`_cards` 枚举顺序，按天分文件、总量 50 MB 上限（§3.5 W4c、§5.10）；`tools/replay-trace.sh`。
- OracleWpf 场景：CardScene、MdRender、MenuSpec、选择器模型，固定时钟、时区和屏幕。
- Rules 和 LayoutPass 的向量生成器；敏感性分析（§5.9）。
- `contracts/desk-transitions.md`、`desk-commands.json`。

**验证**：移动后的代码重现特征输出；C# 测试通过；ui-snap 和 OracleWpf 输出相同；影子版发布给你日常使用，定期检查 `shadow.log`（目标为零）；OracleWpf 从未显示、从未激活任何窗口。

**完成标准**：Rules、LayoutPass、场景、Markdown、菜单向量已提交；影子版在日常使用中，10 天试用开始（与 P7–P10 重叠）；台账覆盖 Desk、CardWindow、Hub、Flyout、Manage、Hotkeys 的成员（ported、pending 或 platform-only）。

#### P7 · Swift PinCCDesk（10 天）

**前提**：你对 §12 第 14 条的回答（EditorOps 向量的形状）。

**工作**：Rules；LayoutPass；DeskCore（EditSession、UndoStack、GestureClock、按 Id 有序的注册表）；CardPresenter → CardScene；EditorOps；MdRender；HubModel、选择器、卡片库、SettingsSchema（作用域和取值语法守卫）；KeyBindings；MenuSpec；ClipIngest；DataRules 的异机路径运行时状态；每一行转移的 FakeHost 场景；轨迹重放工具。

**验证**：Rules、LayoutPass、场景、Markdown、菜单向量在 WSL 和两台 Mac 上通过（实测字段按 §5.9 的规则）；FakeHost 场景通过；已有的 Windows 轨迹从起始快照开始重放，批量命令和布局按轨迹里录下的 `_cards` 顺序（“注册表顺序”参数，§5.10 第 5 条），完整转储逐字节相同。

**完成标准**：桌面层全部移植并在台账里接受；设置作用域守卫证明 Mac 从不读写 Windows 专有的键。

#### P8 · Mac MVP（7 天）

**工作**：main.swift 和 AppDelegate（`.accessory`）；MacScreens（y 翻转、`mac:` 显示器 id、visibleFrame）；CardPanel 和按 CardScene 绘制的内容；MdAttributed；在一个 CATransaction 里应用 LayoutPass；1 秒节拍 + 到点唤醒；`data_version` 轮询；EditorTextView（P0 选定的焦点方案、自动替换全部关闭）；HubPanel；Toast；屏幕外 verify 模式（把场景渲染成 PNG 加 DIP 单位的 layout.json）。

**验证**：`tools/mac.sh verify` 渲染 P6 的场景，与 WPF 基准的 PNG 并排由你检查；layout.json 等于 LayoutPass 向量；`pincc add x` 后一次节拍内出现卡片；一份 Windows 数据目录的副本能打开、列出、编辑。

**完成标准**：两台 Mac 上卡片能显示、平铺、编辑；一份 Windows 数据目录往返一次：在 Mac 上编辑，复制回去，Windows 打开，撤销历史可用。

#### P9 · Mac 交互对齐（8 天）

**工作**：转移表里的每个手势：撤销条、临时展开、胶囊、缩放（触控板增量累计成滚轮档位）、显式拖动和缩放（KeepAspect）、透明度（⌘+滚动）、单卡命令、批量命令、提醒（只响一次、每批一声、全屏推迟）、提醒和颜色选择器（默认子面板，以 P0 的 A3 为准）、由 MenuSpec 生成的菜单、卡片库和历史、§8.19 的 Mac 快捷键。

**验证**：FakeHost 场景；两台 Mac 上按 docs/mac-port.md 的清单手工检查：
- 卡片和提醒选择器里都能用拼音输入；Esc 后回到之前的程序；点卡片的正文、角标、图片、空白处都能让卡片成为 key；
- Safari 在前台时，第一次点击 ○ 就能完成卡片；PinCC 不在前台时悬停按钮仍会出现，失去焦点时卡片不消失；
- PinCC 不在前台时悬停 + 出现速拨，三个按钮第一次点击就生效；
- 单击未在编辑的卡片后，Return、⌘⌫、⌘`、⌘F 都能收到；⌘C、⌘V、⌘Z 可用，撤销条上的 ⌘Z 也可用；
- ⇧⌘X、⌥⌘C、⇧⌘L、⇧⌘8 没有被系统、输入法或常用程序占用；
- Safari 全屏所在的空间里没有卡片，提醒声被推迟；
- 卡片全部被挡住 10 分钟后提醒仍在 1 秒内触发；全屏应用在前台、全部最小化时也一样；睡眠唤醒后也一样（§8.10）；
- 撤销条、临时展开、胶囊状态的 verify 场景与 Windows 相同。

**完成标准**：每一行转移在两台 Mac 上都已通过；除声明的差异外没有未解决的行为差异；如果你同意了 W4c，每一行转移都至少被一条真实轨迹命中，或者已按 §5.10 第 4 条由你签字豁免、在 desk-transitions.md 里标为“只由 FakeHost 场景覆盖”。

#### P10 · Mac 系统功能（9 天）

**工作**：Carbon 热键和录入（`mac.hk.*`、KnownConflicts.Mac）；剪贴板、拖入拖出、HEIC 转码（方向写进像素）、accessBehavior 提示；URLSession 抓取；WebKitRenderer；ToolHost.Mac（登录 shell 环境、`mac.llm.*`）；SMAppService（`mac.autostart.asked`）；提示音；唤醒、时钟、时区通知（重置时区缓存）；桌面空间和全屏；经 button 的状态栏图标；SwiftUI 设置和卡片库；“安装命令行工具”；数据目录位置检查（§4.2.9）。

**验证**：回环 HTTP 录制服务器的 Mac 测试；从访达启动后的手工检查：
- “检测”在两台 Mac 上都能在正确的 Homebrew 目录下找到 claude、codex、ollama，首次运行也能找到用 nvm 装的工具；
- ✨ 对一个网址和一个从 `~/Documents` 拖进来的 PDF 都有效，同一个构建里只弹一次写明 PinCC 的授权；拒绝授权后文件卡片显示“需要授权”；
- 被别的程序占用的热键会被报告；
- ⌃⇧⌘4 截的图能粘贴进新卡片；从 Safari 拖进来的链接保留标题和网址；从 Safari 和 Chrome 复制文字后粘贴都记下来源网址；
- 安装后新开的终端里 `pincc` 可用；登录项开关与系统设置一致；
- 在 Mac 上写的笔记摘要到 Windows 上不显示为过时。

**完成标准**：功能完整。L1 手工往返在真实数据上通过：Windows → Mac → Windows 搬一次，卡片、图片、撤销、摘要不过时、各作用域设置不被改动、异机文件卡片显示标记且 meta 不被改写。

#### P11 · L2a 导出导入与数据搬迁（7 天）

**前提**：你对 §12 第 5 条的回答。

**工作**：W5（C# 先实现 `export`、`import`，GUI 启动时建按数据目录命名的互斥量，`Cli.GuiRunning` 接缝），再实现 Swift 的 Bundle 和 CLI 命令；GUI 运行时拒绝导入（两端都按数据目录判断）；包用建表语句加 `ATTACH` 和 `INSERT … SELECT` 生成，敏感设置和本机作用域的键从不写进包；交接检查和 `--force`；首次导出时生成 `db.id`；录制和交替读写覆盖；`docs/DATA-MOVE.md`（导出导入、退出后复制整个目录、永远不放进云同步文件夹）；可选的 Windows 端位置警告。

**验证**：Windows 导出 → Mac 导入和反向，在 WSL 交替读写和两台 Mac 的样例上都通过；导入后的转储除本机作用域和本机平台的设置外与源相同；经接缝的“GUI 在运行 / 不在运行”两种录制，测试从不创建或打开真实的互斥量；交接检查的拒绝和 `--force`、不带 `--replace` 的退出码 1；只复制 `pincc.db` 而不带 `-wal` 会丢提交、`export` 不会的反例测试；在设置里放一个已知的假密钥，导出后按字节搜索整个包文件（包括空闲页）必须找不到，两端各跑一遍。发布 W5 的 Windows 版并重启 GUI 之后，你才在真实数据上使用 import（旧版 GUI 没有按目录的互斥量）。

**完成标准**：两个方向都能用每边一条命令搬一份数据。

#### P12 · 性能、打包、Intel、终验、LayoutPass 切换（5 天）

**工作**
- 测量和调整：30、50 张卡片的内存和空闲 CPU，布局耗时，2 MB 网页上的正则耗时；可选的 WAL 文件监视。
- 发布打包：通用构建、稳定身份签名；dmg 可选。
- 在 Intel Mac 上完整运行一遍（能编译时 `swift test`，否则 `pincc-selftest`）。
- 三台机器上跑全部向量族的完整对照。
- 影子日志连续 10 天为零后，在 Windows 上切换到 LayoutPass，删除旧的 DoLayout 主体；然后提交 W4d（按 Id 排序，声明的行为变化）。
- docs/DESIGN.md 增加由表生成的“平台差异”一节；README 增加 macOS 一节（安装、快捷键表、数据目录、CLI、数据搬迁）。

**验证**：`tools/check.sh --deep` 通过；两台 Mac 上 `tools/mac.sh test` 通过；切换后 B0 和 LayoutPass 向量不变，屏幕外快照相同；W4d 的向量变化只在声明的情形。

**完成标准**：Mac 版在两台 Mac 上日常使用；Windows 运行切换后的 LayoutPass 没有回退；台账除声明的 platform-only 外没有过期或待办条目。

#### P13 · 预留（17 天，约 15%）

**工作**：修复三台机器日常使用中发现的问题、macOS 27 小版本带来的 AppKit 变化、输入法和焦点的边角情况、超出规则的换行差异。

**验证**：每个修复都走 §5.15 的变更流程。

**完成标准**：两周的双平台使用中没有一致性缺陷。

### 10.2 累计

| 阶段 | 天数 | 关键路径累计 |
|---|---|---|
| P0 工具链与 AppKit 闸门（与 P1 并行，不占关键路径） | 5 | —（与 P1 同在第 1–13 天） |
| P1 基准实现的基础 | 13 | 13 |
| P2 确定性接缝与规范卫生 | 4 | 17 |
| P3 L1 数据格式、W3、存储基准 | 9 | 26 |
| P4 DotNetCompat 与存储核心 | 14 | 40 |
| P5 core 其余部分与 CLI | 13 | 53 |
| P6 Windows 桌面基准 | 10 | 63 |
| P7 Swift PinCCDesk | 10 | 73 |
| P8 Mac MVP | 7 | 80 |
| P9 Mac 交互对齐 | 8 | 88 |
| P10 Mac 系统功能 | 9 | 97 |
| P11 L2a 导出导入 | 7 | 104 |
| P12 性能、打包、终验、切换 | 5 | 109 |
| P13 预留 | 17 | 126 |

- 工作量合计约 131 天（含 P0 的 5 天；不含预留 114 天，预留 17 天约 15%）。P0 与 P1 并行，所以关键路径约 126 个工作日。§0、§2.2 和本章标题都按“工作量约 131 天，关键路径约 126 个工作日”写。区间 118–150 天。
- 架构初稿估 88 天，评审按各阶段的实际内容复核为约 115–130 天（每个大阶段约超出一倍：P1 要做 .NET 语义表、约 25 个模糊向量族、台账和数据字典；P4 要做约 25 个 DotNetCompat 组件加存储核心），6 天的预留也不够，所以采用复核后的数字，并把预留提到约 15%。
- 第二轮核对（附录 E.5）新增的工作共约 8 天，预留随之加 1 天：
  - P1 +2：测试项目加入 Hotkeys/Palette/ClipData 和方法体内字面量的词法抽取器（§5.1）；台账的非成员条目（§5.11）；OracleWpf 不建 HWND、不调用 Start 的做法和新增哨兵（§5.9）；
  - P2 +1：W2h 的 `Cli.Cmd`、W2l 的图片行、W2m 的续列表行尾（§3.5.1）；
  - P3 +1：W3b 出入口扩到任何路径字符串和正文链接，源码扫描按参数来源判断；不调用 Start 的异机内容检查（§10.1 P3）；
  - P5 +1：`pincc-selftest`（§3.2、§9.1）；
  - P6 +1：W4c 记完整正文、起始快照和 `_cards` 顺序，重放工具；`Llm.PreparePrompt`、`Outcome` 的抽取（§3.5）；
  - P11 +2：按数据目录的互斥量和 `Cli.GuiRunning` 接缝、交接检查、按 `INSERT … SELECT` 生成包、密钥字节搜索测试（§4.4.1）。
- 日历时间另有：P2、W3 发布后的试用；LayoutPass 的 10 天影子期（与 P7–P10 重叠）；等待你对 §12 各条的批复；以及下面你本人要做的事。
- 关键顺序：第 1 天 P0 ∥ P1 → P4 的 DotNetCompat 可提前开始 → 拿到批复后 P2、P3 → P4 存储、P5 → P0 结论为“继续”后 P6，影子期马上开始 → P7–P10 → P11 → P12 切换。

**你需要做的事**（不计入上面的工作日，但要排进你的日程；每项结果由你勾选后写进 docs/mac-port.md）：

| 阶段 | 内容 | 时间 |
|---|---|---|
| P0 | 两台 Mac：安装 Xcode（要登录 Apple ID）、开启远程登录、建构建钥匙串并设证书信任（§9.6）、在“节能”里关闭自动睡眠并开启“唤醒以供网络访问”、在路由器上给两台 Mac 固定 IP；A2、A3 的输入法检查要你亲手打字 | 约 1 天 |
| P2–P6 | 把重建的 dist 版（先改名 `.old`）用于日常；每个 W2、W3 提交的真实数据普查签字；W3 发布后点一次 `/Users/…` 样例卡片 | 零散 |
| P6–P9 | 影子期间日常使用并开着录制器；P9 开始前按清单手工触发未命中的转移行（约 30 分钟），对仍无法命中的行签字豁免（§5.10） | 约 1 小时 |
| P8 | 并排比对 Mac 和 WPF 的 PNG | 约 1 小时 |
| P9 | 每台 Mac 按清单检查输入法、焦点、快捷键、提醒 | 每台约 2 小时 |
| P10 | 每台 Mac：TCC 授权、登录项批准、剪贴板“始终允许”、从访达启动后的检查 | 每台约 1 小时 |

`tools/mac.sh` 连不上某台 Mac 时，该 Mac 的闸门记为“待补”：不阻塞 WSL 的内循环，但阻塞 `tools/mac.sh app`。

### 10.3 可以由你决定的裁剪

| 项 | 内容 | 节省 | 代价 |
|---|---|---|---|
| a | 只固定 Asia/Shanghai 一个时区（无夏令时），去掉纽约、柏林的夏令时网格和 LocalDateTime 的歧义标记建模 | 约 3 天 | 如果你在有夏令时的地区用 Mac，在回拨那一小时里设的提醒可能与 Windows 差一小时；登记为声明的差异 |
| b | 代码页只对 GB18030 和 UTF 的 BOM 路径做 .NET 精确表；big5、shift_jis、euc-kr、euc-jp、单字节页交给 Foundation | 约 2 天 | 这些编码的旧网页或旧文本文件，重新读取后的 TextHash 可能与 Windows 不同；登记为声明的差异 |
| c | HtmlExtract/JsonLd 的逐字节一致推迟到 v1.1；v1 要求 Title、Site、Desc、Date 完全相同，Text 在小语料上按哈希相同 | v1 约 3 天（v1.1 补回） | v1 期间个别网页的正文提取可能与 Windows 略有不同，只影响重新读取后的摘要输入 |
| d（不建议） | W4c 录制器改为可选 | 约 2 天 | 桌面编排少了唯一的真实使用证据（§5.10） |

采用 a、b、c：约 106 天工作 + 15% 预留 ≈ **122 天**（关键路径约 117 天）。a、b 减少的是 P1 和 P4 的工作，c 减少的是 P5，所以要在 P1 开始前决定（§12 第 11 条）。

---

## 11. 风险与对策

| 风险 | 对策 | 接受的残余风险 |
|---|---|---|
| 桌面层有状态的编排悄悄走样：撤销分组、编辑与 CLI 合并、撤销条定时器、提醒只响一次、读取竞争。Desk.cs 没有整体抽取，这些规则不全由基准生成 | 决策函数抽进 Rules.cs 并有向量；台账哈希的转移表加每行的 FakeHost 场景；影子期间录制真实轨迹（含完整正文和起始快照，只留在本机）并在 Swift 上重放，P9 前每行至少被一条真实轨迹命中，罕见的行按清单手工触发，仍无法命中的由你签字豁免；Desk.cs 一改，对应行就过期。轨迹显示反复走样时，再考虑整体抽取 DeskCore | 转移表是人工转写的；真实轨迹没覆盖到的罕见路径仍可能不同 |
| 抽取 Rules.cs 和 LayoutPass 让每天在用的 Windows 版出问题 | 先录特征再移动；每个源文件一个提交；只有 LayoutPass 做新旧并行的影子，10 天零差异后才切换；每个提交重现 B0；OracleWpf 覆盖卡片，ui-snap 覆盖辅助界面；每个提交小而可回退 | 屏幕外渲染看不到的交互问题（拖动手感、焦点）只能靠你日常使用发现 |
| DotNetCompat 对 .NET 语义的重现不完整：正则方言、大小写、JSON 转义、双精度文本、时区换算 | 每个辅助函数都由 .NET 输出的表驱动，并有自己的向量；正则有清单、模式哈希和匹配范围向量；lint 禁止同名 API；三个平台（不同的 ICU 和 Foundation）都跑；覆盖率门槛和实时差分模糊 | 语料和覆盖率之外的输入 |
| Mac 写的 JSON 让 Windows 出错：`/` 被转义导致 Purge 删图、camelCase 键导致恢复出空卡片、整数字段写成浮点数导致读取抛异常 | 一个与 System.Text.Json 逐字节相同的 StjWriter，由操作脚本转储强制；双向交叉恢复和交叉 Purge 测试；写入前校验；lint 禁止 JSONEncoder；W3f 让 Windows 隔离坏行 | 若不采纳 W3f，一个漏网的写入缺陷仍可能让 Windows 启动失败（用 `pincc doctor` 定位后手工修复） |
| 跨平台数据格式的决定被否决，或者在决定之前两端就开始共用数据 | W3 在 P3 定下，在 Swift 写你的真实数据之前发布；被否决时改为在 DATA.md 里记录的“照搬”；设置分作用域不需要改 Windows | 选“照搬”时，Mac 建的文件卡片在 Windows 上显示为笔记 |
| 运行中的 WAL 数据库被放进云同步或网络文件夹，或者复制时漏了 `-wal` | Mac 启动时检查位置（statfs、CloudStorage、Mobile Documents、isUbiquitousItem）并警告；`export`/`import` 是支持的搬迁方式；反例测试；DATA-MOVE.md | Windows 端的位置检查是可选的；你手工把数据目录放进同步文件夹的情况无法完全阻止 |
| 在 GUI 运行时导入，内存里的撤销栈和编辑会话指向被替换的数据 | 两端都按数据目录判断 GUI 是否在运行并拒绝导入（Windows 由 W5 的 GUI 建按目录命名的互斥量，Mac 用 flock）；判断经接缝注入，录制和交替读写从不碰真实的互斥量；交接检查防止覆盖本机还没导出的修改 | 不支持在 GUI 运行时导入；W5 之前的旧版 GUI 没有按目录的互斥量，所以要先发布 W5 再用 import |
| macOS 27 的 AppKit 回归或输入法问题（拼音 / 搜狗候选、Esc 交还、选择器焦点、无边框拖动、状态栏图标） | 两个系统版本上的 P0 闸门；`.nonactivatingPanel` 只在 init 设置；`needsPanelToBecomeKey` 或 sendEvent 里 makeKey；选择器默认子面板（以 A3 为准）；跟踪循环里的显式拖动；经 button 的状态栏图标、不看 currentEvent 的类型；编辑器的 windowLevel；焦点方案 B；预留阶段 | macOS 27 小版本可能带来新的变化 |
| Intel Mac 的工具链上限（最多 Xcode 26.x / Swift 6.3，也可能更旧），或者跑不了 Sonoma | P0 第一天确定型号；tools-version 和语言特性按它钉住；部署目标 14，按 §9.1 退到 13 或 12，新接口加 `#available`；通用构建在 Apple 芯片 Mac 上完成、在 Intel Mac 上原生验证；Intel 只能运行时由 `pincc-selftest` 在它自己的系统库上跑全部向量，Rosetta 只查编译问题 | macOS 11 或更早的 Intel Mac 不在支持范围内；12 上的 AppKit 行为要单独重测；Rosetta 的完整支持只到 macOS 27 |
| 向量、样例和轨迹让仓库或 Windows 构建变慢 | 不压缩的 JSONL，每族约 5k 行；deep 语料只提交种子和哈希；随机脚本只提交每步哈希；build.cmd 只加有上限的冒烟检查，从不因向量过期失败；仓库体积预算 | — |
| 盲目重新生成向量，把 C# 的回退变成规范 | `--expect <族>`，其他族变化就失败；语义摘要写进提交说明；文本向量可以直接 diff | 审阅者仍可能看漏 |
| 长期的双倍维护：每次逻辑改动约 1.5–2 倍 | 台账和一条命令重新生成，把 Swift 跟进变成机械工作；Windows 从不被卡住；表只有一份；Swift 跟进可以批量做 | 这是 Swift-only 路线本身的代价 |
| 字体度量不同：自适应高度和溢出可能差一行 | 只让原始文字高度有容差，后处理按精确函数比较；敏感性分析列出会翻转的场景；固定大小的卡片必须完全相同 | 被列出的场景在两端可能出现不同的胶囊或 +N |
| Windows 与 IANA 的时区数据在历史日期上不一致；Swift 的 String 不能表示孤立代理项 | 向量只取两套数据一致的近代日期；两端都经 SQLite 的 UTF-16 接口读写文本；W2f 让 Fetch 的两处截断不再制造孤立代理项，CLI 的截断照搬，只影响显示的截断登记在 §1.4；差异登记在 DATA.md | 历史日期和孤立代理项的行为登记为差异 |
| 自动检查碰到你的实例或真实数据（WSLENV 没传过去时，PinCC.exe 走 GUI 路径并发“显示全部”信号，或打开真实数据目录） | 卡片的屏幕外检查用 OracleWpf（没有互斥量和信号路径）；ui-snap.sh 先确认环境变量到达、否则拒绝运行，数据目录用临时目录；测试框架断言 DefaultHome 不是真实目录；从不调用 `.probe/` | — |
| 通过 ssh 签名要解锁钥匙串，密码可能泄漏；私钥访问要弹窗授权，ssh 里弹不出来 | 专用构建钥匙串，只放自签名身份，密码存在 Mac 上的 0600 文件里；P0 在 Mac 前设好分区列表和搜索列表，S2 必须经 ssh 无弹窗通过（§9.6） | — |
| 从 WSL 调 dotnet.exe 挂起（互操作管道） | G1：输出写文件、不接提前关闭的读取端、每次调用有超时、100 次启动的压力测试 | — |

---

## 12. 待决定事项（附建议默认值）

| # | 问题 | 建议 |
|---|---|---|
| 1 | Intel Mac 的型号和 macOS 版本？ | 请告诉我。它决定 `swift-tools-version`（6.0、6.2 或 6.3）、WSL 的 Swift 版本、部署目标（14、13 或 12；macOS 11 或更早不能运行 Xcode 27 构建的程序），以及 Intel Mac 是编译测试还是只运行 `pincc-selftest`（§9.1）。P0 第一天需要 |
| 2 | 是否同意 W2 的确定性修正（§3.5.1）？对你的 zh-CN 环境输出不变，只在可忽略字符、完全相同的排序平局、全角数字列表、切断代理对等边界情况下变化；每项都有真实数据普查 | 同意全部；j（按结果码判断“忙”）可选 |
| 3 | 是否同意 W3 的数据格式修正（§3.5.2）？a 路径语法（Mac 路径只认 `/Users/`、`/Volumes/` 等固定前缀）；b 异机路径的唯一出入口；c Purge 扩展名修正；d 异机卡片的位置不被平铺和缩放改写；e `data.features`；f Windows 逐行隔离坏数据 | 全部同意；a 用固定前缀，不用“至少两段”：CLI 帮助让 LLM 工具从 WSL 调用 PinCC，钉卡脚本在路径不存在时会原样传入 `/home/…`、`/mnt/…`，按“至少两段”这些今天的笔记会在 Windows 上变成文件卡片，而真实数据普查只看已有卡片、发现不了以后的输入 |
| 4 | 哪些设置两端共享，哪些各自一份？ | 共享：color.mode、text.size、tile.gap、tile.corner、hub.corner、sound、retention.days、llm.auto、llm.prompt；各自一份：热键、LLM 工具配置、开机/登录启动、按屏幕置顶。tile.corner 和 hub.corner 要不要改为每台电脑各自一份，由你决定 |
| 5 | L2 做到哪一步？ | 现在只做导出导入（L2a，约 7 天，含交接检查和按数据目录的 GUI 互斥量）；合并导入（约 8–10 天，含表结构 v2）或经同步文件夹的持续同步（约再加 12–15 天，需要两端同时发布表结构 v2）以后再定，届时再选用哪个同步文件夹 |
| 6 | 日常用的 Windows 版能否运行约 10 天的 LayoutPass 影子模式（不一致写进单独的 shadow.log，行为不变）？影子期间能否打开 PINCC_TRACE 录制器？它记完整正文，开始时另存一份数据库起始快照；两者只存在你的 Windows 电脑上（`%LOCALAPPDATA%\PinCC-trace` 和 artifacts/trace/），从不提交，也不拷到 Mac；提交进仓库的派生轨迹把正文换成哈希。由你用 `setx` 打开和关闭，按天分文件，总量上限 50 MB。P9 开始前约 30 分钟按清单手工触发罕见的转移，仍无法命中的行由你签字豁免。切换之后的 W4d（按 Id 排序）是否同意？ | 都同意。录制器是桌面编排唯一的真实使用证据 |
| 7 | Mac 的命令名用 `pincc`？做法是 W2h 把 Cli.cs 里作为命令名出现的“PinCC”改为常量 `Cli.Cmd`（Windows 值不变），比较时只替换这些位置，产品名不动。Windows 是否也把图片行改成 `![](<路径>)`（上一版 §15 第 7 条）？ | 用 `pincc`；图片行两端都改（W2l，P2 先改 C#，CLI 录制按新格式录），路径含空格时现在的写法会断 |
| 8 | CLI 的 `--json` 是否要求与 Windows 逐字节相同（规范化之后）？ | 要求。数据库本来就需要逐字节相同的 JSON 写入器，CLI 顺带做到 |
| 9 | 远程访问：Apple 芯片 Mac 能否开启“远程登录”（ssh）并保持图形界面登录，供 `tools/mac.sh` 构建、测试和屏幕外验证？Intel Mac 同样？ | 两台都开启 |
| 10 | 沿用上一版 §15 的默认值？菜单栏图标默认显示；Mac 默认热键 ⌃⌥⌘N（有冲突就不设默认）；登录启动由你选择开启；卡片出现在所有桌面空间；两端各用系统字体；只在自己的两台 Mac 上用，稳定的自签名身份 | 沿用 |
| 11 | §10.3 的裁剪 a、b、c 是否采用？P1 开始前需要答复 | 如果你不在有夏令时的地区用 Mac，采用 a、b、c（约 122 天，关键路径约 117 天）；d 不建议 |
| 12 | 带 EXIF 方向标记的照片：两端都按 WPF 的方式（不旋转）显示，还是两端都改为按方向旋转（需要 Windows 改动）？ | v1 两端都不旋转，保持一致；HEIC 转码时把方向写进像素，所以从 iPhone 来的照片方向正确 |
| 13 | 深色模式（上一版 §15 第 10 条） | Mac 的界面外框跟随系统深色，卡片保持浅色纸张感；Windows 是否也加深色外框是另一项工作 |
| 14 | 编辑器里回车插入 CRLF（CardWindow.xaml:20），而当前行只按 `\n` 分割，所以以 CRLF 结尾的行按回车不续列表（CardWindow.xaml.cs:762-775）；经 CLI、剪贴板写入的 CRLF 正文整篇都这样。是在 C# 里修（W2m：编辑时按 LF 计算当前行），还是 Mac 照搬（Mac 回车也插入 CRLF）？P2 前需要答复，P7 依赖它 | 在 C# 里修，随 P2 一起发布；Mac 回车插入 LF，§1.4 的行尾差异只留在编辑中的缓冲区 |

---

## 附录 A：文件映射 C# → Swift

| C# 文件 | Swift 去向 | 类型 | 要点 |
|---|---|---|---|
| `src/Program.cs` | `PinCCApp/main.swift`；`PinCCCli` | 各平台自己实现 + 规则移植 | CLI 路由规则（Program.cs:17）和 RunCli 的兜底（37-66）移植；互斥量（含 W5 按数据目录命名的那个）、Event、控制台附加是 Windows 专有，Mac 用 `.instance` 的 flock |
| `src/Core/Db.cs` | `PinCCCore/Db.swift` + `CSQLite` | 移植 | UTF-16 文本接口；rc 分类 |
| `src/Core/Store.cs` | `Store+Open/Write/History/Files/Settings.swift`、`Home.swift` | 移植 | 建表语句由表生成；`data.features`；带类型的设置 |
| `src/Core/Card.cs` | `Card.swift`、`CardMeta.swift`、`Card+Source/Derived/Hash/Color.swift` | 移植 | StjWriter；PathRx 扫描器；ForeignPath |
| `src/Core/When.cs` | `When.swift` + `DotNetCompat/LocalDateTime`、`NetTimeZone` | 移植 | 歧义标记；.NET 时区规则 |
| `src/Core/Markdown.cs` | `Markdown.swift` | 移植 | UTF-16；扫描器 |
| `src/Core/Cli.cs` | `PinCCCli` | 移植 | 错误枚举；HelpCommon + 平台片段；export/import |
| `src/Core/Fetch.cs` | `TextDecode`、`HtmlExtract`、`JsonLd`、`UrlGuard`、`FileFacts`、`ZipReader`、`WebRoute`（可移植）；`Web`、`WebKitRenderer`、`Dns`（Darwin） | 纯函数移植 + 平台实现 | HTTP 规则照搬；渲染器不同 |
| `src/Core/Llm.cs` | `Llm.swift`（纯）；`ToolHost.swift`（Darwin） | 纯函数移植 + 平台实现 | 只读写 `mac.llm.*` |
| `src/Core/Proc.cs` | `Proc.swift` | 平台实现，契约相同 | posix_spawn + 进程组 |
| `src/UI/Layout.cs`、`Native.cs:7-35` | `Layout.swift`、`Geometry.swift` | 移植 | 回绕整数运算 |
| `src/UI/Desk.cs` | `LayoutPass.swift`、`Rules.swift`（经 W4 抽出的部分）；`DeskCore.swift`（按转移表）；Win32 部分无对应 | 移植 + 按转移表编写 | 按 Id 有序 |
| `src/UI/CardWindow.xaml(.cs)` | `CardPresenter`、`EditorOps`、`Rules`；`CardPanel`、`CardContentView`、`EditorTextView` | 规则移植 + 视图自写 | 视图只画 CardScene |
| `src/UI/MarkdownView.cs` | `MdRender.swift`；`MdAttributed.swift` | 规则移植 + 视图自写 | — |
| `src/UI/HubWindow.cs` | `HubModel`、`Layout.HubRect`；`HubPanel` | 规则移植 + 视图自写 | 角标优先级、吸附 |
| `src/UI/Flyout.cs` | `PickerModels`；选择器视图 | 规则移植 + 视图自写 | 默认用不激活的子面板；只有 P0 的 A3 实测 NSPopover 在 PinCC 不在前台时能打字、又不激活程序，才改用 NSPopover |
| `src/UI/ManageWindow.cs` | `LibraryModel`、`SettingsSchema`；SwiftUI 设置和卡片库 | 规则移植 + 视图自写 | 设置作用域 |
| `src/UI/Palette.cs` | `Generated/Palette.swift` | 表生成 | sRGB |
| `src/UI/ClipData.cs` | `ClipIngest.swift`；`MacPasteboard`、`DragIn`、`DragOut` | 规则移植 + 平台实现 | ImageExts 表生成；HEIC 转码 |
| `src/UI/Hotkeys.cs` | `KeyBindings`、表；`MacHotkeys`（Carbon） | 表生成 + 平台实现 | `mac.hk.*` |
| `src/UI/Toast.cs`、`AuxWindow.cs`、`Theme.xaml`、`App.cs` | `Toast`、AppDelegate | 视图自写 | App.Configure 只属于 WPF |
| `src/UI/Native.cs`（其余） | `MacScreens`、`FullscreenMonitor` 等 | 平台实现 | — |
| `tests/*` | — | 保留 | 新增 Vectors、Ledger、Oracle |

## 附录 B：Windows API → macOS API（Swift）

| 用途 | Windows | macOS（Swift） |
|---|---|---|
| SQLite | winsqlite3.dll，`*16` 接口 | 系统 libsqlite3，经 CSQLite 系统库目标，同样用 `*16` 接口 |
| 单实例 / “显示全部” | 命名 Mutex / EventWaitHandle；W5 另加按数据目录命名的 Mutex，供 import 判断 | 数据目录 `.instance` 的 flock + DistributedNotificationCenter（deliverImmediately）+ applicationShouldHandleReopen |
| 控制台 | GetStdHandle / AttachConsole / WriteConsoleW | 继承 stdout，始终 UTF-8 |
| 开机/登录启动 | HKCU\…\Run | SMAppService.mainApp |
| 加入 PATH | HKCU\Environment + WM_SETTINGCHANGE | 把 `Helpers/pincc` 链接到 `~/.local/bin` 或 `/usr/local/bin` |
| 显示器 | EnumDisplayMonitors / GetMonitorInfo / GetDpiForMonitor | NSScreen.screens / visibleFrame / backingScaleFactor / CGDisplayCreateUUIDFromDisplayID |
| 批量移动窗口 | DeferWindowPos + 跨 DPI 两步放置 | 一个 CATransaction 里批量 `setFrame(_:display: false)` |
| 置顶 / 置前 / 退后 | HWND_TOPMOST / SWP_NOACTIVATE / Behind | `.floating` / `orderFrontRegardless()` / `order(.below, relativeTo:)` |
| 不抢焦点的窗口 | WS_EX_NOACTIVATE / MA_NOACTIVATE | NSPanel `.nonactivatingPanel`（只在 init）+ `acceptsFirstMouse` |
| 不出现在切换列表 | WS_EX_TOOLWINDOW | LSUIElement + `.ignoresCycle` |
| 缩放边框 / 移动结束 | WM_NCHITTEST / WM_SIZING / WM_EXITSIZEMOVE | 显式跟踪循环（不用 `performDrag(with:)`）+ Rules.HitTest / KeepAspect；mouseUp |
| 所属弹窗保持编辑 | WM_ACTIVATE + GW_OWNER | windowDidResignKey + 检查新的 keyWindow 是否是自己的子面板 |
| 系统事件 | WM_DISPLAYCHANGE / WM_TIMECHANGE / WM_POWERBROADCAST | didChangeScreenParameters / NSSystemClockDidChange / NSSystemTimeZoneDidChange / NSWorkspace didWake |
| 全局热键 | RegisterHotKey（1409） | Carbon RegisterEventHotKey（−9878），`@convention(c)` 回调 |
| 录入快捷键 | WH_KEYBOARD_LL | 第一响应者视图 + 本地事件监听 + CopySymbolicHotKeys |
| 全屏检测 | SHQueryUserNotificationState + 前台窗口 | 桌面空间 + CGWindowListCopyWindowInfo 窗口边界 |
| 剪贴板 | IDataObject / CF_HTML / DIB | NSPasteboard（fileURL、public.url(-name)、org.chromium.source-url、webarchive、png/tiff/heic），accessBehavior |
| 拖出文件 | DragDrop.DoDragDrop | NSDraggingSession + 文件 URL |
| 打开 / 定位 | ShellExecute / explorer /select | NSWorkspace.open / activateFileViewerSelecting |
| 提示音 | PlaySound “Notification.Reminder” | AudioServicesPlayAlertSound（Glass.aiff）/ NSSound.beep |
| 图片尺寸和解码 | BitmapDecoder / DecodePixelWidth | CGImageSource 属性 / 不带方向变换的解码 |
| 离屏截图 | RenderTargetBitmap | 视图 `displayIfNeeded()` 之后用根图层的 `render(in:)` 画进 CGContext；`cacheDisplay(in:to:)` 只作对照（它走视图的绘制路径，直接设在图层上的描边、圆角、背景和手加的子图层可能不进位图）。`render(in:)` 不画 mask 和 filters，所以溢出淡出用叠在上面的普通渐变图层，不用 mask；材质背景在 verify 模式里换成等色的纯色 |
| HTTP | HttpClient | URLSession（临时会话 + 重定向代理） |
| 无头渲染 | msedge --headless=new --dump-dom | Chrome/Edge --dump-dom 或屏幕外 WKWebView（nonPersistent） |
| 子进程树 | Job 对象 KILL_ON_JOB_CLOSE | posix_spawn + POSIX_SPAWN_SETPGROUP + killpg |
| 工具的 PATH | 注册表 Path + WSL 登录 shell | 用户登录 shell 的环境（zsh -l -i） |
| SHA-1 | System.Security.Cryptography | 自己写的 SHA-1（不用 CryptoKit） |
| ZIP | System.IO.Compression | 自己写的中央目录解析 + zlib 原始 inflate（CZlib） |

## 附录 C：已查证的事实（2026-09）

“状态”一列：已查证 = 有文档或源码依据；部分 = 依据只覆盖一部分；未查证 = 需要 P0 或后续阶段实测。

### C.1 工具链

| 事实 | 状态 | 来源 |
|---|---|---|
| Swift 6.4.0 于 2026-09-14 发布，随 Xcode 27.0；官方支持 Ubuntu 24.04 x86_64/aarch64；上一版是 6.3.3（Xcode 26.6） | 已查证 | swift.org/blog/swift-6.4-released；swift.org/api/v1/install/releases.json |
| Linux 上用 swiftly（当前 1.2.0，2026-09-22 发布；1.1.3、1.1.4 分别在 2026-06-17、09-15）安装；WSL 缺 libsqlite3-dev、zlib1g-dev，运行库 libsqlite3 3.45.1、zlib 1.3、libicu 74、tzdata 2026c 已有 | 已查证 | swift.org/install/linux；api.github.com/repos/swiftlang/swiftly/releases；本机 dpkg 检查 |
| Linux 上 `swift test` 同时运行 Swift Testing 和 XCTest，Swift Testing 随官方工具链提供 | 已查证 | swift-testing README |
| Linux 的 Foundation 是 swift-corelibs-foundation，兼容性“尽力而为” | 已查证 | swift-foundation README |
| `import SQLite3` 只在 Darwin 可用；可移植做法是 `.systemLibrary` 目标（GRDB 在所有平台都这样做） | 已查证 | GRDB.swift Package.swift、GRDBSQLite/module.modulemap |
| swift-foundation（Linux 工具链，以及较新 Apple 系统自带的 Foundation）的 JSONEncoder 默认把 `/` 写成 `\/`，键顺序随 Dictionary 随机，控制字符用小写 `\u00xx`。Mac 上的实际行为由运行时的系统 Foundation 决定，部署下限（14/13/12）的系统未逐项核对；本计划 lint 禁用 JSONEncoder，不依赖这些行为 | 部分 | swift-foundation JSONWriter.swift:129-147、JSONEncoder.swift、README（Apple 平台用系统自带的 Foundation） |
| System.Text.Json 默认转义所有非 ASCII 为大写 `\uXXXX`，不转义 `/`；UnsafeRelaxedJsonEscaping 仍转义阻止列表（如 U+3000）和代理对；属性名 PascalCase，读取时区分大小写 | 已查证 | learn.microsoft.com system-text-json character-encoding |
| swift-foundation 的 `Calendar.date(from:)` 对重复的本地时间取第一次（夏令时），.NET `TimeZoneInfo.GetUtcOffset` 把歧义时间当标准时间。Mac 上同样取决于系统自带的 Foundation 版本；本计划 lint 禁用 Calendar，不依赖这一行为 | 部分 | swift-foundation Calendar_Gregorian.swift:1667-1669；learn.microsoft.com TimeZoneInfo.GetUtcOffset |
| .NET 的 Local 类型 DateTime 带歧义夏令时标记，`new DateTimeOffset(local)` 使用它 | 部分（来自评审对 When.cs:9-11、33、81 的分析） | 评审 1 第 5 条；P1 用向量确认 |
| 没有 GB18030 的 String.Encoding 常量；原始值 0x80000632 在 Linux 上走 CF + ICU；ICU 73.2+ 按 GB18030-2022 改了部分映射 | 部分 | swift-foundation String+IO.swift；swift-corelibs-foundation CFStringEncodingDatabase.c；icu.unicode.org/download/73。本计划改用 .NET 导出的表，不依赖这条路径 |
| Compression 框架只在 Apple 平台；系统 zlib 在两个平台都可用 | 已查证 | developer.apple.com/documentation/compression |
| CryptoKit 只在 Apple 平台 | 已查证 | github.com/apple/swift-crypto |
| WAL 要求所有连接在同一主机，不能用于网络文件系统；数据库和 WAL 文件与字节序无关，`-shm` 是本机字节序的临时文件 | 已查证 | sqlite.org/wal.html、fileformat2.html |
| Swift 6.4 起 SwiftPM 默认构建系统是 Swift Build（单架构也是）；6.3.x 默认 native，多于一个 `--arch` 时改用 `.xcode`（XCBuild，需要完整 Xcode） | 已查证；只装 CLT 时能否生成通用二进制未查证 | swift-package-manager swift-6.4.0-RELEASE Options.swift:557、564-569；swift-6.3.3-RELEASE Options.swift:549、561；swift.org 6.4 发布博客 |
| swift.org 的 6.4.0 macOS 工具链是通用包，没有 Xcode 时“SwiftPM 的功能可能受限” | 已查证 | swift.org/install/macos/package_installer |
| 原生构建系统的 `Bundle.module` 在 `Bundle.main.bundleURL` 下找资源包；手工组装的 .app 里那是 .app 根目录 | 部分：Swift 6.4 默认用的 Swift Build 的行为未查证，P0 S2 确认；本计划不声明资源，不依赖它 | SwiftModuleBuildDescription.swift:425-437 |
| actool 只随 Xcode 提供 | 已查证 | port-killer PR #117 |
| notarytool 自 Xcode 13 起支持；是否随 CLT 提供未说明；自用不需要公证；macOS 27 上本地构建、ad-hoc 签名、无隔离属性的程序直接启动，SMAppService 能注册 | 部分 | Apple“Customizing the notarization workflow”；KlangLadder #7（2026-09-17） |
| Xcode 27 只在 Apple 芯片、macOS 26.6+ 上运行；部署 macOS 12–27；可向 macOS 12+ 部署通用程序；部署目标 ≥27 时才去掉 x86_64 | 已查证 | Xcode 27 发行说明（162138432、161837535）；developer.apple.com/xcode/system-requirements |
| 各 Intel 系统能用的最新 Xcode：Tahoe 26.2+ → 26.4.1–26.6（Swift 6.3）；15.6–26.x → 26.0–26.3（6.2.x）；15.3+ → 16.4（6.1）；15.2 → 16.3（6.1）；14.5–15.1 → 16.0–16.2（6.0）；14.0–14.4 → 15.4（5.10）；13.5+ → 15.2（5.9）；13.0–13.4 → 14.3.1（5.8.1）；12.5+ → 14.2（5.7）。Tahoe 只支持 4 个 Intel 型号；Xcode 27 构建的程序最低能运行在 macOS 12 | 已查证 | developer.apple.com/xcode/system-requirements（2026-09-24 读取）；Wikipedia macOS Tahoe |
| API 下限：SMAppService 13、`NSApplication.yieldActivation(to:)` 14.0、NSPasteboard.AccessBehavior 15.4、Synchronization.Mutex 15.0、InlineArray 26.0 | 已查证 | developer.apple.com 文档元数据 |
| Swift `rounded()` 默认远离零；.NET `Math.Round` 默认取偶，`Math.Round(x, d)` 在 \|x\| < 1e16 时是 `Round(x·10^d, ToEven)/10^d` | 已查证 | swift FloatingPoint.swift:1661-1666；dotnet/runtime Math.cs:1352-1408 |
| Swift 整数溢出会陷阱；C# 默认不检查；.NET 9 起 x64 上双精度转整数饱和 | 已查证 | TSPL AdvancedOperators；learn.microsoft.com .NET 9 fp-to-integer |
| Swift 的 `String.count` 数字素簇，`==` 按规范等价，`"\r\n"` 是一个 Character | 已查证 | TSPL StringsAndCharacters；UAX #29 GB3 |
| Swift 的 `sort` 稳定；C# `List.Sort` 不稳定，LINQ `OrderBy` 稳定；Swift Hasher 每个进程随机 | 已查证 | Apple sort() 文档；Hasher 文档；learn.microsoft.com List.Sort |
| .NET、ICU、Swift Regex 三种正则方言的差异（`\w`、`\d`、`\s`、`.`、按 UTF-16 判断、ICU 回溯上限 8 MB） | 已查证 | learn.microsoft.com 字符类；ICU 正则用户指南；Swift matchingSemantics |
| Swift 双精度的 `description` 保留 `.0`、小写指数；.NET 用最短往返形式、大写指数 | 部分：.NET 一侧来自常识，P1 用向量确认 | swift-foundation JSONEncoder.swift:759-761 |
| Linux 上 corelibs 的 Process 总是用 POSIX_SPAWN_SETPGROUP；Darwin 是否如此未查证 | 部分 | swift-corelibs-foundation Process.swift:965。本计划直接用 posix_spawn |

### C.2 AppKit 与 macOS 27

| 事实 | 状态 | 来源 |
|---|---|---|
| 重写 `styleMask` getter 伪造 `.nonactivatingPanel` 的 Electron 面板在 macOS 27 上点击会激活程序 | 已查证 | electron/electron #53889、electron_ns_panel.mm |
| 创建后添加样式的 Tauri 面板有类似报告，但维护者在 macOS 27 上实测 2.1.0 的面板“成为 key、别的程序仍在前台”，归因于整组替换样式，报告者说改了仍不行，结论未定；AppKit 会拒绝某些运行中的样式替换；init 时设置的面板没有受影响的报告；不能成为 key 的 `.nonactivatingPanel` 在 macOS 27 上保持键盘焦点在原程序 | 部分：PinCC 的“能成为 key、在别的程序前台时点击并打字”未测 | tauri-nspanel #123（维护者 2026-09-19 的评论）、67212d83；Doll PR #75 |
| macOS 27 上状态项的 action 仍会发出，但此时 `NSApp.currentEvent` 是 AppKitDefined(13) 或 MouseMoved(5)，不是鼠标按下；常驻菜单吞掉左键；对非鼠标事件读 clickCount 抛异常 | 部分（多个独立报告；“target/action 不再触发”的最初说法已在原帖里被实测推翻） | Wails #6147（2026-09-21 的评论实测）、tray-icon #355、ai-usagebar #237、GoldenDict #3080；Apple NSEvent.clickCount 文档 |
| 锚在状态项上的 NSPopover 显示时拿走键盘焦点；`isMovableByWindowBackground` 不再移动无边框面板 | 部分（各自单一来源） | Doll #75；meeting-reminder #39 |
| 输入法候选窗按 `NSTextInputClient.windowLevel` 定位，只在它高于 NSFloatingWindowLevel 时生效 | 已查证 | Apple NSTextInputClient windowLevel 文档；tauri-nspanel 8ed8f642 |
| Carbon RegisterEventHotKey、SMAppService.mainApp 在 macOS 27 上可用 | 已查证 | PortKiller PR #117、OpenInTerminal PR #281、KlangLadder |
| 事件监听 token 弱引用会在某些 macOS 26/27 版本上被释放 | 部分 | KeyboardShortcuts #241 |
| NSPasteboard.AccessBehavior：默认“程序读取时询问”，用户发起且与粘贴相关的读取不提示；detect 接口不触发提示 | 部分：哪一版开始实际提示未查证 | Apple AccessBehavior 文档 |
| 非激活面板的标志必须在创建时设定；NSPanel 的 hidesOnDeactivate 默认 true；无边框窗口默认不能成为 key | 已查证 | Apple NSPanel / NSWindow 文档（上一版附录 C） |
| `.activeAlways` 跟踪区域和 acceptsFirstMouse 让后台时悬停和第一次点击有效；`.activeAlways` 不支持 `.cursorUpdate` | 已查证 | Apple NSTrackingArea / NSView 文档；Developer Forums 738051 |
| `becomesKeyOnlyIfNeeded = true` 时只有点到需要键盘的视图面板才成为 key；不激活面板只在被点中的视图 `needsPanelToBecomeKey` 返回 true 时才成为 key，NSView 默认返回 false | 已查证 | Apple NSPanel.becomesKeyOnlyIfNeeded、NSView.needsPanelToBecomeKey 文档 |
| `resignKey()` 不应直接调用；Spotlight 类面板靠关闭面板交还焦点 | 已查证 | Apple NSWindow 文档 |
| 程序不在前台时分布式通知暂停投递，除非 deliverImmediately | 已查证 | Apple Notification Programming Topics |
| `NSSound.play()` 走主音量，`AudioServicesPlayAlertSound` 走提醒音量 | 已查证 | Rogue Amoeba“Playing Sound Effects the Right Way” |
| `open -W` 等待程序退出，`--env` 设置环境变量 | 已查证 | open(1) |
| ssh 里签名要先解锁钥匙串，否则 errSecInternalComponent；还要一次性设好私钥的分区列表（`set-key-partition-list`）并把钥匙串加进搜索列表，否则同样报错或等待弹窗 | 已查证 | Developer Forums 768354、712005；GitHub Docs“Signing Xcode applications”的工作流步骤 |
| ad-hoc 签名的指定要求就是 cdhash，每次构建都变，TCC 授权随之失效 | 已查证 | YARG #1695 等 |
| Carbon 热键不需要权限；macOS 15.0 拒绝只用 Option 的组合，15.2 放宽 | 已查证 | KeyboardShortcuts；Developer Forums 763878 |
| CGEventTap 需要“输入监控”，全局事件监听需要“辅助功能” | 已查证 | Developer Forums 707680 |
| agent 程序没有主菜单时 ⌘C/⌘V/⌘Z 在文本框里无效 | 已查证 | 多个开源 agent 程序的 issue |
| App Nap 降低后台定时器频率，作用于整个进程；`.strict` 定时器让系统尽量遵守指定的 leeway；`beginActivity` 可以阻止小睡；didWake 必须在 NSWorkspace 的通知中心监听 | 已查证 | Apple Energy Guide、DispatchSource.TimerFlags.strict、ProcessInfo.beginActivity(options:reason:)、NSWorkspace 文档 |
| 从访达启动的程序 PATH 只有 `/usr/bin:/bin:/usr/sbin:/sbin`；Apple 芯片上 Homebrew 在 /opt/homebrew | 已查证 | Homebrew FAQ |
| App Sandbox 会被子进程继承；TCC 按负责的父程序计算子进程的文件访问；拖入属于隐含同意 | 已查证 | App Review Guidelines；Eclectic Light“Permissions, privacy and TCC” |
| Apple 的包结构：辅助工具放 Contents/Helpers 或 Contents/MacOS，Resources 只放非代码资源 | 已查证 | Apple“Placing content in a bundle” |
| macOS 15 取消了“右键 › 打开”绕过 Gatekeeper | 已查证 | mjtsai.com 2024-07-05 |
| ⌃+滚动是“缩放”辅助功能的默认手势 | 已查证 | AbilityNet |
| Liquid Glass 不用在内容层 | 已查证 | Apple HIG Materials |
| 系统没有公开接口得知其他程序是否全屏或专注模式 | 已查证 | Developer Forums 792917、813033 |
| Dropbox、OneDrive、Google Drive 在 `~/Library/CloudStorage` 下，statfs 看不出；iCloud“桌面与文稿”同步 `~/Documents`、`~/Desktop` | 部分（评审提出，P10 用单元测试确认检查方法） | 评审 1 第 27 条 |
| WebKit 只在视图有窗口、窗口 `isVisible` 且没被遮挡时把页面当作可见；从不显示或完全在屏幕外的窗口里，页面是 hidden，定时器被节流，requestAnimationFrame 停止 | 已查证 | WebKit PageClientImplMac.mm:206-226（`isViewVisible`） |
| WKWebView 在 macOS 27 上没有找到与屏幕外渲染有关的回归 | 未查证（“没找到证据”不等于安全） | window-vibrancy #229 等 |
| `performDrag(with:)` 立即返回，mouseUp 可能收不到 | 已查证 | Apple NSWindow.performDrag(with:) 文档 |
| `cacheDisplay(in:to:)` 在视图不绘制内容的地方是透明的；`CALayer.render(in:)` 不画 mask、filters 和 3D 变换 | 部分：直接设在图层上的描边和圆角是否进 cacheDisplay 的位图没有 Apple 原文，P0 S5 实测 | Apple cacheDisplay(in:to:)、CALayer.render(in:) 文档 |
| ditto、unzip 在 macOS 上存在 | 未查证（来自常识；本计划不用） | — |

### C.3 本仓库

| 事实 | 来源 |
|---|---|
| 数据库用 `sqlite3_open_v2` 打开（UTF-8 存储），文本用 `bind_text16`/`column_text16` 读写 | src/Core/Db.cs:26、119、142 |
| `user_version` 高于 1 时只读，低于 1 时迁移并写成 1；`SetSetting` 不受只读保护 | src/Core/Store.cs:60-63、71-79、325-331 |
| `SettingBool` 只把 `"1"` 当真，`SettingInt` 用 `int.TryParse` | src/Core/Store.cs:322-323 |
| Purge 用 `DateTime.UtcNow` 算文件时间下限，用 `img/([0-9a-f]{20}\.\w+)` 找引用 | src/Core/Store.cs:282、337 |
| `Card.Compare` 的提醒分支在完全相同时返回 0 | src/Core/Card.cs:206 |
| `Card.Src` 对 `file:///` 直接返回 `Uri.LocalPath`，不经 PathRx | src/Core/Card.cs:117 |
| PathRx 只认 `X:\` 和 `\\server\` | src/Core/Card.cs:224 |
| `llm.prompt` 在 `Trim()` 等于默认模板时删除，否则原样存储 | src/UI/ManageWindow.cs:180 |
| 设置页的选项集合 | src/UI/ManageWindow.cs:191-202 |
| 下载文档的 TextHash 是 `Card.Hash(绝对路径)` | src/UI/Desk.cs:1100 |
| PINCC_UI_SNAPSHOT 在拿互斥量之前分流；其余无参数启动会找到互斥量并给已运行的实例发“显示全部” | src/Program.cs:19-29 |
| 互斥量名 `Local\PinCC.Instance`，事件名 `Local\PinCC.Show`；互斥量与 `PINCC_HOME` 无关，第二个实例拿不到它就发信号后退出 | src/Program.cs:10-11、24-29 |
| App.OnStartup 设置 CJK 语言并合并 Theme.xaml（pack URI 写死程序集名 PinCC） | src/UI/App.cs:18-21 |
| Screens 缓存 2 秒 | src/UI/Native.cs:40-52 |
| 47 个 `[GeneratedRegex]`、10 行内联 Regex 调用（按出现次数数是 11 处，Fetch.cs:468 一行里还有 `Regex.Escape`） | grep src/Core、src/UI |
| build.cmd 先跑测试再两次发布 | build.cmd:7-11 |
| 测试项目只编译 src/Core 和 UI 里的 Layout.cs、Native.cs | tests/PinCC.Tests.csproj:14-15 |
| `Desk.Start` 会写备份、显示卡片和 + 按钮、注册热键、响提示音、联网、启动子进程，第一次运行还会改 HKCU Run；`_layout` 计时器的处理函数只在 Start 里挂上 | src/UI/Desk.cs:97-130、112、184-191 |
| src/UI 里共 14 处 `DateTime.Now`；Native.cs:46、49 的 `DateTime.UtcNow` 是屏幕缓存计时 | grep src/UI |
| 仓库没有远端；上一版计划在提交 7e24435 | git |

## 附录 D：数据字典（初稿，提交后为 contracts/DATA.md）

### D.1 表

| 表.列 | 类型 | 默认 | 规则 |
|---|---|---|---|
| `cards.id` | INTEGER PK AUTOINCREMENT | — | 导入从不重新编号 |
| `cards.rev` | INTEGER | 0 | 每次写 +1；`WHERE rev = ?` |
| `cards.body` | TEXT | `''` | 原样；哈希按存储内容 |
| `cards.images` | TEXT | NULL | NULL 或 STJ 默认写法的 JSON 数组；`img/<sha1-20>.<ext>` |
| `cards.summary` | TEXT | NULL | 原样 |
| `cards.meta` | TEXT | NULL | CardMeta JSON（WhenWritingNull） |
| `cards.color` | INTEGER | NULL | NULL = 自动，0–8 |
| `cards.topmost`、`collapsed`、`free` | INTEGER | 0 | 0/1 |
| `cards.opacity` | REAL | 1 | `Math.Round(x, 1)` 取偶 |
| `cards.mon` | TEXT | NULL | 不透明显示器 id：Windows 设备接口路径或 `\\.\DISPLAYn`；Mac `mac:<UUID>` |
| `cards.x`、`y` | REAL | 0 | 相对工作区左上角的 DIP，不取整 |
| `cards.w` | REAL | 260 | 取偶舍入 |
| `cards.h` | REAL | NULL | NULL = 自适应 |
| `cards.remind_at`、`done_at`、`deleted_at` | INTEGER | NULL | UTC 毫秒 |
| `cards.created_at`、`updated_at` | INTEGER | — | UTC 毫秒；`updated_at` 单调 |
| `ops.id` | INTEGER PK AUTOINCREMENT | — | 清理后不复用 |
| `ops.grp` | INTEGER | — | 组号 = 组内第一条的 id |
| `ops.at` | INTEGER | — | UTC 毫秒 |
| `ops.card_id` | INTEGER | — | — |
| `ops.op` | TEXT | — | 只来自操作名表 |
| `ops.before` | TEXT | NULL | Card 快照（STJ 默认，21 键，写 null） |
| `ops.cols` | TEXT | NULL | 逗号分隔，首次出现顺序；新建和重新插入的记录写入时为 `''`，同一组随后对这张卡片的写入会把列并入（Store.cs:171-176），所以 before 为 NULL 的记录也可能有非空 cols；NULL 只出现在发布前的旧记录 |
| `settings.key`、`value` | TEXT | — | NULL 值 = 删除该行 |
| `PRAGMA user_version` | — | 1 | 更高 → 只读；更低（含负数）→ 迁移并写成 1（Store.cs:60-63） |
| 索引 | — | — | `ops_grp(grp)`、`ops_card(card_id, id)` |

建表语句以 Store.cs:36-52 的 `Schema` 常量为准（含第 51 行的 settings 表），逐字节导出到 `contracts/tables/schema.sql`。

### D.2 Card 快照的键（ops.before，声明顺序）

以 `Card.Columns` 和 Card 的属性声明为准，由生成器导出；共 21 个 PascalCase 键，按 Card.cs:16-36 的声明顺序：`Id`、`Rev`、`Body`、`Images`（数组，空时为 `[]`）、`Summary`、`Meta`（字符串）、`Color`、`Topmost`、`Opacity`、`Collapsed`、`Free`、`Mon`、`X`、`Y`、`W`、`H`、`RemindAt`、`DoneAt`、`DeletedAt`、`CreatedAt`、`UpdatedAt`。

### D.3 CardMeta 的键

`Src`、`Title`、`Site`、`Desc`、`Date`、`FType`、`Size`（整数）、`Mtime`（整数，UTC 毫秒）、`TextHash`、`SumHash`、`State`、`Error`（Card.cs:228-250）。null 不写。

| 键 | 允许的特殊值 |
|---|---|
| `Src` | 写入 meta 时卡片当时派生出的 Src，逐字；可以为 null（CLI 给还没读取的卡片写摘要，Cli.cs:165）；可以与当前 Src 不同（首行改变后、由网址变成的图片卡，Desk.cs:1144）。只用 `SrcInfo`（Card.cs:142）判断是否属于当前来源，校验不把不相等当错误 |
| `FType` | `文件夹`、大写扩展名（`ToUpperInvariant`），或空字符串 `""`（没有扩展名的文件，Fetch.cs:351）；链接卡片没有这个键 |
| `State` | `manual`、`blocked`、`missing`（不允许新增） |
| `SumHash` | 哈希或 `source-changed` |
| `Error` | 不透明的系统文本 |

运行时状态“在另一台电脑上”“需要授权”不写进 meta。

### D.4 操作名

`create` 新建、`edit` 编辑、`done` 完成、`delete` 删除、`restore` 恢复、`remind` 提醒、`summary` 摘要、`meta` 读取来源、`move` 移动、`resize` 调整大小、`view` 显示设置、`color` 颜色、`tile` 平铺（Cli.cs:308-313）。`import`、`sync` 推迟到 L2b 再定。

### D.5 设置键

| 键 | 作用域 | 取值 | 默认 | 敏感 |
|---|---|---|---|---|
| `color.mode` | 共享 | off / rotate / kind / tag | rotate | 否 |
| `text.size` | 共享 | 13 / 14 / 16 / 18（读取接受 12–20） | 14 | 否 |
| `tile.gap` | 共享 | 8 / 12 / 18（读取限制 4–40） | 12 | 否 |
| `tile.corner` | 共享（待定，§12 第 4 条） | tl / tr / bl / br | tr | 否 |
| `hub.corner` | 共享（待定） | tl / tr / bl / br | br | 否 |
| `sound` | 共享 | 1 / 0 | 1 | 否 |
| `retention.days` | 共享 | 7 / 30 / 90 / 365 | 30 | 否 |
| `llm.auto` | 共享 | 1 / 0 | 1 | 否 |
| `llm.prompt` | 共享 | 文本，等于默认模板时删除 | 无 | 否 |
| `data.features` | 共享 | 逗号分隔的特性 id | 无 | 否 |
| `backup.last` | 本机 | yyyyMMdd（不变区域公历）；不导出，导入时保留本机值 | 无 | 否 |
| `db.id` | 本机 | 首次导出时生成；不导出进 settings（写进 `bundle_meta.db_id`），导入不覆盖 | 无 | 否 |
| `local.handoff` | 本机 | 上一次 export 或 import 成功时本机 ops 的 `sqlite_sequence`（§4.4.1 的交接检查） | 无 | 否 |
| `hk.new`、`hk.paste`、`hk.min`、`hk.collapse`、`hk.tile` | Windows | Windows 组合键写法；`''` = 未绑定 | 见 Hotkeys | 否 |
| `autostart` | Windows | 1 / 0 | — | 否 |
| `llm.where` | Windows | windows / wsl | — | 否 |
| `llm.tool`、`llm.model`、`llm.distro`、`llm.detected` | Windows | — | — | 否 |
| `llm.custom`、`llm.env` | Windows | — | — | **是** |
| `top:<Windows 显示器 id>` | Windows | 1 / 0 | — | 否 |
| `mac.hk.*` | Mac | ⌃⌥⇧⌘ 写法；`''` = 未绑定 | ⌃⌥⌘N（新建，待定） | 否 |
| `mac.llm.tool`、`mac.llm.model`、`mac.llm.detected` | Mac | — | — | 否 |
| `mac.llm.custom`、`mac.llm.env` | Mac | — | — | **是** |
| `mac.autostart.asked` | Mac | 1 / 0 | — | 否 |
| `mac.statusitem` | Mac | 1 / 0（菜单栏图标） | 1 | 否 |
| `mac.welcomed` | Mac | 1 / 0（欢迎提示和剪贴板提示已显示过） | — | 否 |
| `top:mac:<UUID>` | Mac | 1 / 0 | — | 否 |

### D.6 文件夹

见 §4.1。`edge/`、`llm/`、`error.log`、`shadow.log`、`.instance` 是本机文件，不搬迁；Mac 只在数据目录里创建 `.instance`（GUI 持有 flock，内容是持有者的 bundle id），不创建其余几项。

### D.7 数据特性（data.features）

初始为空。每个以后的数据格式变化在这里登记一个 id、引入它的提交、两端各自开始支持的版本。

## 附录 E：评审意见的处理

E.1–E.4 是两份评审的 48 条，E.5 是第二轮核对的 92 条。E.1–E.4 保留当时的处理记录，其中被第二轮核对改动过的地方在括号里注明。“采纳”表示已改进设计；“部分采纳”说明取舍；“残余风险”表示接受并在 §11 记录。

### E.1 评审 1

| # | 意见 | 严重度 | 处理 | 位置 |
|---|---|---|---|---|
| 1 | GUI 运行时 `import --replace` 让内存里的撤销栈、编辑会话、窗口指向被替换的数据 | 高 | 采纳：两端在 GUI 运行时拒绝导入；交替读写用例 | §4.4.1 |
| 2 | `file:///` 绕过 PathRx，异机路径守卫不完整 | 高 | 采纳：ForeignPath 定义为“不是本机的绝对路径语法”，作用于最终 Src；向量含 `file:///tmp` 等 | §3.5.2 b |
| 3 | W2/W3 只用向量证明，看不到对你真实卡片的重新分类 | 高 | 采纳：真实数据普查，差异要你签字 | §5.14 |
| 4 | OracleWpf 记录的是默认值而不是实测值；屏幕缓存过期；语言和 Theme 没设置 | 中 | 采纳：Measure/Arrange 并推进调度器、固定屏幕缓存、App.Configure、哨兵检查 | §5.9、W1c |
| 5 | 固定时区的方式与生产路径不同（歧义标记、`zzz`） | 中 | 采纳：用反射固定本地时区，不设时区接缝；LocalDateTime 建模歧义标记 | §5.3、§6 第 16 条 |
| 6 | Mac 写共享设置的取值格式不受约束 | 中 | 采纳：取值语法、带类型的写入、守卫检查取值 | §4.2.4 |
| 7 | NSTextView 的自动替换 | 中 | 采纳：全部关闭，单元测试和 verify 场景 | §8.3 |
| 8 | 平铺、缩放、按屏幕置顶会改写异机卡片的位置 | 中 | 采纳：W3d（第二轮核对改为按“异机位置”判断，E.5） | §3.5.2 d |
| 9 | path.map 带来本机哈希和 meta 反复改写 | 中 | 采纳：path.map 移出 v1，以后只做只读映射 | §4.2.5 |
| 10 | 非法 Unicode 在两端解码路径不同 | 中 | 采纳：Swift 也用 UTF-16 接口；转储打印解码结果和哈希；W2f | §4.2.1、§5.5 |
| 11 | 语义格式变化没有版本标记 | 中 | 采纳：`data.features`（W3e） | §4.2.6 |
| 12 | 移动数据目录引入第二套解析规则 | 中 | 采纳：唯一的 `Home.resolve()` 和 lint；v1 不提供移动 | §4.1 |
| 13 | 向量可以被盲目重新生成，gzip 无法审阅 | 中 | 采纳：不压缩、`--expect`、语义摘要 | §5.3 |
| 14 | 台账扫描器会被字符串内容和未标记代码绕过 | 中 | 采纳：按词法扫描、变异自测、Swift 全行覆盖 | §5.11 |
| 15 | L2b 按行 `updated_at` 决定胜负是错的，操作记录会被清理 | 中 | 采纳：按列；水位线早于保留期时拒绝按列合并（第二轮核对发现 `ops.at` 不够，改为表结构 v2 的 `ops.last_at`，E.5） | §4.4.3 |
| 16 | NetDictionary 给人虚假的安心；转移表是人工转写的 | 中 | 采纳：W4d 按 Id 排序；影子期间录制器必须打开，每行都要被真实轨迹命中（第二轮核对加了手工触发和签字豁免，E.5） | §5.10 |
| 17 | 一行容差掩盖逻辑缺陷 | 低 | 采纳：只有原始高度有容差；敏感性分析 | §5.9 |
| 18 | 反射找不到内联正则 | 低 | 采纳：源码扫描门槛；W2g；运行时拼出的正则用扫描器 | §5.4 |
| 19 | 若干决定行为的表不在单一来源列表里 | 低 | 采纳：补充这些表；集合字面量扫描 | §5.1 |
| 20 | C# 整数回绕，Swift 陷阱 | 低 | 采纳：回绕运算的 lint 覆盖全部移植的整数运算；毒样例 | §5.13、§5.7 |
| 21 | EXIF 方向破坏“内容相同” | 低 | 采纳：转码时方向写进像素；显示时不旋转，与 WPF 相同 | §8.8、§8.12、§12 第 12 条 |
| 22 | Purge 直接读 `DateTime.UtcNow`，W1 漏了 | 低 | 采纳：Store.cs:282 进 W1a；禁用时钟的 grep 测试 | §3.5 W1a、W1d |
| 23 | 生产 exe 读 PINCC_NOW / PINCC_TZ 可能污染真实数据 | 低 | 采纳：Program.cs 不读；Swift 只在测试构建认 | §3.5、§5.8 |
| 24 | 向量文件头缺 ICU、winsqlite3、运行时版本 | 低 | 采纳 | §5.3 |
| 25 | 回车插入 CRLF 的编辑器行为；llm.prompt 规则转写错误 | 低 | 采纳：EditorOps 向量用 WPF 形状的文本；改正 prompt 规则并移入 Rules | §7、§4.2.4 |
| 26 | 帮助不是连续的平台段落；图片路径分隔符 | 低 | 采纳：命名平台片段；只在图片字段替换 `{SEP}` | §3.5.1 h、§4.2.8 |
| 27 | 位置检查漏掉 CloudStorage 和 iCloud 桌面与文稿 | 低 | 采纳 | §4.2.9 |
| 28 | 生成器和移植者有同样的盲区 | 低 | 采纳：覆盖率门槛和实时差分模糊 | §5.3 |

### E.2 评审 2

| # | 意见 | 严重度 | 处理 | 位置 |
|---|---|---|---|---|
| 1 | 88 天低估 25–35% | 高 | 采纳：重新估算为约 122 天（110–140），预留 15%；列出可选裁剪（约 113 天）。第二轮核对后为约 131 天（118–150），裁剪后约 122 天 | §10 |
| 2 | GUI 运行时导入 | 高 | 同 E.1 第 1 条 | §4.4.1 |
| 3 | Windows 的回归网看不到卡片 | 高 | 采纳：OracleWpf 的卡片渲染移到 P1，成为 B0 的一部分；改写各阶段的验证说法 | §5.14、§10 |
| 4 | W3 的异机路径守卫覆盖的地方太少；`~/` 会把笔记变成文件卡片 | 高 | 采纳：`LocalPath` 唯一出入口加源码扫描；与 PathRx 同一提交；去掉 `~/`；保留“照搬”作为备选 | §3.5.2 a、b |
| 5 | Swift 写出的一行坏数据能让 Windows 无法启动 | 高 | 采纳：写入前校验、`pincc doctor`、模糊测试；W3f 作为建议项请你批准 | §4.2.7 |
| 6 | W1 漏了 Cli.cs:303 的 `zzz` 和 Store.cs:282；生产 CLI 读环境变量 | 中 | 采纳：用反射固定本地时区覆盖 `zzz`；Store.cs:282 进 W1a；不读环境变量。不采纳“只固定 Asia/Shanghai”作为默认，列为裁剪项 a | §5.3、§10.3 |
| 7 | OracleWpf 的工程会破坏构建、断言本身不成立 | 中 | 采纳：Exclude obj/bin、程序集名 PinCC、放在 tests/ 之外、断言“从未显示”、屏幕作为参数、不进 build.cmd | §5.9 |
| 8 | build.cmd 会因向量过期卡住 Windows，并变慢 | 中 | 采纳：build.cmd 只加有上限的冒烟检查，从不因向量过期失败 | §5.16 |
| 9 | gzip 向量让仓库膨胀 | 中 | 采纳：不压缩 JSONL、每族约 5k 行、deep 只提交种子和哈希、随机脚本只提交每步哈希 | §5.3、§5.5 |
| 10 | 影子模式的保证言过其实 | 中 | 采纳：先录特征再移动；按文件提交；只对 LayoutPass 做影子；单独、有上限、哈希化的 shadow.log | §5.14、W4 |
| 11 | Intel Mac 的工具链下限可能不支持 Swift 6；只装 CLT 能否测试未验证 | 中 | 采纳：P0 第一天确定；装完整 Xcode；S6 闸门。只能运行时原先改在 Rosetta 下测，第二轮核对改为在 Intel Mac 上原生运行 `pincc-selftest`（E.5） | §9.1、§10 P0 |
| 12 | 缺少若干早期闸门 | 中 | 采纳：G1、G2、G3、G5、G6（编号 G4 未使用） | §5.6、§5.7、§5.9、§5.14、§5.16、§10 |
| 13 | 阶段顺序把能并行的工作串起来了；试用期和批复没排进日程 | 中 | 采纳：P0 ∥ P1；DotNetCompat 可提前；P6 在 P0 结论之后；影子期与 P7–P10 重叠；B0 以当前 HEAD 为准 | §10.2 |
| 14 | 导出包会泄漏密钥 | 中 | 采纳：敏感设置默认不导出，导入不覆盖 | §4.4.1 |
| 15 | WSLENV 没传过去会给你的实例发信号；ui.sh 会写 .probe/ | 中 | 采纳：卡片检查用 OracleWpf；新的 ui-snap.sh 先确认环境变量；断言 DefaultHome 不是真实目录；不调用 .probe/ | §5.14 |
| 16 | 变更流程对一个人跨三台机器太重；pre-push 钩子不现实 | 中 | 采纳：不设强制钩子；tools/sync.sh；Swift 跟进可以批量；写明 1.5–2 倍成本 | §5.15、§5.16 |
| 17 | “现在就为 L2 做”的几项增加 Windows 改动却不影响格式 | 低 | 采纳：设备 id、预留操作名、data.last_host 推迟；db.id 在首次导出时生成 | §3.5.2、§4.4.2 |
| 18 | 下载文档的 TextHash 改动收益小 | 低 | 采纳：改为“照搬”，登记为差异 | §3.5.2 |
| 19 | `pincc` 放在 Resources 里 | 低 | 采纳：放 Contents/Helpers，由内而外签名，严格校验 | §3.3、§9.6 |
| 20 | ssh 签名要把登录密码放进 WSL 脚本 | 低 | 采纳：专用构建钥匙串，密码只在 Mac 上 | §9.6 |

### E.3 两份评审之间的取舍

- **录制器 W4c**：评审 2 建议去掉以节省时间，评审 1 建议在影子期间强制打开。本文采用评审 1：它是桌面编排唯一不来自人工转写的证据。去掉它列为不建议的裁剪项 d。
- **时区**：评审 2 建议只固定 Asia/Shanghai，评审 1 建议让固定时区的方式与生产路径一致。本文默认采用评审 1 的做法（覆盖夏令时），把评审 2 的做法列为裁剪项 a，由你按是否在有夏令时的地区使用来决定。
- **新旧并行的影子**：评审 2 指出决策函数的影子实际上比较的是新代码和新代码，本文只对 LayoutPass 做影子，其余用“先录特征再移动”。

### E.4 接受的残余风险（汇总）

1. 转移表是人工转写的，真实轨迹没覆盖到的罕见桌面路径仍可能不同。
2. 屏幕外渲染看不到的交互问题（拖动手感、焦点）只能靠日常使用发现。
3. 语料和覆盖率之外的输入可能在 DotNetCompat 里表现不同。
4. 不采纳 W3f 时，一个漏网的写入缺陷仍可能让 Windows 启动失败。
5. 选择“照搬”而不改 PathRx 时，Mac 的文件卡片在 Windows 上显示为笔记。
6. 你手工把数据目录放进同步文件夹无法完全阻止；Windows 端的位置检查是可选的。
7. 不支持在 GUI 运行时导入。
8. macOS 27 小版本可能带来新的 AppKit 变化。
9. Intel 只能运行时，`pincc-selftest` 覆盖向量、脚本和 CLI 录制，但 AppKit 行为只能在它自己的系统上手工检查；macOS 11 或更早的 Intel Mac 不在支持范围内。
10. 自适应高度差一行会翻转结果的布局场景，两端可能出现不同的胶囊或 +N。
11. 历史日期的时区数据和孤立代理项的行为登记为差异。
12. 长期的逻辑改动成本约 1.5–2 倍。
13. 下载文档的 TextHash、无头渲染的 DOM、HTTP 实现细节、OS 错误文本、W4d 之前的批量顺序、来源网址、HEIC 转码、包目录、U+2028、回车行尾、全屏处理、“需要授权”状态、子进程清理、历史日期的时区等，登记为声明的差异（§1.4 各行，每行在 `mac-diffs.json` 里有 id）。
14. LLM 子进程的清理用 killpg 近似 Windows 的 Job 对象，调用 setsid 的孙进程收不到。

### E.5 第二轮核对（5 名核对者，92 条）

5 名核对者（数据格式、代码逻辑、外部事实、全文一致性、需求）对照源码和外部资料逐段核对，共提出 92 条，其中 8 条高、40 条中、44 条低。不少条目针对同一处文字，处理时合并成一段文字；与源码冲突的建议先查 src/ 再改写。下表按主题汇总，没有完全照原意采纳的条目单独列在后面。

| 主题 | 条目 | 处理 | 位置 |
|---|---|---|---|
| 数据格式与数据字典 | 3、83、8、10、87、12、13、14、57、15、62、88、63、66、4、72 | 采纳：`meta.Src` 可以为 null、可以与当前 Src 不同，校验不查相等；ops.cols 的合并规则；`user_version` 小于 1 时照搬迁移；图片扩展名的两套规则；FType 可为空串；建表语句是 Store.cs:36-52；Mac 只在数据目录里建 `.instance`；`mac.statusitem`、`mac.welcomed`；Mac 不读 Windows 的 `llm.*`；共享设置的取值语法永不就地修改（选 72 的第二种做法，SetSetting 不改） | §4.1、§4.2、附录 D |
| 本机作用域与交接 | 7、77 | 采纳并改写：新增“本机”作用域（`backup.last`、`db.id`、`local.handoff`），不导出、导入不覆盖；导入前备份用不会与每日备份重名的文件名；交接检查加 `--force` | §4.2.4、§4.4.1、D.5 |
| 异机路径（W3a、W3b、W3d） | 1、48、74、11、84、5 | 采纳：`ForeignPath(string)` 与 `LocalPath(card)` 两层出入口，覆盖点来源标题、Enrich 和 Summarize 的 `Fetch.File`、正文链接和图片；源码扫描按参数来源判断；Mac 路径用固定前缀，WSL 路径保持笔记；`\\server\` 不要求共享名段；W3d 改为按“异机位置”判断，拔掉的本机显示器保持今天的行为 | §3.5.2、§4.2.5、§7、§10.1 P3、§12 第 3 条 |
| 声明的差异 | 16、46、73 | 采纳：§1.4 补齐 14 行，每行在 `mac-diffs.json` 里有 id，引用未登记的 id 测试失败 | §1.2、§1.4、E.4 第 13 条 |
| 导出导入（W5、L2a），以及数据目录位置检查（64）和 L2b（78） | 2、42、71、9、50、79、86、78、64 | 采纳并合并：W5 让 GUI 另建按数据目录命名的互斥量（71），判断经进程内接缝 `Cli.GuiRunning`，测试从不碰真实互斥量（2、42）；包用建表语句加 `INSERT … SELECT` 生成，被排除的值从不写进文件（50），加字节搜索测试（79）；`--with-files`、必须带 `--replace`；help 和未知命令提示的变化写进提交说明；L2b 需要表结构 v2 的 `ops.last_at`；去掉 NSFileProviderManager。L2a 从 5 天增加到 7 天 | §3.5 W5、§4.2.9、§4.4、§5.6、§10.1 P11、§11 |
| 时钟、时区、语义表 | 6、58、91、20、19、25、22、23 | 采纳：W1b 列全 14 处；Native.cs 的 2 秒缓存列入 W1d 白名单；反射设的时区不影响 Windows 上的 `DateTime.Now`，W1b 之前的 B0 场景不放提醒；STJ 遇到孤立代理项转义抛异常；`Convert.ToInt64` 越界抛异常，加整数列毒样例；截断位置更正，CLI 截断照搬、只影响显示的截断登记差异；运行时拼出的正则来源更正，门槛按行计数 | §3.5 W1、§5.3、§5.4、§5.7、§6 |
| OracleWpf 与 Windows 不回退 | 17、27、41、70、49、67、76 | 采纳并合并：OracleWpf 从不调用 `Desk.Start` 和会走到 DoLayout 的方法，不建卡片的 HWND，对 `Surface` 做 Measure/Arrange，哨兵加 `Hwnd == 0`、无可见窗口和 Toast；工程补 StartupObject、全局 using、manifest；P3 的异机内容检查和 W4a 的特征录制都改为不显示窗口的调用；ui-snap.sh 在同一个 cmd 进程里设变量；build.cmd 跑源码规则检查、从不因向量过期失败；台账加 XAML 整文件哈希和生成环境指纹，发布闸门要求 check.sh 对应当前 HEAD | §5.9、§5.11、§5.14、§5.16、§10.1 P3、P6 |
| 单一来源的表 | 18、45、75、69 | 采纳：表按来源分三处导出（Core 反射、测试项目加入三个不依赖窗口的 UI 文件、OracleWpf）；方法体内的字面量先用词法抽取器、W4a 后改反射；集合扫描认 `is … or …` 和 switch；已是公开常量的 Metrics 直接导出；目录树补 gen-swift-tables.sh 和 schema.sql | §3.1、§5.1、§10.1 P1 |
| 桌面编排与录制器（W4c、W4d） | 44、51、68、80 | 采纳：录制器记完整正文和起始快照，只留在本机，提交的派生轨迹换成哈希；记下 `_cards` 的枚举顺序，W4d 之前重放用它作输入；`setx` 打开、按天分文件、50 MB 上限；罕见转移按清单手工触发，仍无法命中的由你签字豁免 | §3.5 W4c、§5.10、§10.1 P7、P9、§12 第 6 条 |
| CLI 与 Llm | 24、85、47、65、89、26 | 采纳：“忙”照搬“(5)”或“(6)”；W2l 图片行、W2h 的 `Cli.Cmd` 常量，比较时只替换它出现的位置；W4a 从 `Llm.Ask` 抽出 `PreparePrompt`、`Outcome`；§7 的 CardWindow 和 Card 派生行的行号更正 | §3.5.1、§4.2.8、§7、§12 第 7 条 |
| 编辑器行尾 | 21、90 | 采纳：EditorOps 向量同时覆盖 LF 和 CRLF 正文；新增 §12 第 14 条和待定的 W2m，P2 前答复、P7 依赖 | §1.4、§3.5.1 m、§7、§8.3、§12 第 14 条 |
| Mac 实现要点 | 30、31、32、35、36、37、38、59、34 | 采纳：状态项不看 currentEvent 的类型、临时挂菜单；`needsPanelToBecomeKey` 或 sendEvent 里 makeKey；离屏截图用 `render(in:)`，淡出不用 mask；WKWebView 在不显示的窗口里页面是 hidden，A6 实测；不用 `performDrag(with:)`；Tauri 的报告改为“未定”；`.strict` 定时器加到点前的短暂 `beginActivity`；选择器默认子面板、以 A3 为准；开发版用单独的数据目录 | §8.1、§8.2、§8.5、§8.10、§8.14、§8.16、§8.17、附录 B、C.2 |
| 工具链、签名、Intel Mac | 28、54、29、33、52、82、39、40、81、53 | 采纳并合并：Intel 表补 14.0–14.4、13.0–13.4、12.x、11 及更早四行，部署目标可退到 12，14 才有的接口加 `#available`；Swift 6.4 起单架构也用 Swift Build，两个 Swift 版本各用自己的构建目录；构建钥匙串一次性设分区列表和搜索列表，S2 经 ssh 无弹窗；swiftly 1.2.0；JSONEncoder、Calendar 的行为取决于系统 Foundation；不能编译的 Intel Mac 原生运行 `pincc-selftest`；`Posix.swift` 垫片 | §3.2、§8.1、§8.3、§8.9、§9、附录 C.1 |
| 计划与文档一致性 | 43、55、56、60、61、92 | 采纳：§10.2 分开工作量和关键路径；G4 未使用；“上一版”统一指 `git show 7e24435:docs/MACOS.md`；P0 完成标准逐项写清；P0、P1 的前提；§10.2 列出你本人要做的事和 Mac 连不上时的处理 | §0、§10、附录 E.2 |

**未采纳或改写的部分**（其余部分都按原意采纳）：

| # | 建议 | 处理 | 原因 |
|---|---|---|---|
| 2 | 继续用 `Local\PinCC.Instance`，在帮助和 DATA-MOVE.md 里注明它不区分数据目录 | 不采用这一半，接缝部分采纳 | 改用 71 的按数据目录命名的互斥量，两端规则一致，临时目录的导入不会因为你开着 PinCC 被拒绝 |
| 17 | 推进调度器之前用反射停掉 Desk 的 `_layout` 计时器 | 不需要 | `_layout.Tick` 的处理函数只在 `Start` 里挂上（Desk.cs:112），OracleWpf 不调用 Start，Relayout 什么也不做（70 的证据） |
| 18 | 在 W2 里把非成员表改成具名静态表 | 改写 | P1 不能改 src/；搬移代码属于 W4a 的“先录特征再移动”。改为 75 的做法：P1 用词法抽取器，W4a 搬移后改反射 |
| 41 | 用反射把样例卡片 Adopt 进 Desk（只建 HWND） | 改写 | 建了 HWND，`ScreenOf`、`MaxAutoHeight` 就读真实屏幕，绕过假屏幕（17）。改为反射放进 `_cards`、不建 HWND |
| 1、70 | P3 的检查里点开来源（`Desk.Open`）和手动 ✨ | 改写 | 异机路径上它们按设计经 Notify 弹提示条，这是真实窗口，违反“只在屏幕外渲染”。检查里用 `userAsked: false` 调 Summarize（同一个文件分支），Open、Reveal 由源码扫描和 `ForeignPath` 向量覆盖，再由你手工点一次 |
| 47 | §3.5 表单独加一行 W2l | 改写 | 放进 §3.5.1 的 l 项，W2 行注明，编号体系不变 |
| 54 | macOS 12 或更早一律不支持 | 改写 | 按 28 的查证，Xcode 27 能向下部署到 12；12 上加 `#available` 退路（登录项用 LaunchAgents plist），只有 11 或更早不支持 |
| 6、58 | Native.cs 的屏幕缓存计时改走 `When.Clock` | 不采用 | 采用 91 的白名单：它是与业务时间无关的单调计时，改走时钟会让 §5.9 的反射做法随之改变 |
| 72 | 第一种做法：`SetSetting` 在只读时拒绝写共享键 | 不采用 | 是另一项 Windows 改动；选第二种做法“换新键名”，不改 Windows |
| 77 | 键前缀改为 `local.`，记 `local.sync.<对方 db.id>` 和 max(updated_at) | 改写 | `backup.last` 改名是 Windows 改动，旧版仍会写旧名，所以保留原名，只把作用域改为本机；`journal:false` 的 meta 写入也推进 `updated_at`（Store.cs:155-160），会把自动补元数据误判为修改，所以只记 ops 的 `sqlite_sequence`（`local.handoff`） |
| 79 | 包先 `VACUUM INTO`，再 `secure_delete=ON`、删除、再 `VACUUM` | 不采用 | 采用 50 的 `INSERT … SELECT`：被排除的值从不写进文件，不依赖各端 SQLite 的编译参数，也省一次整库 VACUUM；79 的字节搜索测试采纳 |
| 75 | UI 的表也可以对构建出的 PinCC.dll 做 `Assembly.LoadFrom` 再反射 | 不采用 | 采用 45：测试项目直接编译三个不依赖窗口的 UI 文件，其余由 OracleWpf 导出，不依赖构建产物的状态 |

工作量：新增约 8 天工作（P1 +2、P2 +1、P3 +1、P5 +1、P6 +1、P11 +2），预留相应加 1 天，总数从 122 天变为约 131 天，关键路径约 126 天；裁剪后约 122 天（§10.2、§10.3）。
