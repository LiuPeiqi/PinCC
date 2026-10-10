# PinCC macOS 本地工程

Pin Carbon Copy 是桌面悬浮卡片程序。每张卡片由独立的 AppKit `NSPanel` 承载；每屏的“+”和菜单栏是入口。卡片库和设置是辅助窗口。

## 在 Mac 上打开

通过 Git clone/pull 把整个仓库放在本机目录，例如 `~/Developer/PinCC`，用 **完整 Xcode 16 或更新版本**打开 `mac/PinCC.xcodeproj`。选择 **PinCC** scheme、**My Mac**，运行 Debug。工程使用 Swift 6，部署目标 macOS 13.0；Apple silicon 和 Intel 使用同一份源码。Xcode 的主机系统要求随版本变化，安装前对照 [Apple 工具链支持表](https://developer.apple.com/xcode/system-requirements/)。

不用安装 .NET、Homebrew、XcodeGen，也没有远程 Swift 包依赖。Xcode 的本地 package 引用目录是 `mac/`，GUI 和 helper 是原生 Xcode targets。保留整个仓库结构，尤其是根目录 `builtin/`，不要只复制 `.xcodeproj`。源码以 Git 管理，不生成源码交接包；`artifacts/macos/` 和 `dist/mac/` 是已忽略的本机产物。

Debug 使用独立 bundle ID 和 `~/Library/Application Support/PinCC.dev`；Release 使用 `~/Library/Application Support/PinCC`。显式设置 `PINCC_HOME` 时两者都使用该目录。首次验证请使用开发目录或空目录。

## 目录与职责

```text
mac/
  PinCC.xcodeproj/             Xcode 原生 app、CLI target 和共享 scheme
  Package.swift               本地 Swift 库及 XCTest targets
  Config/                     Swift 6、部署、调试与发布配置
  Resources/                  Info.plist、entitlements、完整 macOS 图标
  Sources/
    CSQLite/                  系统 SQLite 头文件边界
    PinCCCore/                Card、Markdown、时间、数据库、撤销、Home、草稿
    PinCCDesk/                纯布局计算，工作区左上角坐标
    PinCCMacPlatform/         系统读取、进程、执行器、技能和摘要
    PinCCCli/                 GUI/helper 共用命令实现
    PinCCApp/                 独立卡片、每屏入口、菜单栏、设置、卡片库
    pincc/                    CLI helper 入口
  Tests/                      C# 对照样例与隔离数据集成测试
  contracts/                  当前 C# 输出和规范源码指纹
  scripts/                    本机构建、检查、发布、CLI 安装
oracle/mac-contracts/          只在 Windows 开发时运行的 C# 样例生成器
builtin/                      根目录唯一内置执行器和技能来源
```

系统框架包括 Foundation、AppKit、Carbon、CoreGraphics、ColorSync、CryptoKit、ImageIO、PDFKit、WebKit 和 SQLite。输入、文本撤销、文件选择、登录启动、图片解码、PDF 和窗口管理使用系统能力。helper 复用平台读取库，可能间接链接 AppKit，但 CLI 分支不会启动 `NSApplication`。

## 本地检查与构建

从仓库根目录执行：

```sh
bash mac/scripts/check.sh
bash mac/scripts/build.sh Debug
bash mac/scripts/build.sh Release
```

脚本先检查完整 Xcode、Apple Swift 6 和 macOS SDK；`check.sh` 用 Xcode 提供的 Python 3 执行工程结构与 C# 契约指纹检查，再运行 Swift XCTest、双架构编译、签名验证及 CLI 入口检查。检查日志保存在 `artifacts/macos/check.*`。脚本不修改 shell 启动文件、不启动真实卡片、不连接远程构建机。脚本可以通过 `bash` 调用，Windows checkout 不依赖可执行权限位。

PinCC app scheme 用于运行应用；XCTest 属于本地 Swift Package，由 `check.sh` 或以下命令执行，不把空的 app TestAction 当作测试通过。单独运行包测试：

```sh
xcrun swift test --package-path mac --scratch-path artifacts/macos/SwiftPM
```

Windows 上规范源码有变化时，用以下命令重新生成 C# 对照样例；Mac 日常编译不需要 .NET：

```powershell
powershell -ExecutionPolicy Bypass -File mac/scripts/update-contracts.ps1
python mac/scripts/check-source.py
```

`contracts/source-fingerprint.json` 记录生成时的规范源码哈希。源码变化后必须重新生成并核对样例，不能把旧期望值当成最新保证。

## .app 与发布

发布包结构：

```text
PinCC.app/Contents/
  Info.plist
  MacOS/PinCC
  Helpers/pincc
  Resources/Assets.car
  Resources/builtin/
```

GUI 与 helper 来自同一次 Xcode 构建。开发运行默认 ad-hoc 签名，无需开发者账号。用户自己执行正式发布脚本时，先签 helper，再签整个 app，启用 Hardened Runtime、时间戳、严格验证、公证和 stapling。签名后不修改 bundle。[Apple 嵌套代码与签名规则](https://developer.apple.com/library/archive/technotes/tn2206/)、[Apple 公证规则](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)。

只在本机使用的构建：

```sh
bash mac/scripts/release.sh --local
```

对外发布时使用你自己的 Developer ID 和钥匙串中的公证 profile：

```sh
bash mac/scripts/release.sh \
  --developer-id 'Developer ID Application: Your Name (TEAMID)' \
  --notary-profile 'pincc-notary' \
  --version 0.1.1 --build 2
```

开发过程不提交公证、不上传源码，也不把本地 ad-hoc 包标成已公证包。CLI 安装由用户显式执行 `install-cli.sh`，只建立指向 app 内 helper 的链接；不会自动改 PATH。

## 安装、高版本升级与路径

Release 生成 **一个 Universal app 和一个 DMG**，GUI 与内嵌 CLI 都包含 `arm64` 和 `x86_64`。系统在 Apple 芯片上原生运行 arm64，在 Intel 上原生运行 x86_64；无需按芯片分发两个包。Debug 默认只为当前机器构建。发布脚本用 `lipo` 验证两份可执行程序，缺任一架构即停止。[Apple Universal 规则](https://developer.apple.com/documentation/apple-silicon/building-a-universal-macos-binary)。

| 用途 | 固定路径或格式 |
|---|---|
| Git 源码 | 仓库 `mac/`，Xcode 工程 `mac/PinCC.xcodeproj` |
| 构建缓存与 staging | `artifacts/macos/`；Release app 在 `artifacts/macos/DerivedData-Release-arm64-x86_64/Build/Products/Release/PinCC.app` |
| 正式分发 | `dist/mac/PinCC-<版本>-<构建号>-universal.dmg`；发布时输出 SHA256 |
| 本地测试包 | `artifacts/macos/local/PinCC-<版本>-<构建号>-universal-local-unnotarized.zip` |
| 安装程序 | `/Applications/PinCC.app`；仅当前用户使用也可选 `~/Applications/PinCC.app`，升级始终保持同一路径 |
| 内嵌 CLI | `PinCC.app/Contents/Helpers/pincc`；用户命令链接为 `~/.local/bin/pincc` |
| 正式数据 | `~/Library/Application Support/PinCC/pincc.db`，图片 `img/`、下载文档 `files/`、常规备份和草稿在 Home 下，保存失败时草稿另有本机备用目录 |
| 开发数据 | `~/Library/Application Support/PinCC.dev/`；显式 `PINCC_HOME` 覆盖默认位置 |

首次安装打开已公证 DMG，将 `PinCC.app` 拖到 Applications，再从安装后的 app 启动并推出 DMG。该步骤使用 Finder 的标准应用安装方式。[Apple 安装说明](https://support.apple.com/guide/mac-help/install-and-uninstall-other-apps-mh35835/mac)。

升级步骤与 Windows 替换程序的方式一致：

1. 用“关于 Pin Carbon Copy”核对旧版。新发布递增 `--version` 和 `--build`；每次分发构建号都增加，不在已发出的版本／构建号上重新打包。
2. 在旧版菜单中正常退出，等待当前 CLI／技能执行结束。保留 Home 备份；不删除 Application Support。
3. 打开高版本 DMG，将完整 `PinCC.app` 拖到原安装位置，Finder 提示时选择“替换”。不把带版本号的 app 并列安装，不只替换 bundle 内的 executable。
4. 从原位置重新打开，在“关于”确认新版本，再核对卡片、图片、撤销和设置。数据库按现有 schema 在事务内迁移；遇到未来 schema 时旧程序只读。
5. 原来的 CLI 符号链接仍指向同一 `Contents/Helpers/pincc`，无需重装。若改变安装路径，先用原路径执行 `install-cli.sh --app <原路径> --remove`，再用新路径执行 `install-cli.sh --app <新路径>` 重建。检查登录项的真实状态，系统要求批准时从系统设置完成。

Release 的 bundle ID 固定为 `io.pincc.PinCC`；后续正式版本使用同一 Apple Developer Team 的 Developer ID。版本号、构建号改变，app 名、身份、安装路径和数据目录保持稳定。签名身份用于 macOS 识别程序更新；本地 ad-hoc 包仅供开发验证。Finder 替换不自动下载更新，也不保证拦截降级；按上述流程安装更高版本，与当前 Windows 产品的更新方式一致。[Apple 版本号](https://developer.apple.com/documentation/bundleresources/information-property-list/cfbundleshortversionstring)、[构建号](https://developer.apple.com/documentation/bundleresources/information-property-list/cfbundleversion)、[更新签名身份](https://developer.apple.com/library/archive/technotes/tn2206/)。

正式发布脚本仅在契约、双架构、签名、公证和包验证全部成功后，把临时成品晋级到 `dist/mac/`；本地 ad-hoc ZIP 留在 `artifacts/macos/local/`。失败留下诊断，不覆盖既有成品。相同版本和构建号的成品已存在时拒绝覆盖，需要递增后再发布。Git 不跟踪构建缓存或分发包。

## 数据和验证范围

SQLite 沿用当前 C# 的 `user_version=2`、`cards/ops/settings`、PascalCase 历史快照、UTF-16 文本边界和图片内容寻址。一次手势对应一个操作组，恢复只写该组改过的列。CLI 的退出码为成功 0、参数错误 1、找不到 2、冲突 3、忙 4。

普通卡片位置与大小来自用户操作；平铺位置、拔屏临时夹紧、临时胶囊不写回数据库。Mac 工具和快捷键使用 `mac.llm.*`、`mac.hk.*`；Windows 执行器设置保持原值。异机文件来源不会猜测成 Mac 本地路径。跨设备拷贝数据前退出两端所有进程并备份，当前版本没有 export/import 或在线合并命令。

详细实现记录、来源规则和验收项见 [MACOS-IMPLEMENTATION.md](../docs/MACOS-IMPLEMENTATION.md)。当前开发主机是 Windows；源码检查和 C# 样例生成不能代替 Mac 上的编译与交互验证。Xcode、签名、中文输入、Spaces、拖放、休眠和多屏测试需要在 Mac 执行。
