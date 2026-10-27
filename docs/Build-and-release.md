# 构建、测试与 CI

目标：Windows x64、.NET 10。SDK 由 global.json 选择。主界面采用 Windows Forms，Core 使用 BCL；没有第三方 NuGet 库依赖。占位程序由 SDK 的 C# 编译器配合 Windows .NET Framework 引用程序集从源码生成，不分发游戏文件。

```powershell
.\Build.ps1
.\Build.ps1 -SelfContained
.\Build.ps1 -All -RunTests -Package
```

Build.ps1 构建轻量版及自包含单文件，测试实际发布 EXE。发布附件为 SteamGameBridge.exe（自包含版）及 SteamGameBridge-small.exe（轻量版），许可证和运行时声明内置在程序中。SHA256SUMS.txt 留在 CI 构建产物中用于上传前校验，不单独发布。

本地构建在 artifacts/test-results 保存证据。可用 `-OutputDirectory artifacts/verified` 选择项目内的新输出目录，避免覆盖正在使用的演示程序。网络不可用时 NuGet 漏洞数据查询可能产生 NU1900 警告；这不代表漏洞检查已通过，也不能代替构建或测试结果。没有为绕过该查询而关闭证书校验、改代理或关闭审计。

GitHub Actions 对分支 push、PR 和手动运行进行构建、测试和打包，上传两个 EXE、校验文件及测试日志作为构建产物。正常源码提交不触发 Release。推送 v* 标签时，工作流在构建和测试通过后发布，附两个 EXE。v0.2.0 这样的标签发布为正式版；包含预发布后缀的标签发布为预览版。

small 版本需要 .NET 10 Desktop Runtime x64；self-contained 版本包含运行时。两个版本功能相同。更换稳定启动组件前先恢复该游戏接入，再用新程序重新接入。
