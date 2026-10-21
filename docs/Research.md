# 绝区零国服 Steam 接入：依据与验证边界

核查日期：2026-10-21（Asia/Shanghai）。本工具是独立项目，不修改 Starward，也不添加“非 Steam 游戏”快捷方式。目标是由 Steam 正式 AppID `4162040` 的启动选项调用现有国服客户端。

## 已核实的公开资料

- [Steam 正式商店页](https://store.steampowered.com/app/4162040/)确认 `4162040` 对应 Zenless Zone Zero；页面列出 HoYoverse 第三方账号及 HoYoKProtect 内核反作弊。商店条目不证明国服客户端获官方支持，也不证明本方案能在当前 Steam 客户端统计时长。
- [Better Endfield 官方仓库](https://github.com/Dr-hydra/Better-Endfield)于本次 GitHub API 核查时的 `main` 为 `1038af190572511e64fe42053f74fb7d19931771`；[最新正式发布 v4.0.8](https://github.com/Dr-hydra/Better-Endfield/releases/tag/v4.0.8)发布于 2026-10-19。其 [LICENSE](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/LICENSE)为 AGPL-3.0。本工具仅参考公开行为和资料，独立实现，没有复制其源代码、注入器或游戏内模块。
- [Better Endfield 桌面工程文件](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/ui/BetterEndfieldNext.UI/BetterEndfieldNext.UI.csproj)显示 .NET 9、WinUI 3、Windows x64 自包含桌面程序；仓库另外包含原生 C++ 组件及 Android 功能。它是综合游戏增强工具，不能将整套行为等同于普通启动包装器。
- [Better Endfield Steam 功能说明](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/docs/host/STEAM_CN_LAUNCH.md)描述了安装 ACF、独立占位目录、启动选项、原启动选项备份及移除恢复。它明确要求已正常入库，并指出 `AutoUpdateBehavior=1` 不能保证 Steam 永不下载；验证或更新仍可能下载国际服文件。
- [Better Endfield 4.0.6 发布记录](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/docs/workspace/releases/4.0.6/RELEASE_4_0_6.md)说明其链式启动 Overlay 修复向挂起的游戏子进程注入 Steam Overlay。本工具不实现这一机制，因此不能承诺复制该修复效果。
- [Valve Steam Overlay 文档](https://partner.steamgames.com/doc/features/overlay)说明 Steam 通常会自动向经 Steam 启动的游戏接入 Overlay，并要求从 Steam 客户端启动。本工具采用正常进程创建和等待，保留 Steam 启动上下文；实际国服能否显示 Shift+Tab 必须单独验收。

## 客户端发现与权限依据

自动发现读取官方启动器登记位置 `HKCU\Software\miHoYo\HYP\1_1\nap_cn` 的 `GameInstallPath`，再定位 `ZenlessZoneZero.exe`。启动时要求相邻 `config.ini` 中存在 `game_biz=nap_cn`。发布包不预置任何使用者的磁盘路径、账号或游戏版本。

检查过的国服客户端执行级别清单包含 `requestedExecutionLevel level="requireAdministrator"`。因此，从普通权限 Steam 创建这类进程可能返回 Windows 错误 740；本工具报告该错误，由用户完全退出 Steam 后，通过正常 UAC 以管理员身份重新打开 Steam。工具不修改 Steam 兼容性或系统权限设置。权限满足也不能代替 Overlay 实测。

这些只读检查不能证明真实游戏启动、登录或 Steam 计时已通过；本次验证没有启动真实游戏、登录账号、修改真实 Steam 配置或写入游戏目录。

## 独立实现的设计要求

1. Steam 账号先通过商店正常获取此免费游戏的许可。ACF 和 `steam_appid.txt` 都不能授予账号许可。
2. 使用独立 Steam 占位目录，与原国服目录完全分离。国服 EXE、数据、反作弊文件及更新配置不改动，国服更新继续由官方启动器处理。
3. 只在 Steam 完全退出后应用或恢复；只修改所选账号、所选 AppID 的 `LaunchOptions`，保留其他账号、游戏和未知字段。检测到外部改动时停止，避免覆盖用户修改。
4. 每次接入先保存原值和文件备份；持有可识别的归属记录。恢复时仅删除本工具明确创建且仍匹配记录的内容，遇到未知文件、目录链接或其他安装应拒绝清理。
5. 安装目录、启动 EXE、BuildID 和 Depot Manifest 来源必须可追溯。第三方 Steam 元数据接口是获取公开元数据的途径，不是 Valve 官方保证；缓存必须带来源和时间。元数据缺失或异常时停止，不填猜测值。
6. Steam 原始 `%command%` 只用于接收和诊断，不执行，也不混入国服参数。用户选择的国服 EXE 独立保存并再次核验。
7. 当前包装器仅等待自己直接启动的游戏进程结束，不以同名进程长期维持运行；游戏自行脱离或重启进程的场景尚未验证，也未实现后继进程接管。
8. 提权只通过正常 Windows UAC 流程。无法创建进程并收到需要提权错误时，明确报告，再由用户决定正常提权；不静默改 Steam 兼容性、关闭 UAC 或修改反作弊设置。
9. 修复动作只恢复本工具管理的清单和启动选项，在 Steam 退出后进行。发布新版本时保留稳定安装路径及状态目录；发现 Steam 资源已下载或未知配置变更时应停止，不能递归清理后宣称自修复成功。

上述是本工具的实现与审查准则，不代表每项真实游戏行为已经完成验收；实际实现及检查结果以随包测试报告为准。

## “零下载”的准确范围

本工具不请求、不安装、不下载 Steam 国际服游戏资源；接入可以在未下载这些资源的前提下完成。Steam、游戏许可和公开 App 元数据仍然需要存在。

无法在保持 Steam 正常联网和更新能力的同时，用本地 ACF 保证它此后永远不下载任何国际服内容。Steam 可能重写清单、要求更新或校验。发现 Steam 出现“安装／更新／验证”或下载队列时，应立即停止此次验证并暂停该任务，不点击开始下载；退出 Steam 后重新检查接入配置。把自动更新设为“仅启动时”也不能作为绝对防下载保证。

## 必须由真实 Steam 会话验收的项目

| 项目 | 最小证据 | 不能代替该证据的结果 |
| --- | --- | --- |
| 正式游戏库接入 | Steam 库中 AppID 4162040 的正式条目显示可启动 | 本工具显示“已配置”、ACF 存在 |
| 启动国服 | 由该正式条目进入原国服账号／服务器流程，路径与已选客户端一致 | 仅出现包装器进程、环境变量包含 AppID |
| 原生游玩时长 | 记录启动前时长，实际运行约 10 分钟，正常退出并同步后记录正式条目的增长 | 包装器自行计时、`LastPlayed` 改变、Steam 显示正在运行 |
| 好友状态 | 好友端或第二个正常账号看到此正式条目的游戏中状态，并在退出后结束 | 仅当前用户库中的“停止”按钮、游戏内普通好友列表 |
| Steam Overlay | 游戏实际渲染时 Shift+Tab 成功显示 Steam 界面；截图可作留证 | gameoverlayui 进程存在、DLL 文件存在、继承了 Steam 环境 |
| 退出生命周期 | 游戏正常退出后，包装器结束，Steam 正式条目恢复“开始” | 只等待首个启动进程后立即退出、手动关闭包装器 |
| 未下载国际服资源 | 对照配置前后下载队列／占位目录内容和 Steam 下载日志 | 本工具自身没有 HTTP 游戏下载逻辑 |
| 更新持久性 | 国服和 Steam 分别正常更新后重复上述验证，且修复／恢复仍有效 | 隔离夹具中更换 BuildID 后测试通过 |

验收时应分别记录：已通过、失败、未测试。好友状态、时长和 Overlay 可以有不同结果，不能合并为一个“Steam 识别成功”。不把测试日志中的 AppID、继承环境或进程路径包装成账号服务器已经确认的结果。

## 不实施的行为

本工具不注入 DLL，不创建挂起进程做内存修改，不伪造官方父进程，不改反作弊，不写国服受保护文件，不调用 Steamworks 伪造富状态、成就、账号数据或时长。若普通 Steam 启动链无法实现 Overlay 或时长，应如实记录该项失败，不增加上述手段绕过限制。
