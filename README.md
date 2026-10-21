# ZZZ Steam Bridge

将已安装的《绝区零》官方国服客户端接入 Steam 正式条目（AppID `4162040`）的独立 Windows 工具。

[![Windows build](https://github.com/rainRb/ZZZSteamBridge/actions/workflows/build.yml/badge.svg)](https://github.com/rainRb/ZZZSteamBridge/actions/workflows/build.yml)
[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

> **实验预览版。** 已通过编译和隔离测试；真实国服登录、Steam 原生游玩时长、好友状态、Overlay 与更新后的持续可用性尚未验收。它不是米哈游或 Valve 的官方工具。

## 下载

前往 [Releases](https://github.com/rainRb/ZZZSteamBridge/releases) 下载 Windows x64 压缩包，解压后运行 `ZZZSteamBridge.exe`。

| 版本 | 适用情况 |
| --- | --- |
| `small` | 轻量单文件，需要已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。安装 SDK 不是运行工具的必要条件。 |
| `self-contained` | 内置所需 .NET 运行时，体积较大，不需要另行安装 .NET。 |

两个版本使用相同功能实现。发行包附带 `SHA256SUMS.txt`，程序未作商业代码签名。

## 能做什么

- 使用已有国服 `ZenlessZoneZero.exe`，无需先下载 Steam 国际服游戏资源。
- 为正式 AppID 生成独立安装清单和小型占位程序，不使用“添加非 Steam 游戏”。
- 只修改明确选择的一个 Steam 账号中该游戏的启动选项，保留其他游戏和账号配置。
- 一键接入、检查修复、恢复原启动选项；操作前保留备份与归属记录。
- 通过 Steam 的启动命令调用国服，保留正常启动环境并等待自己启动的游戏进程退出。
- 可在托盘维护：Steam 和游戏完全退出后修复已管理的配置；不注册开机自启或计划任务。

本项目独立于 Starward，不修改其项目、安装或配置。没有复制 Better Endfield 的 AGPL 源码，也不使用它的注入器、XInput 代理或进程内模块。参考资料与实现边界见 [依据与验证](docs/Research.md)。

## 使用步骤

1. 安装 Steam，用自己的账号通过正常商店流程入库 [绝区零](https://store.steampowered.com/app/4162040/)。**入库不等于下载客户端**；此工具不授予许可，也不处理商店地区限制。
2. 先确认国服能通过官方启动器正常运行，再退出游戏和 Steam，包括 Steam 托盘后台。
3. 打开工具，核对自动检测的国服 EXE，选择 Steam 游戏库及目标账号。没有唯一最近账号时必须自行选择。
4. 获取最新元数据，检查界面中的目标路径及启动命令，再点击“一键接入”。启动选项会自动写入所选账号，无需另行粘贴。
5. 打开 Steam 并登录相同账号，从绝区零正式条目启动。国服要求提权时，先完全退出 Steam，再使用“管理员打开 Steam”完成正常 UAC 授权。

已有 Steam 国际服安装、下载暂存目录、未知接入清单或占位目标目录时，工具拒绝覆盖。不要将占位目录设置为国服安装目录。国服游戏更新继续通过官方启动器完成。

生成的启动命令形如：

```text
"<稳定目录>\ZZZSteamBridge.exe" --game "<国服目录>\ZenlessZoneZero.exe" -- <国服参数> --steam-command %command%
```

`%command%` 后的 Steam 原始启动程序和国际服参数不会被执行或转发给国服。直接双击启动包装器不能替代从 Steam 正式条目启动。

## 维护、升级和恢复

状态、稳定启动组件和备份保存于 `%LOCALAPPDATA%\ZZZSteamBridge`，不依赖下载文件所在目录。**未恢复接入前，请不要删除此目录。**

开启托盘维护后，关闭窗口会收至托盘；Steam／游戏完全退出时会联网获取元数据并尝试修复。临时失败后间隔五分钟重试，完整退出工具后停止维护。Steam 运行期间不改写清单或启动选项。

缺失或空的受管启动选项可以修复；用户改成其他非空命令、出现未知文件或下载状态时，停止覆盖。更换游戏路径、Steam 账号、游戏库，或想采用新版本工具的启动组件时，先恢复旧接入，再重新接入。当前版本不会自动迁移旧启动组件。

恢复时完全退出 Steam 和游戏，点击“一键恢复”。工具恢复原启动选项，移除仍匹配归属记录的占位文件与清单，保留备份。其他程序或用户修改过的值、未知文件不会被删除；发生冲突时界面会报告需要人工检查的项目。

## 关于“零下载”与真实 Steam 效果

工具自身只请求公开元数据，**不下载国际服游戏资源**。但 Steam 仍可能因更新、校验或状态变化自行要求下载；本工具不能保证整个 Steam 客户端此后永远零下载。Better Endfield 的功能说明也明确列出了这一限制。

若 Steam 显示安装、更新或验证任务，应停止此次启动并暂停该任务，退出 Steam 后检查配置。不要通过校验完整性来修复此占位安装，也不要将自动更新选项当作绝对防下载保证。

首次实际验收请分别检查：

| 项目 | 如何确认 |
| --- | --- |
| 正式游戏条目 | AppID 4162040 的条目显示运行中，且启动的是已选国服客户端。 |
| 原生游玩时长 | 记录启动前时长，真实运行后正常退出、同步，再确认该正式条目增长。 |
| 好友状态 | 请好友核对正式游戏中的状态及退出后的结束状态。 |
| Overlay／截图 | 在真实游戏画面中测试 Shift+Tab 与 F12。 |
| 生命周期 | 游戏退出后包装器结束，Steam 条目恢复“开始”。 |
| 没有国际服下载 | 检查 Steam 下载队列、日志和占位目录是否出现额外内容。 |

进程存在、AppID 环境变量、ACF 或包装器计时都不能代替上述验收。当前只等待直接启动的子进程，游戏自重启／脱离后的跟踪尚未验证。

工具不修改国服受保护文件，不绕过反作弊，不注入 DLL，不伪造官方父进程、账号许可、成就或时长。普通启动链不能提供某项 Steam 功能时，该项应记录为失败；不承诺官方认可或账号零风险。

## 从源码构建

需要 Windows、[.NET 10 SDK](global.json) 及 Windows 的 .NET Framework 程序集。`Build.ps1` 使用 SDK 中的 C# 编译器，从源码生成内嵌占位程序，再构建主程序，无需下载游戏资源。

```powershell
# 轻量版
.\Build.ps1

# 内置运行时版
.\Build.ps1 -SelfContained

# 两个版本、测试与发行包
.\Build.ps1 -All -RunTests -Package
```

产物位于 `artifacts/`。测试使用自建 EXE、模拟 Steam 配置和独立目录，不启动真实 Steam／游戏。验证结果见 [测试说明](docs/Testing.md)。GitHub Actions 对提交和 PR 构建，`v*` 标签发布实验预发行包。

## 项目结构

```text
src/ZZZSteamBridge.App       Windows Forms 界面和国服启动包装器
src/ZZZSteamBridge.Core      元数据、清单、账号配置、备份和恢复
src/SteamPlaceholder        约 4 KiB 的失败保护占位程序
tests/                      配置与启动生命周期隔离测试
docs/                       来源、测试和发行说明
```

## 许可证

[MIT](LICENSE)。游戏与 Steam 的商标、资源和服务归各自权利人所有，本仓库不分发游戏文件、Steamworks SDK 或 Better Endfield 代码。
