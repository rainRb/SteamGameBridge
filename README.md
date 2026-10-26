# SteamGameBridge

[![Windows build](https://github.com/rainRb/SteamGameBridge/actions/workflows/build.yml/badge.svg)](https://github.com/rainRb/SteamGameBridge/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

把已经安装的本地游戏接入 **Steam 正式游戏条目**的独立 Windows 工具。可以同时管理多个游戏，选择内置预设或手动输入 Steam AppID；也可以让绝区零、崩坏3把实际游戏进程交给 Starward 记录时长。

## 预设与自定义 AppID

| 本地预设 | 默认 Steam AppID | Starward 时长联动 | 默认启动方式 |
| --- | --- | --- | --- |
| [明日方舟：终末地](https://store.steampowered.com/app/4732690/) | `4732690` | 不启用 | 官方启动器 |
| [绝区零](https://store.steampowered.com/app/4162040/) | `4162040` | `nap_cn` | 直接启动 |
| [鸣潮](https://store.steampowered.com/app/3513350/) | `3513350` | 不启用 | 直接启动 |
| [崩坏3](https://store.steampowered.com/app/1671200/) | `1671200` | `bh3_cn` | 直接启动 |
| 自定义游戏 | 手动填写 | 不启用 | 直接启动 |

**本地游戏预设与 Steam AppID 分开保存。** 选择预设会填入默认 ID，之后仍可修改。获取元数据后，界面显示 Steam 真实名称及本地映射。

自定义 AppID 必须有可用的公开 Windows EXE、安装目录、公共分支 BuildID 和 Depot Manifest 元数据。多个 Windows 入口必须明确选择；信息缺失、路径异常或已有未知安装时停止。选中的入口决定占位程序路径。

## 环境与准备

- Windows x64；源码构建使用 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（版本选择见 [global.json](global.json)）。
- 已安装 Steam，并用自己的账号正常入库所选正式条目。本工具不授予游戏许可，也不处理商店地区限制。
- 已有能正常运行的本地客户端。更新继续通过原游戏官方启动器进行。
- `small` 轻量版需要 .NET 10 Desktop Runtime x64；`self-contained` 版内置运行时，功能相同。
- 接入、修复或恢复前完全退出 Steam 及对应游戏。工具不会自动结束这些进程。

首次打开的新列表为空。新增游戏后，选择实际游戏 EXE、Steam 安装位置、游戏库和目标账号。没有唯一最近账号时，必须手动选择。Steam 路径发现失败时可自行选择安装目录。

如果创建游戏进程需要管理员权限，先完全退出 Steam，再手动正常提权打开 Steam。

## 接入步骤

1. 点击“新增游戏”，填写配置名称并选择本地预设。需要时修改 Steam AppID。
2. 核对 Steam 安装位置，选择游戏库及目标账号；实际游戏 EXE 必须指向本地客户端，而不是启动器。
3. 配置以下三种启动方式之一，填写本地游戏参数。
4. “获取元数据”，或导入同格式的 Steam app-info JSON。多个入口时，在“Steam 启动入口”中明确选择。数据来自第三方公开镜像 `https://api.steamcmd.net/v1/info/<AppID>`。
5. 在“接入预览与运行日志”核对正式条目名称、实际游戏路径、清单位置及预计启动项。
6. 完全退出 Steam，点击“一键接入”，在确认窗口选择如何处理原启动项并核对最终游戏参数。工具会备份原值。
7. 正常打开 Steam、登录相同账号，从正式游戏条目启动；也可以点击工具中的“从 Steam 启动”。

游戏设置可先保存，稍后接入。已接入的配置锁定路径、AppID、参数和账号；要修改先恢复，再重新接入。列表中的游戏各自保存备份、维护和恢复状态。同一 Steam 安装与 AppID 只能有一份配置。

## 三种启动方式

### 直接启动游戏

直接运行所选 EXE，仅传入明确配置的游戏参数，并等待该实际进程退出。Steam 原始命令默认不执行、不传给游戏。自重启或退出后另启后继进程的场景没有通用接管保证。

### 官方启动器

分别配置“实际游戏 EXE”和“启动器 / 包装器”。工具正常打开启动器，等待新出现的实际游戏进程；启动器可以保持自己的正常界面，由用户手动点击开始。

默认等待 180 秒，可改为 1–1800 秒。按实际游戏的**完整路径、创建时间、当前 Windows 会话**识别；不能确认、出现多个候选或无法读取进程信息时报告失败。

终末地优先使用此方式。

### 已有第三方包装器

选择自己已经安装的包装器，并填写参数模板。以下占位符必须作为独立参数使用，可放在引号内：

| 占位符 | 展开结果 |
| --- | --- |
| `{game}` | 实际游戏 EXE 完整路径，作为一个参数 |
| `{gameArgs}` | 按 Windows 引号规则解析后的游戏参数列表 |
| `{steamCommand}` | Steam 在 `%command%` 处传入的原始命令参数列表 |

例如，调用用户已有的 Better Endfield：

```text
启动器 / 包装器：<已安装的 BetterEndfieldNext.Launcher.exe>
参数模板：--hide-console --game {game} -- {gameArgs} --steam-command {steamCommand}
```

路径和参数分别保存，通过参数列表创建进程，不经过命令解释器；不支持 `--game={game}` 这种嵌入式占位符。官方启动器模式也可使用这些明确占位符；只有模板中出现 `{steamCommand}` 才会转交 Steam 原始命令。

本项目不安装、捆绑或更新第三方包装器。包装器自身的额外行为和兼容性需要单独了解。桥接进程只等待确认的实际游戏，不等待包装器常驻进程无限运行。

生成的 Steam 启动项形如：

```text
"<稳定工具目录>\SteamGameBridge.exe" --profile "<该游戏状态目录>\launch.json" --steam-command %command%
```

`launch.json` 保存此游戏的明确启动配置。直接双击启动包装器不能替代从正式 Steam 条目启动。工具不会自行伪造 `SteamAppId` 或 `SteamGameId`。

## 原启动项的三个选择

接入前同时显示原启动项、预计桥接命令和最终游戏参数：

| 选项 | 行为 |
| --- | --- |
| **保留原设置** | 不修改该游戏 Steam 配置，本次不接入。 |
| **保留游戏参数并接入** | 按 Windows 引号规则保留纯参数，与本次参数合并后供编辑确认。顺序保留，不擅自去重或解释冲突。 |
| **完全替换启动项** | 使用本次设置的桥接命令与游戏参数，不沿用原 Steam 参数。 |

如果原值含完整 EXE 命令、命令链、`%command%` 或不完整引号，不会自动把整段塞入游戏参数；需要在确认窗口里仅填写想保留的参数。后两项都备份原值，恢复时可找回。

确认后原启动项又发生改变时停止接入。即便选择“完全替换”，仍不会接管已有 Steam 安装、清理未知目录或覆盖别人的清单。

## Starward 时长

为**绝区零和崩坏3**预设提供“使用 Starward 记录时长”开关。已注册协议可用时，选择预设默认勾选；可手动关闭。没有注册协议时，可以指定现有 Starward EXE。

流程是：**Steam → 本工具 → 本地游戏 → 确认真实 PID → 通知 Starward**。协议用于交接进程，不是启动游戏：

```text
starward://playtime/nap_cn?pid=<真实游戏PID>
starward://playtime/bh3_cn?pid=<真实游戏PID>
```

优先调用注册协议；未注册时，将同一 URI 作为一个参数传给所选 Starward EXE。不会调用 `startgame` 重复启动游戏。检查实际游戏 EXE 与本地地区标识，不能把包装器 PID 交过去。

同一 PID 与创建时间仅尝试通知一次；Starward 仍有自己的 PID 去重机制。协议缺失、EXE 失效或发送失败时，继续等待游戏正常退出，不结束游戏、不无限重试。

界面和日志区分“协议可用”“已发送计时请求”“不可用”。**已发送不等于时长已记录**，没有公开确认回执；需要在 Starward 中查看真实记录。后续功能由 Starward 自身处理，本工具不改写它的源码、配置或数据库。

## 维护与恢复

配置、启动组件和备份保存于：

```text
%LOCALAPPDATA%\SteamGameBridge
  profiles.json                         游戏配置列表
  bin\<工具哈希>\                       稳定启动组件
  integrations\<Steam安装标识>-<AppID>\  各游戏清单归属、启动配置、状态和备份
```

未恢复接入前不要删除状态目录。不同游戏使用独立记录；修改共享 Steam 账号配置时按 Steam 安装位置串行加锁，避免丢失另一款游戏的更新。

开启维护后，工具仍在运行时，会在 Steam 和对应游戏退出后重新获取元数据，修复自己的缺失清单、占位文件和缺失/空启动项。关闭窗口可收至托盘，托盘退出后停止维护；没有开机自启或计划任务。临时失败至少间隔五分钟重试。用户改写的非空启动项、未知文件或 Steam 下载状态不会被自动覆盖。

Steam 可能在更新、校验或状态变化时自行下载资源；安装清单和 `AutoUpdateBehavior=1` 不能保证永远零下载。出现安装、更新或验证任务时暂停该任务，退出 Steam 后检查；**不要用“验证完整性”修复占位安装**。

恢复时仅移除仍符合归属和哈希记录的占位内容，并恢复原启动项。用户自己后来修改的启动项保留；发生文件冲突时保留文件与恢复记录供检查。不会把操作前的整份配置备份覆盖回共享 VDF，因此其他游戏后来的设置不会丢失；新建的空 App 对象可能保留。

更换游戏路径、账号、游戏库或工具组件时，先恢复，再按新设置接入；Steam 安装目录或启动入口元数据改变时也需重新核对。

## 从源码构建与测试

```powershell
.\Build.ps1                         # 轻量版
.\Build.ps1 -SelfContained          # 内置运行时版
.\Build.ps1 -All -RunTests -Package  # 两种版本、隔离测试与本地产物
```

输出位于 `artifacts/`。

```text
src/SteamGameBridge.App           Windows Forms、启动链和 Starward 通知
src/SteamGameBridge.Core          游戏配置、元数据、VDF/ACF、备份与恢复
src/SteamPlaceholder              启动项失效时的失败保护占位程序
tests/                           配置与实际假进程隔离测试
docs/                            来源、验证范围、构建与版本说明
```

## 许可证

本项目采用 [MIT 许可证](LICENSE)，允许使用、修改和分发。分发源码或软件副本时，请保留原版权声明与许可证文本。软件按原样提供，不附带担保。

游戏、Steam 及相关软件的名称、商标与资源归各自权利人所有。
