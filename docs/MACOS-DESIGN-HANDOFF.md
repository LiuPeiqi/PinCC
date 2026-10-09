# PinCC macOS 设计与本地开发交接

更新日期：2026 年 10 月 9 日。Pin Carbon Copy 的 macOS 版沿用 Swift 与 AppKit 路线，保留独立桌面卡片、快速采集、平铺避让、操作组撤销和本地 CLI，把窗口、输入、系统入口和权限反馈按 macOS 习惯实现。本稿补充现有 [macOS 制作计划](MACOS.md)，提供当前产品分析、界面规范、近期稳定性契约和 Mac 本地开发顺序。

“本地化编写与构建”按在 Mac 本机开发理解。界面先沿用简体中文，多语言为后续扩展。本文中的 mac 界面、模块名和构建脚本是实施设计，尚未成为可运行的 Swift 工程。

## 当前基线与迁移状态

| 范围 | 当前状态 | Mac 开发的含义 |
|---|---|---|
| Windows 产品 | C#、WPF、.NET 10，零 NuGet 依赖 | 当前源码是行为基准 |
| 数据与规则 | SQLite、Card 派生状态、Store 差量写与撤销、布局、时间解析、技能、CLI 已实现 | 需要 Swift 等价实现与差分证据 |
| macOS 方案 | MACOS.md 已有 Swift 与 AppKit 完整计划 | 延续路线和数据一致性约束 |
| Mac 工程 | 尚无 mac/、Package.swift、Swift 源码、bundle 脚本 | 不能直接执行旧文档中的 Mac 构建入口 |
| 一致性基础设施 | contracts/、oracle、生成器和移植台账尚未落地 | 先固定基准，再建立验证 |
| 数据交接 | export、import、异机路径守卫等仍属计划 | 先在隔离数据中验证 |
| 近期稳定性修改 | RunContext、Diagnostics、HomeLease、DraftRecovery 和相关测试存在于当前工作区 | 只克隆 HEAD 会遗漏部分内容 |

基准是本次分析时的工作区，HEAD 为 129a01d，存在未提交修改。迁移必须包含已跟踪改动与未跟踪源码。本稿没有修改生产代码，也没有验证 Mac 编译或运行。界面尺寸与交互来自代码审计，配套预览是设计示意。

既有计划中的特定 Xcode、Swift、macOS 版本与 Intel 兼容表，在进入开发时按实际机器和官方要求重新核对。旧计划的版本号不构成安装前提。

## 产品核心设计

PinCC 的主界面是桌面上的一组小窗口。每张卡片承载一件事：笔记、清单、链接、文档或图片；类型从正文和图片推导，用户无需先选模板。卡片库承担找回、搜索、归档和历史恢复，日常录入与查看直接发生在桌面。

最值得保留的六项规则：

1. **快速采集。** 每屏 +、全局新建、剪贴板和拖放共同承担入口。同一次新图片复制只自动贴一次。
2. **正文优先。** 无标题栏，底部状态常见，操作按悬停或键盘焦点出现。编辑原位进行，失焦与 Esc 都保存。
3. **派生状态。** 类型、任务进度、到期、摘要过时和自动配色不存成另一套业务状态。
4. **空间降级。** 竖排、避让、胶囊、隐藏与 +N 逐级处理空间不足；到期、编辑、撤销期间的卡片优先保留。
5. **可回退修改。** 一次编辑、拖动、批量平铺或技能运行是一组操作；撤销只恢复该组改过的字段。
6. **可取消 AI。** 摘要和技能通过本机 CLI 执行，结果写回前核对输入，卡片不依赖 AI 才能使用。

这些业务规则与数据格式两端统一。字体、图标、系统阴影、快捷键、设置窗口、菜单栏和权限提示按平台呈现。业务增强先落入 C# 基准或明确登记允许差异，避免 Swift 单方面改变任务、撤销、提醒和哈希语义。

## 当前 UI 细节

