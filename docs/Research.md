# 设计依据与实现边界

核查更新：2026-10-23。SteamGameBridge 是独立 .NET 10 Windows Forms 工具，Core 使用 BCL；没有修改 Starward 工程、安装目录、配置或数据库。实现多游戏 ACF/VDF 管理、正常进程创建和只读进程查询，源码为 MIT。

## 来源与许可证

- 正式 Steam 商店页：[终末地 4732690](https://store.steampowered.com/app/4732690/)、[绝区零 4162040](https://store.steampowered.com/app/4162040/)、[鸣潮 3513350](https://store.steampowered.com/app/3513350/)、[崩坏3 1671200](https://store.steampowered.com/app/1671200/)。AppID 预设只提供默认值，不授予许可，不构成本地国服受到 Steam 官方支持的证明。
- [Better Endfield 官方仓库](https://github.com/Dr-hydra/Better-Endfield)，此前核对源码提交 `1038af190572511e64fe42053f74fb7d19931771` 的 [LICENSE](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/LICENSE) 为 AGPL-3.0。该项目包含 .NET/WinUI 桌面界面和原生组件；本项目仅独立参考公开行为，没有复制其源码或分发注入模块。
- [Better Endfield Steam 功能说明](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/docs/host/STEAM_CN_LAUNCH.md)说明安装 ACF、占位目录、启动项和恢复，并明确 `AutoUpdateBehavior=1` 无法保证 Steam 此后永不下载。
- [Better Endfield Overlay 链式启动修复说明](https://github.com/Dr-hydra/Better-Endfield/blob/1038af190572511e64fe42053f74fb7d19931771/docs/workspace/releases/4.0.6/RELEASE_4_0_6.md)涉及进程注入。本项目不采用该实现，因此不承诺相同 Overlay 效果。
- [Valve Steam Overlay 文档](https://partner.steamgames.com/doc/features/overlay)提供正常 Steam 启动的依据，实际各游戏是否可用需单独实测。
- [Starward 官方仓库](https://github.com/Scighost/Starward)。本次重新读取了 [UrlProtocolService.cs](https://github.com/Scighost/Starward/blob/main/src/Starward/Features/UrlProtocol/UrlProtocolService.cs)：公开 `starward://playtime/<biz>?pid=...` 协议接受真实 PID，然后交给其自身的计时服务。[PlayTimeRecordService.cs](https://github.com/Scighost/Starward/blob/main/src/Starward/Features/PlayTime/PlayTimeRecordService.cs)负责对应进程和 PID 去重。我们仅调用已有协议，不复制或分发 Starward 实现。
- [Snap Hutao Remastered 的联动处理器](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered/blob/7a9df50ea688ad9ae9740ff286e0457ee3cb27af/src/Snap.Hutao.Remastered/Snap.Hutao.Remastered/Service/Game/Launching/Handler/LaunchExecutionStarwardPlayTimeStatisticsHandler.cs)采用游戏已运行后查询 Starward 协议并通知的流程，文件头标注 MIT。本项目独立采用这一调用顺序，并通过现有 PID 查询参数明确交接实际游戏进程，不通过 Starward 再启动游戏。

公开第三方 Steam 元数据来自 api.steamcmd.net，可导入同格式 JSON。必须包含所选 AppID 的实际名称、安装目录、Windows EXE、公共 BuildID 和 Depot Manifest。缺失或异常时拒绝接入，不伪造版本号或下载状态来源。

## 本次实现

1. 本地身份、Steam AppID 和 Starward biz 分离。一个 Steam 安装/AppID 使用唯一活动记录，与游戏库和账号选择分开校验。
2. 只在 Steam 与对应游戏退出后写配置；跨进程的 Steam 安装锁串行保护共享 VDF，修改精确到所选 AppID 的 LaunchOptions。
3. 先保存原配置及恢复日志，再写隔离占位文件和 ACF。拒绝已存在的清单、目标目录、下载目录、未知文件、重解析路径和与本地游戏重叠的写入位置。
4. 三种旧启动项选择均经过原值和最终参数预览。原命令的 EXE/链/Steam 占位符不能自动当作纯游戏参数。
5. 直接启动正常等待实际游戏；官方启动器和包装器等待完整路径、创建时间和会话均符合的进程。只读查询句柄用于等待和退出码，不做注入、挂起或内存写入。
6. `%command%` 仅在用户明确填写 `{steamCommand}` 时转交选定启动器或包装器；直接游戏不执行它，不混入国际服参数。
7. 游戏确认后尝试通知 Starward，协议或手动 EXE 失效不结束游戏。发送无记录成功回执；本地状态不会虚报 Starward 已记录。
8. 修复仅管理自有内容；恢复只改目标启动项、只删除归属仍可确认的文件。其他游戏和未知内容保留。元数据变更、权限问题或外部冲突清楚报告。
9. 新目录 SteamGameBridge 不迁移旧工具数据。升级前先恢复旧接入，再重新接入。

## 需要真实验收的边界

Steam 安装清单和继承的 AppID 环境不能证明 Steam 服务端累计时长、好友状态或 Overlay 成功。Starward 协议发送成功不能证明它已在数据库完成记录。假进程可验证路径和 PID 交接，不能替代内核反作弊、正常 UAC、官方启动器或真实更新兼容性。

不部署注入模块，不伪造官方父进程，不修改游戏或反作弊受保护文件，不修改 Starward，不伪造 Steamworks 状态、成就或时长。普通启动链不能做到的功能应标记为未验证或失败，不添加绕过手段。
