# 测试与验证状态

首个预览版的本地结果：**97 项隔离检查通过**，其中核心 66 项，启动器与发布 EXE 31 项。Windows x64 轻量版、内置运行时版均完成 Release 发布。公开发行构建不包含个人本机路径的调试符号。

## 已检查

- 元数据：正式 AppID、public BuildID、Windows 启动入口、64 位 depot manifest、缺字段及非法路径拒绝。
- 配置：单个明确账号、局部 LaunchOptions 修改、原值恢复、UTF-8 BOM／UTF-16 与其他文本保留。
- 安装清单：独立占位目录、归属、备份、正常 Steam 运行字段变化、更新元数据及未知字段保留。
- 恢复：已有安装保护、未知文件拒绝、并发保护、中断接入／中断修复恢复、目录联接及断开链接拒绝。
- 启动：国服标记、Steam 上下文、原始命令隔离、Unicode 和引号参数、原生工作目录、直接子进程等待、退出码与重复启动拒绝。
- 发布 EXE：用隔离模拟游戏测试成功启动链，不打开真实 Steam 或游戏。

## 复现

```powershell
.\Build.ps1 -All -RunTests -Package
```

构建脚本运行的核心测试为 `tests/ZZZSteamBridge.Core.Tests`；启动测试为 `tests/ZZZSteamBridge.Launcher.Tests`，传入 `tests/ZZZSteamBridge.FakeGame` 的编译产物及轻量发布 EXE。

模拟 AppID 环境只存在于测试子进程；测试结果不证明 Steam 服务端接受了游玩状态或时长。测试生成物位于忽略的构建／夹具目录，不上传真实账户配置和本地日志。

## 尚未完成的真实客户端验收

| 项目 | 当前状态 |
| --- | --- |
| 由正式 Steam 条目进入并登录国服 | 未测试 |
| 原生 Steam 游玩时长增长与停止 | 未测试 |
| 好友端显示正式游戏状态 | 未测试 |
| Shift+Tab Overlay、F12、Steam Input | 未测试 |
| Steam／国服正常升级后的持续可用性 | 未测试 |
| Steam 客户端始终无国际服下载 | 未测试，不能作永久保证 |
| 游戏脱离或重启后继进程 | 未测试，当前只等待直接子进程 |
| ARM64／x86 | 未提供发行包 |

开发过程中没有修改真实 Steam 游戏库、游戏目录或 Starward 项目。发现普通启动链不兼容时，不用注入或修改反作弊的方式补救。