| 对象 | 当前实现 | macOS 基线 |
|---|---|---|
| 文字卡片 | 默认宽 260 DIP，最小 160 × 64；圆角 8，常态描边 1，无阴影 | 默认 260 pt、8 pt 连续圆角；系统阴影位于命中区之外 |
| 正文 | 字号 14，左右内边距 12，上边距 10；行高约字号的 1.5 倍 | 系统字体与中文回退，14 pt；代码使用系统等宽字体 |
| 自动高度 | 不超过 min(360, 工作区高度 × 40%)；手动改高后固定 | 保留高度规则，文字实测高度允许平台差异 |
| 底部行 | 状态在左、操作在右，总占高约 31 | 显式分配左右宽度及溢出优先级 |
| 折叠 | 文字胶囊高 36，图片缩略方块 48 × 48 | 保留标题、下一任务和必要状态 |
| 每屏 + | 直径 40，角落内边距 16；快捷按钮直径 32 | 保留每屏入口，菜单栏增加统一入口 |
| 平铺 | 间距 12、边距 16；每列取最宽卡片宽度 | 相同逻辑，以统一坐标适配屏幕 |
| 提醒弹层 | 宽 300，预设与自然语言输入，即时解释具体时间 | 输入弹层允许取得 key，日期和时区可见 |
| 管理窗口 | 640 × 720，卡片与设置两个 tab | 卡片库与设置分窗，使用原生窗口控制 |
| 图片卡片 | 全幅图片、深色悬浮操作底板、缩放与拖出 | 增加捏合、实际像素、适合窗口入口 |

自动浅色按**存储索引**依次为：柠檬 #FCF1BC、蜜桃 #FFEBD1、蔷薇 #FFE6F4、丁香 #F6EAFF、天空 #DFF3FF、湖水 #D6F8F7、薄荷 #DEF9DE；手动色为云灰 #F0F0F0、墨炭 #2F3133。存储索引与自动轮换顺序不同，Mac 使用导出的表，不重新排列索引。

当前菜单、弹层和管理窗 chrome 实际始终白色，Theme 注释中的系统明暗切换尚未兑现。Mac 系统窗口使用语义颜色跟随外观；默认卡片保留纸张色，墨炭采用深色墨色。整套卡片随系统变暗作为后续外观选项，复用现有颜色 ID，不改变存储含义。

保留交互节奏：卡片悬停延迟 150 ms；操作出现约 120 ms、收起约 200 ms；停止输入 500 ms 自动保存；完成或删除的原位撤销条保留 4 秒，悬停延长；Hub 约 300 ms 展开、350 ms 收起。减少动态效果时取消闪烁和位移动画，保留静态状态与文本。

## macOS 窗口与系统入口

### 常驻外壳

AppKit 管理生命周期，默认 accessory 模式与 LSUIElement；卡片不进入 Dock 和系统窗口轮换。每屏 + 和菜单栏提供入口；再次从 Finder 或 Spotlight 打开时显示卡片，并确保可以进入设置和退出。

菜单栏提供新建、粘贴为新卡片、显示卡片、平铺、全部折叠、隐藏普通卡片、卡片库、设置、退出。全局与本屏操作的范围写清，Hub 菜单明确使用“本屏”。隐藏继续保留到期卡片的业务规则。菜单栏左右点击使用同一菜单，避免依赖事件类型推断鼠标行为。

卡片库与设置使用普通原生窗口，可进入系统窗口切换；关闭后继续常驻。⌘W 只关闭辅助窗口，在卡片上沿用既有迁移计划的无操作语义。主菜单与 responder chain 始终存在，使 NSTextView 的复制、粘贴、撤销和重做正常工作。

