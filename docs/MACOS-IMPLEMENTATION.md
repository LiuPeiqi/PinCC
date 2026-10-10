# macOS 实现记录与本机验收

更新：2026-10-10。当前实现位于 `mac/`，以 `docs/MACOS-DESIGN-HANDOFF.md` 的独立悬浮卡片方案和当前 C# 源码为依据。规范基线提交为 `1bf37c6260626e354122b87414f6cce7f014ead7`，具体文件内容由 `mac/contracts/source-fingerprint.json` 固定。旧设计文档中“工程尚不存在”的描述是当时状态，构建入口以 [mac/README.md](../mac/README.md) 为准。

## 工程选择

本地 Swift Package 只承担可复用库与测试；Xcode 原生 targets 生成 `PinCC.app` 和内嵌 `pincc`。没有远程包、第三方 SQLite、跨平台 UI、额外 IPC 服务或自制窗口管理器。根目录 `builtin/` 是内置执行器和技能的唯一来源，构建时放入 app Resources。

每张卡片由独立 `NSPanel` 显示。AppKit 负责事件、原生输入、UndoManager、系统菜单、拖放和窗口生命周期；卡片库与设置是辅助窗口。SQLite 与 CLI 不依赖桌面实例启动。

| 目录 | 唯一职责 |
|---|---|
| PinCCCore | 数据、派生规则、SQLite、操作组恢复、Home 与草稿 |
| PinCCDesk | 无 AppKit 的矩形与布局算法 |
| PinCCMacPlatform | 本机进程、来源读取、技能与摘要 |
| PinCCCli | 两个入口共用的命令与 DTO |
| PinCCApp | 原生面板、桌面协调、Hub、设置、卡片库 |
| pincc | headless helper 入口 |

