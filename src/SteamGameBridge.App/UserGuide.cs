namespace SteamGameBridge.App;

internal static class UserGuide
{
    internal const string Text = """
        接入游戏

        1. 新增游戏，选择预设，按需修改 Steam AppID。
        2. 选择实际游戏 EXE，也可点击“自动检索”。
        3. 在 Steam 设置中选择游戏库和账号。
        4. 选择启动方式并填写参数。
        5. 获取元数据，选择 Steam 启动入口。
        6. 完全退出 Steam，点击“一键接入”，确认原启动项的处理方式。
        7. 打开 Steam，登录相同账号，从游戏条目启动。

        启动方式

        直接启动：选择游戏 EXE。
        官方启动器：分别选择启动器和实际游戏 EXE。
        第三方包装器：选择已安装的包装器，填写参数模板。
        Starward 启动：支持绝区零和崩坏3，游戏参数在 Starward 中设置。

        包装器占位符

        {game}            游戏 EXE 路径
        {gameArgs}        游戏参数
        {steamCommand}    Steam 原始启动命令

        Better Endfield 示例：
        --hide-console --game {game} -- {gameArgs} --steam-command {steamCommand}

        Starward 时长

        选择绝区零或崩坏3后，Starward 设置显示在游戏设置页。
        可选择“使用 Starward 启动”，也可只开启“使用 Starward 记录时长”。
        未注册协议时，可选择 Starward EXE。

        维护与恢复

        关闭窗口后，已启用的维护在托盘继续运行；从托盘菜单退出可停止。
        修改已接入游戏的路径、参数或账号前，先恢复接入。
        Steam 出现安装、更新或验证任务时，暂停任务，退出 Steam 后检查。
        不要用“验证完整性”修复占位安装。
        游戏需要管理员权限时，先退出 Steam，再以管理员身份打开 Steam。
        """;
}