生命周期不交给单独的 SwiftUI MenuBarExtra，因为用户移除菜单项可能影响仅有该场景的应用退出行为。[Apple MenuBarExtra](https://developer.apple.com/documentation/swiftui/menubarextra)

### 卡片面板与焦点

CardPanel 创建时即使用无边框非激活 NSPanel，不在运行中切换 nonactivatingPanel 样式。面板允许成为 key，不因应用失活隐藏；正文、图片和空白区域的第一下点击能选中卡片，悬停只显示操作。

把“应用是否激活”“哪张卡片是 key”“编辑器是否是 first responder”分开管理。非激活面板仍可能接收键盘，命中视图的 needsPanelToBecomeKey 也影响行为，必须通过 Mac 原型验证。[Apple 面板焦点](https://developer.apple.com/documentation/appkit/nspanel/becomeskeyonlyifneeded)、[视图键盘需求](https://developer.apple.com/documentation/appkit/nsview/needspaneltobecomekey)

| 事件 | 显示和焦点 | 数据规则 |
|---|---|---|
| 悬停 | 显示工具，透明置顶卡恢复清晰 | 不写库，不改变 key |
| 单击空白 | 选中卡片，获得卡片快捷键 | 不开始编辑 |
| 双击正文或 Return | 原位纯文本编辑，定位到点击位置 | 开始一个编辑操作组 |
| 打开提醒或颜色子面板 | 子面板与卡片形成一组焦点范围 | 不因子面板获得焦点而提交 |
| Esc | 有输入法组合时先交给输入法；正常编辑时提交并交还焦点 | 保存，不能解释为放弃编辑 |
| 点其他应用 | 提交正文，保留卡片 | 保存失败保留草稿和可见反馈 |
| 完成或删除 | 原处撤销条保留布局占位 | 一个可撤销操作组 |

焦点方案 A 延续非激活 key 面板；若中文候选、Esc 交还键盘或辅助窗口不稳定，采用方案 B：编辑时激活 PinCC，结束后尽可能返回原应用。原应用已退出或不可激活时不循环争抢前台，编辑数据仍应保存。

### Spaces 与多显示器

默认延续既有计划：跨普通 Spaces 显示，避开其他应用的独占全屏空间；置顶用 floating，普通卡用 normal。全屏、Stage Manager、Mission Control、多屏独立 Spaces 分别验收。canJoinAllSpaces 不能单独证明 Stage Manager 行为；primary、auxiliary、canJoinAllApplications 是互斥选项，按实际部署目标使用可用性检查。[Apple 窗口集合行为](https://developer.apple.com/documentation/appkit/nswindow/collectionbehavior-swift.struct)

只在屏幕适配层转换 AppKit 全局坐标与工作区相对坐标。使用 visibleFrame，核对刘海、安全区、Dock 变化与 backing scale。仅屏幕变化造成的临时夹紧不覆盖存储位置，接回后回到用户位置；用户显式平铺、移动、缩放继续按 C# 规则处理，本机拔屏与异机布局分开判断。Windows 显示器标识不能作为 Mac 屏幕身份。

## 卡片组件规范

### 底部行与空间避让

默认 260 pt 紧凑卡片。320 pt 舒适卡片是可选宽度预设，使用现有 w 字段；底栏分层时登记平台呈现差异并更新场景测试，不自动重写已有尺寸。

当前 WPF 底栏的左右 StackPanel 同处一个 Grid，缺少显式宽度避让。Mac 使用弹性状态区、固定主操作区和更多菜单：

| 优先级 | 显示规则 |
|---|---|
| 到期 | 到期文字、延后、完成优先；窄卡可临时增加提醒行 |
| 提醒 | 保留短日期与时刻，弹层和辅助功能描述提供完整时间 |
| 清单进度 | 保留 N/M，能快速添加；全勾完时完成按钮常驻 |
| 置顶 | 小图标配工具提示和可访问名称 |
| AI | 阶段文字按空间缩短，取消与错误详情始终可达 |
| 颜色与折叠 | 空间不足时进入更多菜单 |

悬停、卡片键盘焦点和子控件焦点都展示操作。160、200、260、320 pt 宽下状态不能相互覆盖。新增第二行属于呈现方案，视图不能擅自改变平铺优先级或持久 h。

### 清单与编辑器

NSTextView 为纯文本，关闭智能引号、破折号、自动替换、纠错和自动链接等会改变源码的行为。Return、Tab、Esc 经输入法感知回调处理；有 marked text 时不执行列表延续、快速加项或退出。中文候选在正常与置顶层级分别验证。

保留 Markdown 行号映射：复选框只改目标任务行；双击渲染文本定位回源码；快速添加一次会话算一个操作组。文字编辑撤销由 UndoManager 管理，结束编辑后卡片级撤销走 Store，不能一次 ⌘Z 调用两套撤销。

已完成任务达到条件时折叠成“已完成 N 项”；全部勾完不自动完成整卡。没有内容的新卡离开不留记录。清空已有卡片沿用可撤销删除。

### 图片与来源

保留全幅悬浮、深色工具底板和比例缩放。捏合缩放，⌘0 或菜单“实际像素”恢复 1:1；增加“适合窗口”，操作后短暂显示百分比。一个图像像素对应一个屏幕物理像素时，point 尺寸需除以 backingScaleFactor，跨屏重算。v1 沿用两端对原图片未旋转像素的显示规则；HEIC 转码时把方向写入输出像素，避免平台默认 EXIF 校正造成差异。

拖入区分文件 URL、网页 URL、文字与位图，保持剪贴板优先级。补 NSFilePromiseReceiver：Safari、Photos 等可能延迟提供文件，先显示导入状态，校验后提交；取消只清理本次临时文件。拖出可用文件承诺提供临时副本。[Apple 文件承诺](https://developer.apple.com/documentation/appkit/supporting-table-view-drag-and-drop-through-file-promises)

⌥拖出图片，⌘拖动留给系统后台窗口习惯。Quick Look 可作为后续查看增强，不改变存储和摘要输入。

异机文档显示“来自 Windows 的文件”，保留来源和摘要。“在 Finder 中显示”仅对可解析本机路径可用。TCC 拒绝为“需要文件访问授权”，不存在为“文件不存在”。共享路径守卫与 Mac 绝对路径识别尚未实现，先完成规范，不自动猜测并改写 Windows 路径。

### 外观与可访问性

正文保持实体纸张底色，系统材质用于菜单、设置和卡片库。SF Symbols 配短文字或可访问名称，不仅用颜色表示状态。逾期红框加到期文字，焦点用强调描边；降低透明度时用实体背景，不覆盖用户存储的 opacity。

清单控件有 checked 值，颜色项有名称和 selected 值，图标按钮能键盘到达。VoiceOver 顺序为来源、正文、摘要、状态、操作；编辑与阅读视图互斥暴露，避免正文读两次。按语义变化播报，不能每秒播报到期状态。

## 卡片库与设置

卡片库建议初始约 820 × 620 pt。侧栏为活动、已完成、已删除、历史；内容为搜索、列表、详情。窄窗收为列表与详情切换。活动项“显示在桌面”定位并临时突出；完成与删除项先恢复再显示。

保留当前标题、正文、摘要搜索。类型、标签、来源、提醒过滤与“待恢复编辑”是新增设计，查询或派生规则完成后再展示。永久删除单次原生确认，说明不可撤销；普通删除继续可撤销。

设置用 ⌘, 打开，分组如下：

| 分组 | 内容 |
|---|---|
| 通用 | 登录启动实际状态、菜单栏显示、再次打开行为 |
| 外观与桌面 | 屏幕角、配色、默认宽度、置顶与透明度 |
| 快捷键 | 录制、注册结果、重复或占用提示、恢复默认 |
| AI 执行器 | 本机工具、模型、检测、测试、实际命令；PATH 收在诊断详情 |
| 技能 | 生效来源、匹配说明、文件错误、运行授权、打开技能目录 |
| 数据与恢复 | Home、备份、草稿、日志；迁移完成后增加导出导入 |

配置即时生效，命令与模型先校验。Mac 工具与快捷键使用 mac.llm.*、mac.hk.* 等平台键，不读写 Windows 的 llm.tool、llm.model、llm.custom、llm.where、llm.distro、llm.env、llm.detected 和 hk.*。共享的 color.mode、text.size、tile.gap、sound、retention.days、llm.auto、llm.prompt、llm.run_timeout 保留原名和语义；tile.corner、hub.corner 延续既有计划的共享默认，其作用域仍列为待定。skill.ok.* 是否跨平台复用确认也按既有计划明确后再实现。多语言资源集中在视图层；参与 CLI、数据或哈希的文字继续走共享规范表。

快捷键沿用既有迁移表：Return 编辑、Esc 保存退出、⌘N 新建、⌘F 卡片库、⌘, 设置、⌘⌫ 删除、⌘Z 撤销、⇧⌘Z 重做、⌘C/V 复制粘贴、⌘反引号切卡；⌘D 不用于删除。编辑器不绑定 Control 加字母，保留 NSTextView 的系统文本导航。

## 提醒与权限体验

提醒仍只有 UTC 毫秒 remind_at，不增加重复提醒、截止日期或隐式完成。每个卡片与提醒时间组合只响一次；启动、唤醒、时间变化后重评估。到期展开、可见、置顶，提供 10 分钟、1 小时、完成。

清醒时按相同一秒业务网格评估，空闲调度可合并。strict timer 只是尽力遵守误差，允许 idle sleep 的活动不能承诺睡眠期间提醒。说明应区分清醒提醒、唤醒补提醒、可选系统通知。[Apple TimerFlags](https://developer.apple.com/documentation/dispatch/dispatchsource/timerflags)、[允许睡眠的活动](https://developer.apple.com/documentation/foundation/processinfo/activityoptions/userinitiatedallowingidlesystemsleep)

系统通知为新增可选平台通道，在核心提醒通过后接入。首次选择时申请权限，设置显示真实授权和测试结果。通知 ID 关联卡片与提醒版本，完成、删除、改期取消旧请求。桌面声音和通知共用投递决策，防重复；退出后已安排通知的行为要明确。Focus 与系统设置由用户控制，不承诺突破专注模式或唤醒睡眠。[Apple 本地通知](https://developer.apple.com/documentation/usernotifications/scheduling-a-notification-locally-from-your-app)、[通知权限](https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications)

全屏检测为尽量避扰：公开 API 不能读取其他进程 NSWindow styleMask，窗口大小只能近似。检测仅影响暂缓声音与显示层级，不改到期记录。可后续增加“暂缓提醒声 1 小时”。[Apple DTS 全屏检测](https://developer.apple.com/forums/thread/792917)

首次启动只介绍 +、快捷新建与保存。登录启动由用户选择，SMAppService 显示未注册、已启用、等待批准、不可用等实际状态，返回设置时刷新。[Apple 登录项示例](https://developer.apple.com/forums/thread/777520)

剪贴板用显式采集与无提示探测，不周期性请求内容。普通 + 的图片读取被拒时仍建空白卡片，解释原因并提供“粘贴为新卡片”。API 可用性与隐私行为在目标版本验证。[Apple 剪贴板访问](https://developer.apple.com/documentation/appkit/nspasteboard/accessbehavior-swift.enum)

全局热键沿用 Carbon 注册，应用内录制，默认仅新建 ⌃⌥⌘N，其余留空且可修改。无授权启动在目标 Mac 实测；不为录制引入全局键盘监控，后者需要辅助功能信任。[Apple 事件监控](https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/EventOverview/MonitoringEvents/MonitoringEvents.html)

## AI 与草稿恢复

### AI 任务

读取、取数、排队、模型、回写共用 RunContext 的单调截止时间与 run ID。Mac 模型阶段上限 150 秒与总预算分开，排队计时，合法长取数步骤保留。llm.run_timeout 是当前共享总预算键，不另造不兼容含义。

| 状态 | 呈现 | 动作 |
|---|---|---|
| 读取来源 | 读取中，正文仍可编辑 | 取消 |
| 等待执行器 | 排队中 | 取消、查看原因 |
| 模型执行 | 正在生成 | 取消 |
| 写回 | 正在保存 | 不重复启动同卡任务 |
| 成功 | 摘要和生成标记 | 撤销手动结果 |
| 输入已变 | 旧摘要变淡，新结果丢弃 | 重新生成 |
| 超时或失败 | 短错误分类，可展开详情 | 重试、检查工具 |
| 待确认授权 | 需要确认技能命令或权限 | 授权面板 |

同卡一个任务，全局并发沿用基准。取消和期限结束不触发读取回退，迟到回调不清除新任务状态。summary、append、cards 分别执行守卫，多卡插入原子提交。阶段是瞬时状态，不增 Card 存储列。

后台捕获登录 shell 环境，Finder 与终端启动结果一致或差异可解释。参数独立 argv，提示词 stdin。技能 windows/wsl 取数步骤在 Mac 报不支持，不静默当成本机执行。运行授权继续与技能哈希绑定，文件改变后重新确认。

### 保存与恢复

正常编辑自动保存；退出前先持久化编辑、快捷添加草稿，再有限等待保存。保存失败保留草稿和明确反馈；渲染失败与写入失败分开。

启动时成功恢复用非阻塞提示反馈。revision 冲突保留原草稿，不提前清理已存在的幂等标记，也不把未成功恢复的草稿标成成功。卡片库提供对照、另建卡片，不覆盖新正文。自动恢复沿用 key、组号与卡片同事务提交的幂等规则。人工冲突处理为新增 UI，实施时补事务和测试。

fallback 放独立于 PINCC_HOME 的本机正式或开发 Application Support 子目录，并按 Home 身份区分；Home/recovery 保留兼容草稿。先唯一临时文件、flush、原子改名，删除文件成功后才清理恢复标记。

## Swift 模块与数据边界

~~~text
PinCCApp          AppKit 外壳、菜单栏、主菜单、生命周期
PinCCMacPlatform  Panel、输入、屏幕、剪贴板、Finder、shell、登录项
PinCCDesk         卡片状态转换、编排、布局、编辑会话
PinCCCore         Card、Store、When、Markdown、Fetch、Skills、Llm
PinCCCli          纯文本接口，不启动 WindowServer
DotNetCompat      规范测试要求的字符、JSON、时间兼容语义
系统库适配         CSQLite、CZlib，无第三方 Swift 包
~~~

追加近期模块映射：

| C# 文件 | Swift 职责 | 契约 |
|---|---|---|
| RunContext.cs | RunContext | 单调预算、取消、执行器快照、唯一终态 |
| Diagnostics.cs | Diagnostics | schema、version、pid、UTC、run ID，不记正文或完整提示词 |
| HomeLease.cs | HomeLease | Images、Backup、Recovery 各用途维护锁、有限等待 |
| Diagnostics.cs 中 LogFile | LogFile | 日志独立跨进程锁、追加与轮转 |
| DraftRecovery.cs | DraftRecovery | 持久 checkpoint、幂等、revision 冲突保护 |
| SourceLimitException.cs | 有界读取错误 | 输入上限、解析期限、失败分类 |

维护、来源解析和可能阻塞的重读不占主线程。UI、维护、CLI 连接独立，在所有者队列串行使用；GUI 小事务按单一序列提交，提交后发布 UI 状态。AppKit/SwiftUI 更新在 MainActor，解析与子进程 I/O 有界并发。全部 GUI 写入异步化属于新增架构，需操作轨迹验证。取消检查实际读循环，不能只取消外层 Task。

GUI 单实例锁与维护用途锁分开。Mac 用真实路径身份与 flock 等跨进程锁，不能照搬 Windows 路径大写后哈希；大小写敏感与不敏感 APFS 都验证。GUI 与 CLI 共用 Home 解析函数、构建通道常量。

| 目录或文件 | 规则 |
|---|---|
| 正式 Home | ~/Library/Application Support/PinCC |
| 开发 Home | ~/Library/Application Support/PinCC.dev，bundle、通知、锁隔离 |
| pincc.db、img、files、agents、skills | 按共享规范验证和迁移 |
| runs.log、diagnostics.log | Home 内 GUI/CLI 协调追加和轮转，失败不阻断业务 |
| .instance、维护锁 | 本机协调，交接包排除 |
| recovery、恢复标记 | 恢复契约组成，明确本机作用域和交接处理 |
| 原生异常日志 | ~/Library/Logs/PinCC，开发通道隔离 |
| 渲染、LLM 临时工作 | ~/Library/Caches/PinCC，开发通道隔离 |

旧 MACOS.md 的“Home 只建 .instance、runs.log”应按上表扩展。恢复标记作用域、日志与锁的交接规则必须补数据字典，本稿不把未实现的包格式当成现成能力。

共用格式不意味着两机同时打开一个运行数据库。两机各用本地库，今后通过导出导入交接；运行数据库、WAL、构建缓存不放云同步目录。异机路径、布局、工具配置保留原值并由平台守卫处理。

## Mac 本地开发与构建顺序

### 接收基线

接收当前完整源码，包括未提交跟踪文件、未跟踪稳定性模块与测试。使用经审阅的提交，或源码快照加文件哈希清单；仅记录 HEAD 不够。记录 dirty 清单、源码哈希和 Windows 测试结果；oracle 向量标明同一基线。

仓库放本地 APFS，如 ~/Developer/PinCC。生产数据另备份，开发用隔离 Home。首日可直接执行环境盘点：

~~~sh
sw_vers
uname -m
xcodebuild -version
xcode-select -p
xcrun swift --version
git rev-parse HEAD
git status --short
~~~

按实际输出决定部署下限与 Intel 策略，分别记录系统 UI、编译器和 Foundation 版本。Mac 不需要 .NET，C# 规范输出由 Windows 提供。

### 先完成窗口原型

第一批仅做 CardPanel、纯文本编辑、提醒输入子面板、普通设置窗。验收第一下点击、悬停、中文候选、Esc 交还、子面板不误提交、⌘C/V/Z、全屏、Stage Manager、多屏缩放。A/B 焦点通过后再确定面板实现。

测量 1、30、50 张卡片的启动、空闲 CPU、内存、WindowServer 负载与拖动响应，建立预算，暂不承诺未经实测的资源数字。

### 分阶段实施

| 阶段 | 交付 | 下一阶段条件 |
|---|---|---|
| 基线与规范 | C# 快照、数据字典、最小 oracle 样例、工具链记录 | 样例对应明确源码 |
| 存储与 CLI | Card、SQLite、差量写、撤销、恢复、CLI | 两端脚本、冲突、退出码一致 |
| 桌面核心 | 布局、胶囊、编辑、原位撤销、提醒 | 布局与状态样例通过 |
| 卡片 MVP | 笔记、清单、剪贴板、图片、Hub | 原生输入与生命周期通过 |
| 系统功能 | 菜单栏、Finder、热键、登录、多屏、睡眠恢复 | 权限拒绝与系统切换可恢复 |
| AI 与技能 | shell、读取、进程组、预算、授权、写回 | 取消、超时、迟到结果通过 |
| 数据交接 | 实际导出导入、异机路径、设置作用域 | 两端隔离副本双向验证 |
| 发布 | staging bundle、签名、helper、升级 | 真正 .app 从 Finder 和登录项运行 |

这是本地推进顺序，工作量取决于既有计划的一致性强度，不改写为短期完成承诺。Mac 原型与 Windows oracle 可并行。

以下为**工程建立后的预期入口**，当前不能执行：

~~~sh
swift build --package-path mac
swift test --package-path mac
# 随后创建 scripts/bundle.sh，组装 .app 和 Contents/Helpers/pincc
# 创建生命周期、发布验证入口，再接签名和分发
~~~

helper 在 Contents/Helpers，与 GUI 同次构建、同通道、同 Home、同内置技能。CLI 安装优先用户目录；PATH 修改先展示具体内容，再由用户执行或确认，不在首次启动改 shell 文件。

构建采用 staging → 检查 → 替换，延续当前 tests/Build.ps1 的验证后晋级。先签 helper 等嵌套代码，再签 app，签名后不改 bundle。给他人分发再加 Developer ID 与公证。升级后的文件授权、登录项、CLI 链接真实测试，不能只看签名成功。[Apple 签名流程](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/Procedures/Procedures.html)

## 验收场景

| 场景 | 结果 |
|---|---|
| 最窄卡同时有长提醒、进度、AI | 不覆盖操作，完成、取消、完整提醒可达 |
| 中文候选期间 Return、Esc、Tab | 先完成输入法，不误加项、退出、改写源码 |
| 编辑内与编辑后 ⌘Z | 正确路由一套撤销，保留操作组 |
| 完成后悬停、离开 | 撤销条占位不变，计时后重排 |
| 临时拉宽、滚动、失焦 | 普通卡恢复，置顶卡保持 |
| Retina 与普通屏移动图片 | 实际像素与比例正确 |
| Finder、Safari、Photos 拖入 | 文件、文件承诺均可用，取消不留无主文件 |
| 多屏、拔屏、Dock、Spaces、Stage Manager | 不跑屏外，不覆盖存储位置 |
| 全屏、睡眠、时间或时区变化 | 到期保留，唤醒评估，声音不无界重复 |
| 登录、通知、剪贴板、文件权限拒绝 | 状态明确，仍能记笔记，入口可重新进入 |
| Finder 与终端启动 | CLI 检测一致或差异可解释 |
| 读取、排队、模型、回写取消 | 停本次工作，旧回调不改新状态 |
| CLI 改正文后摘要回来 | 旧结果丢弃，新正文保留 |
| 保存失败、可处理异常退出、已持久 checkpoint、恢复冲突 | 幂等恢复，无静默覆盖或重复恢复；不承诺强杀或断电恢复最后尚未持久化字符 |
| 图片写入与回收、备份、日志并发 | 用途锁、有限等待、副本完整 |
| 深色、对比度、透明度、减少动画、VoiceOver | 内容可读，操作可达，状态不只靠颜色 |

快照用于外观和测量，真实运行用于焦点、IME、拖动、退出、草稿，不能互相代替。验证使用隔离 bundle 和 Home，不唤醒或修改真实 Windows 实例。

## 后续 Mac 开发指令

> 阅读 docs/MACOS-DESIGN-HANDOFF.md、docs/MACOS.md、docs/DESIGN.md 和当前源码。先核对基线包含 RunContext、Diagnostics、HomeLease、DraftRecovery 及测试，记录本机工具链与架构。保留 Swift、AppKit、系统 SQLite、无第三方包路线，开发用独立 Home 和 bundle 身份。先验证 CardPanel、NSTextView 中文输入、焦点交还和 Spaces 最小原型；通过后搭建 SwiftPM，优先移植存储、撤销、恢复、CLI。只在文件真实存在后执行构建入口。业务差异登记并提供 C# 基准证据，开发版不写生产数据。

## 源码定位

| 依据 | 本次审计定位 |
|---|---|
| 工程与命令 | src/PinCC.csproj:4、src/Core/Cli.cs:19 |
| 数据、来源、默认宽度 | src/Core/Card.cs:14、:32、:113 |
| 差量写、批量、恢复 | src/Core/Store.cs:158、:184、:325 |
| 卡片结构与底栏 | src/UI/CardWindow.xaml:4、:35 |
| 保存、焦点、键盘 | CardWindow.xaml.cs:785、:818、:864、:1249 |
| 布局与高度 | src/UI/Layout.cs、src/UI/Desk.cs:408 |
| Hub 与管理 | HubWindow.cs:22、ManageWindow.cs:27 |
| 配色与 chrome | src/UI/Palette.cs:19、:43 |
| 预算与诊断 | RunContext.cs:5、Diagnostics.cs:9 |
| 用途锁与草稿 | HomeLease.cs:12、DraftRecovery.cs:40 |
| 生命周期与产物 | tests/UiSmoke.ps1、tests/Build.ps1:31 |

行号对应本次工作区，后续按成员名定位。完整迁移计划承担算法和兼容性细节，本稿承担平台体验、稳定性补充与本地开发交接。