使用 Xcode 16 工程格式和 Swift 6.0 可用语法，部署下限 macOS 13。UI 明确隔离在 MainActor，后台工作传递 Sendable 数据；锁保护的 SQLite、取消与有界读取状态注明 unchecked Sendable 的边界。没有依赖 Swift 6.2 的 isolated deinit、默认 MainActor 或 nonisolated(nonsending)。完整 Xcode 的主机系统要求与 app 的部署下限不同，安装时核对 [Apple 支持表](https://developer.apple.com/xcode/system-requirements/)。语言规则依据 [Swift 6 并发迁移指南](https://www.swift.org/migration/documentation/swift-6-concurrency-migration-guide/enabledataracesafety/)、[Swift 并发语言规则](https://docs.swift.org/latest/documentation/the-swift-programming-language/concurrency/)、[Swift PackageDescription](https://docs.swift.org/latest/documentation/packagedescription/) 和 [Xcode 本地包说明](https://developer.apple.com/documentation/xcode/organizing-your-code-with-local-packages)。

## 数据与两端契约

- 保留实际 schema 2：cards、ops、settings，以及 ord、rev 和现有列；不加入旧计划中尚未落地的 last_at。
- Card 历史快照保留 PascalCase 与 null，CLI DTO 保留 snake_case。UTF-16 文本存取、ordinal 比较、摘要哈希和标签色与 C# 样例对照。
- Store 是唯一写入入口。事务内检查版本和输入；恢复仅恢复原组变动列，保留后来修改的其它列。多卡技能写入原子提交。
- 迁移检查实际旧表形状，兼容未标注 user_version 的 v0 日志缺列，保留卡片、图片、设置和旧操作；迁移在同一事务内完成。`StoreTests` 增加首次修改和撤销的回归。此项是 Mac 端对旧预发布结构的防御修复。
- 未来 schema 禁止写入；草稿冲突保留为待恢复，不覆盖外部 CLI 修改。损坏草稿保留文件并报告，其它草稿可继续恢复。
- 图片与下载文档使用现有小写 SHA1 前 20 位文件名约定；图片维护与单实例使用各自的本机用途锁。来源读取、日志及进程输出均有上限。
- Mac 执行器、快捷键和授权确认使用 mac.* 键。共享规则设置保留原有键；默认开发 Home 与正式 Home 分离。
- CLI 保留 list、show、add、set、rm、log、restore、skills、run，以及退出码 0/1/2/3/4。仅显式“-”读取 stdin，冲突和参数失败不产生部分写入。

SQLite C 边界使用系统头文件与 `sqlite3_bind_text16`、`sqlite3_column_text16`、显式长度和 SQLITE_TRANSIENT；SQL 语句及路径使用 UTF-8。依据 [绑定规则](https://sqlite.org/c3ref/bind_blob.html)、[列读取寿命与编码规则](https://sqlite.org/c3ref/column_blob.html)、[SQLite 备份 API](https://sqlite.org/backup.html)。本机跨进程用途锁依据 [flock 系统规则](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/flock.2.html)。

## 平台实现与允许差异

| 范围 | Mac 实现和边界 |
|---|---|
| 面板 | 创建时固定 borderless/nonactivatingPanel；允许 key、禁止 main，不在运行中切换样式 |
| Spaces | 普通/置顶使用 normal/floating；普通 Spaces 可见，未加入 fullScreenAuxiliary |
| 编辑 | NSTextView 纯文本与系统 UndoManager；marked text 期间先交给输入法；500 ms 自动保存，Esc 与失焦提交 |
| 显示 | 卡片默认 260 pt，正文默认 14 pt、设置范围 12–20 pt；普通卡临时拉宽与滚动在不再活跃时恢复；字体测量和系统阴影允许平台差异 |
| 屏幕 | ColorSync 本机屏幕身份，visibleFrame；仅 MacScreens 转换 AppKit 与工作区坐标 |
| 图片 | ImageIO 解码前校验尺寸与 8000 万像素上限，HEIC 定向转 PNG；实际像素考虑 backingScaleFactor |
| 系统入口 | 每屏 Hub、菜单栏、Carbon 注册热键；登录项由 SMAppService 显示真实状态 |
| 文件 | Windows/UNC 来源保留并说明；不猜测成 Mac 路径；本机错误区分权限和不存在 |
| AI | 本机 argv、stdin、进程组取消、共享总预算、排队计时；Windows/WSL 步骤明确不支持 |
| 网页 | 自动读取逐跳校验地址；无 cookie 凭据会话。手动请求的薄网页才允许系统 WebKit 回退 |
| 发布 | helper 先签、app 后签；本地 ad-hoc ZIP 与正式 Developer ID 公证 DMG 明确区分 |

面板与输入实现依据 [NSPanel](https://developer.apple.com/documentation/appkit/nspanel)、[视图面板焦点](https://developer.apple.com/documentation/appkit/nsview/needspaneltobecomekey)、[输入法组合状态](https://developer.apple.com/documentation/appkit/nstextinputclient/hasmarkedtext())、[NSWindow collectionBehavior](https://developer.apple.com/documentation/appkit/nswindow/collectionbehavior-swift.struct)。系统 API 的规则不能证明最终中文候选或 Spaces 行为，须执行下述实机验收。

图片、文件承诺、登录项、网页读取采用 [ImageIO](https://developer.apple.com/documentation/imageio)、[AppKit 文件承诺](https://developer.apple.com/documentation/appkit/supporting-table-view-drag-and-drop-through-file-promises)、[SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)、[URLSession](https://developer.apple.com/documentation/foundation/urlsession)、[WKWebView](https://developer.apple.com/documentation/webkit/wkwebview)。进程取消基于 Darwin 进程组，不把提示词拼成 shell 命令。
执行器依赖用户本机安装的 CLI，权限由该 CLI 的正式参数、授权和配置控制，PinCC 不把执行器设置等同于操作系统 sandbox。取消覆盖初始进程组和 leader；主动另建会话脱离的后代进程不具备 Windows Job 的生命周期保证。自动 HTTP 校验每次重定向和系统 DNS 返回的地址，但系统代理及再次解析仍有边界；手动 WebKit 的子资源不具备同样的逐跳过滤，所以只在用户显式请求时运行。PDF、Office 与 HTML 使用系统库和有界读取，抽取文本可有平台差异，不作为 778 条纯规则契约的一部分。

正式发布脚本在本机执行契约和双架构检查，再构建 staging、检查版本与架构、签名并严格验证。app 公证并 staple 后制作 DMG，再签名、公证和 staple DMG；本地构建不执行上传。依据 [Apple 嵌套签名规则](https://developer.apple.com/library/archive/technotes/tn2206/)、[公证工作流](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow)、[Developer ID 时间戳规则](https://developer.apple.com/documentation/technotes/tn3161-inside-code-signing-certificates)。

## Git 工作流与高版本升级

源码保留在 Git 仓库，Mac clone/pull 后直接打开 `mac/PinCC.xcodeproj`。不制作源码交接包，`artifacts/macos/` 缓存和 `dist/mac/` 成品都不进入 Git。

Release 设置同时构建 arm64 与 x86_64；GUI 和 helper 均经 lipo 验证。一个 Universal app 在两种芯片上使用本机架构，包格式为 `PinCC-<version>-<build>-universal.dmg`，本地验证使用明确标为 unnotarized 的 ZIP。[Apple Universal 编译规则](https://developer.apple.com/documentation/apple-silicon/building-a-universal-macos-binary)。

正式分发继续固定 `io.pincc.PinCC`、同一 Developer Team、`/Applications/PinCC.app` 和 `~/Library/Application Support/PinCC`。后续版本递增 CFBundleShortVersionString 与 CFBundleVersion；包名包含版本，app 与数据目录名不包含版本。CLI 链接指向安装路径的 helper，所以整包替换后跟随更新。[Apple 版本号规则](https://developer.apple.com/documentation/bundleresources/information-property-list/cfbundleshortversionstring)、[构建号规则](https://developer.apple.com/documentation/bundleresources/information-property-list/cfbundleversion)、[签名更新身份](https://developer.apple.com/library/archive/technotes/tn2206/)。

安装和升级交给 Finder：旧版正常退出，打开高版本已公证 DMG，整体替换原位置 app，再从该位置启动。保持 Home，运行时 Store 在事务内做结构迁移；future schema 拒绝写入。当前 Windows 产品没有在线自动更新器，Mac 也采用同类手动程序替换流程；Finder 不承担自动下载或强制拦截降级。路径、步骤及升级后核对见 mac/README.md。

发布先在 `artifacts/macos/release.*` 完成验证，正式 DMG 再通过同目录候选晋级到 `dist/mac/` 并输出 SHA256；本地 ad-hoc ZIP 留在 `artifacts/macos/local/`。相同版本和构建号不覆盖。失败保留诊断，不把未验证 staging 当成正式包。DMG 中 app、Applications 链接和安装说明在镜像签名及公证前准备完成。
## 对照 Boardcast 的构建修正（2026-10-10）

参考 Boardcast `b3260fd` 中已针对 Xcode 调整的配置和构建逻辑：

- 共享 preflight 检查完整 Xcode、Apple Swift 6 与 macOS SDK，脚本构建和诊断统一写入 `artifacts/macos/`；`mac/` 保留源码。
- `check.sh` 必须先执行源码、OpenStep 工程与 C# 契约指纹验证，再运行包 XCTest 和双架构 app 编译。发布复用这个入口，失败不会继续打包；各阶段日志保留在 `artifacts/macos/check.*`。
- helper 显式使用自己的 bundle identifier 签名，关闭 signed executable 的 copy strip 与 deployment postprocessing，防止签名后修改二进制；保留 Hardened Runtime 和从内到外的签名顺序。依据 [Apple 构建设置](https://developer.apple.com/documentation/xcode/build-settings-reference)。
- 工程与 scheme 的推荐设置标记对齐 Xcode 26，同时保留 Xcode 16 可识别的工程格式和 Swift 6 语言模式。PinCC 的测试由 SwiftPM 管理，不直接照搬 Boardcast 原生 XCTest target 的部署覆盖，也不提高 app 的 macOS 13 下限。

运行逻辑同步修正：菜单与设置共享技能目录缓存，扫描只在后台串行队列执行，周期请求不打断进行中的扫描，过时结果不回写；保存失败后回退到原文仍通过 revision 校验确认，清除自己的草稿，外部冲突保留；关闭 `llm.auto` 时不自动请求网页，手动请求与本地文件元数据读取仍可运行；网页读取以明确 MIME 为准，忽略标签使用嵌套栈并处理自闭合 SVG。

新增 `PinCCMacPlatformTests` 的四项回归，覆盖自动请求被阻止且快照与日志不变、手动请求和本地文件读取、MIME 路由及七组 HTML 场景。测试通过不可变 Sendable 读取闭包和锁保护 fixture 避免真实联网。

本次 Windows 验证已通过源码／工程检查（80 项）、5 个 Bash 脚本语法检查、7 个参数／非 Mac 环境退出场景及 C# oracle 重复生成：778 条向量与仓库样例逐字节一致。新增 Swift XCTest 尚未执行。工程结构校验与实际 Apple SDK 编译仍按下述证据分别记录，不能把静态通过当作 XCTest 通过。

仍有一项已知时间语义差异未在本轮修正：`When.parse` 的相对时间把整日按本地日历累加、余秒按绝对时间累加。在纽约 2026-03-07 中午，`23h` 和 `24h` 会同时落在次日中午，C# civil 加法分别为 11:00 和 12:00。完整对齐需统一 civil 运算并保留 .NET 在回拨第一次的歧义标记；现有 UTC 时间向量不能证明跨夏令时一致。后续必须用纽约、柏林春秋转换及两次回拨小时的 C# 时区基准验证，不改变当前 UTC 规范向量掩盖差异。

## 当前验证证据

开发主机为 Windows，尚未使用 Apple SDK 编译。完成的是源文件和结构检查、C# 可执行基准生成及回归，而不是 macOS 运行验收。

| 检查 | 证据 |
|---|---|
| Windows 当前产品回归 | 791 passed，0 failed |
| 实际 C# oracle | 778 条向量；重复生成内容一致 |
| 规范指纹 | 28 个规范文件的 canonical LF SHA256 |
| 升级 SQL 演练 | 空 v0、旧 v0、v1、v2、旧 v2、future schema 六场景；独立 SQLite 演练，不代表 Swift 测试通过 |
| Mac 契约覆盖 | Card、Markdown、When、排序、颜色、schema、布局、几何、Unicode ordinal |
| Store XCTest | 版本、逐字段恢复、回滚、草稿幂等与冲突、损坏文件、未来 schema、图片租约 |
| CLI XCTest | JSON、读取无写入、stdin、任务编号、恢复、删除与冲突 |
| 工程静态检查 | OpenStep、XML/plist、目标/资源引用、Swift 语法解析、Bash 语法 |
| 原生编译与测试 | 在 Mac 执行 check.sh；本次 Windows 会话没有运行 Swift XCTest 或 Xcode |

Swift 语法解析只能发现语法损坏，不能替代 Swift 类型检查、SDK 可用性和链接检查。本次静态检查使用独立的临时解析工具，检查完成后已清理；它们不是工程依赖。

## Mac 本机验收

首先保留整个仓库并运行 `bash mac/scripts/check.sh`，随后在 Xcode 中运行 PinCC scheme 的 Debug。首次使用独立开发 Home。编译报错必须在源文件中修正并重新执行相应检查，不能靠降低 Swift 模式绕过。

1. 普通与置顶卡的第一下点击、中文 Return/Esc/Tab、子面板焦点、复制粘贴与编辑前后撤销；Esc 提交后键盘回到原应用。
2. 160/200/260/320 pt 的提醒、进度、AI 与操作；快速加项一次会话一组，已完成折叠及原位撤销占位。
3. 平铺插入、自由卡避让、跨屏拖动、临时拉宽和胶囊展开；CLI 刷新不会抬高普通卡片。
4. Retina/普通屏实际像素、图片缩放、Photos/Safari 文件承诺、拖出，取消导入不留本次临时文件。
5. Finder 重开、菜单栏、登录项等待批准、快捷键占用、应用退出和重复打开；helper 独立调用和安装链接。
6. Dock/屏幕变化、拔屏接回、普通 Spaces、独占全屏、Stage Manager、Mission Control，提醒到期、唤醒与时间变化。
7. 本机 CLI 执行器检测、授权改变、取消和超时、取数失败、外部修改后的迟到结果；日志能按 run ID 关联。
8. 文件访问拒绝、保存失败、已持久化草稿、恢复冲突和损坏草稿；检查没有静默覆盖或重复恢复。
9. 深色、提高对比度、减少透明度、减少动态效果、VoiceOver；状态与操作不只靠颜色传达。
10. 在 Intel 和 Apple 芯片上分别安装同一 Universal 包；先安装旧版创建正文、图片、提醒、设置、撤销和 CLI 链接，再正常退出并用高版本整体替换。核对数据、版本、CLI 链接、登录项及文件访问授权；安装路径和 Home 保持不变。使用自己的身份完成签名与公证。

本轮修复的定向验收：慢盘或连续刷新技能目录时菜单仍响应，周期刷新不会使扫描结果一直失效；损坏技能文件只显示对应配置错误。锁住数据库让保存失败后，解除锁、Undo 回原文并按 Esc，确认编辑可以结束且没有残留草稿；新卡回退为空不会恢复此前失败文本；外部 CLI 修改仍产生 revision 冲突并保留草稿。关闭自动摘要后新建网页卡不产生自动来源请求，手动请求继续可用。

性能记录至少包含 1/30/50 张卡片的启动、空闲 CPU、内存与拖动，不能由源码推算承诺数值。通知通道、数据 export/import、在线合并、多语言和 Quick Look 在设计中属于后续扩展，当前不增加伪入口。
