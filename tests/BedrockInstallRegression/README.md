# 基岩版安装回归测试

使用 .NET 10 SDK，从仓库根目录执行（Windows、Linux 均可）：

```powershell
dotnet run --project tests/BedrockInstallRegression
```

此测试直接编译启动器的 `ModBedrockUwp.cs`，用测试宿主替换 PowerShell 和进程操作，
不会卸载应用、终止游戏进程或写入游戏目录，也不依赖额外的 NuGet 包。

覆盖版本目录校验、保留数据卸载失败和验证结果不明时中止、成功卸载后继续，
以及安装路径包含单引号或 PowerShell 变量字符的情况。
