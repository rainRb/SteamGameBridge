# 构建、测试与 CI

目标：Windows x64、.NET 10。SDK 由 global.json 选择。主界面采用 Windows Forms，Core 使用 BCL；没有第三方 NuGet 库依赖。占位程序由 SDK 的 C# 编译器配合 Windows .NET Framework 引用程序集从源码生成，不分发游戏文件。

```powershell
.\Build.ps1
.\Build.ps1 -SelfContained
.\Build.ps1 -All -RunTests -Package
```

Build.ps1 构建轻量版及自包含单文件，测试实际发布 EXE，再按白名单打包 README、LICENSE、docs 和 BUILDINFO。自包含包另附实际使用的 .NET Runtime/Desktop 许可证与第三方声明。SHA256SUMS.txt 对应本次两个 ZIP；不包含 PDB、私有配置或测试夹具。脚本显式使用 UTF-8 读取 JSON，兼容 Windows PowerShell 5.1。

本地构建在 artifacts/test-results 保存证据。可用 `-OutputDirectory artifacts/verified` 选择项目内的新输出目录，避免覆盖正在使用的演示程序。网络不可用时 NuGet 漏洞数据查询可能产生 NU1900 警告；这不代表漏洞检查已通过，也不能代替构建或测试结果。没有为绕过该查询而关闭证书校验、改代理或关闭审计。

GitHub Actions 对分支 push、PR 和手动运行进行构建、测试和打包，上传两个版本及测试日志作为构建产物。正常源码提交不触发 Release。历史工作流的 v* 标签预发行通路保留，但本次 0.2.0-dev 任务不创建新标签，不运行新 Release 发布。

small 版本需要 .NET 10 Desktop Runtime x64；self-contained 版本包含运行时。两个版本功能相同。更换稳定启动组件前先恢复该游戏接入，再用新程序重新接入。
