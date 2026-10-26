using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public partial class PageBedrockRight
{
    private void InstallGdkPackage(string filePath, string versionId)
    {
        Task.Run(() =>
        {
            try
            {
                ModBase.Log($"[Bedrock] 开始解包 GDK 版本: {filePath}", ModBase.LogLevel.Normal);

                var lastPercent = -1;
                var extractDir = ModBedrockInstall.InstallGdk(filePath, BedrockVersionsDir, versionId, (_, index, total) =>
                {
                    if (total <= 0) return;
                    var percent = index * 100 / total;
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    ModBase.RunInUi(() => LabDetail.Text =
                        Lang.Text("Bedrock.Gdk.ExtractingFile", versionId, percent));
                });

                if (!ModBedrockGdk.IsInstalled(extractDir))
                    throw new FileNotFoundException(Lang.Text("Bedrock.Gdk.ExecutableMissing"));

                ModBase.Log($"[Bedrock] GDK 版本已解包: {versionId}",
                    ModBase.LogLevel.Normal,
                    userSummary: Lang.Text("Bedrock.Gdk.ExtractSuccessSummary", versionId));

                ModBase.RunInUi(() =>
                {
                    IsBedrockInstalled = true;
                    BedrockInstallPath = extractDir;
                    BedrockVersion = versionId;
                    _isStorePackage = false;
                    _isGdkPortable = true;
                    BtnUpdate.Visibility = Visibility.Collapsed;
                    ShapeStatusDot.Fill = (Brush)FindResource("ColorBrush3");
                    LabStatus.Text = Lang.Text("Bedrock.Gdk.Extracted");
                    LabDetail.Text = Lang.Text("Bedrock.Status.CopyPathHint");
                    LabVersion.Text = Lang.Text("Bedrock.Status.Version", versionId);
                    BtnLaunch.IsEnabled = true;
                    BtnLaunch.Text = Lang.Text("Bedrock.Action.Launch");
                    RefreshLocalVersions();
                    ModMain.frmBedrockLeft?.RefreshStatus();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "GDK 版本解包失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Bedrock.Gdk.ExtractFailed", versionId, ex.Message));

                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.Failed");
                    LabDetail.Text = Lang.Text("Bedrock.Install.FailedHint", filePath);
                    BtnLaunch.IsEnabled = true;
                });
            }
            finally
            {
                ModBase.RunInUi(() =>
                {
                    _installingVersions.Remove(versionId);
                    RefreshLocalVersions();
                });
            }
        });
    }

    private void InstallBedrockPackage(string filePath, string versionId)
    {
        Task.Run(async () =>
        {
            try
            {
                ModBase.Log($"[Bedrock] 开始安装: {filePath}", ModBase.LogLevel.Normal);

                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.CheckingEnvironment");
                    LabDetail.Text = Lang.Text("Bedrock.Install.CheckingDependencies");
                });

                // 结束正在运行的基岩版，避免文件被占用
                ModBedrockUwp.StopMinecraftProcesses();

                // 参考 BedrockBoot：安装前补齐 VCLibs / .NET Native 等 UWP 依赖
                await ModBedrockUwp.EnsureDependenciesAsync().ConfigureAwait(false);

                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.Installing");
                    LabDetail.Text = Lang.Text("Bedrock.Install.InstallingVersion", versionId);
                });

                try
                {
                    // Manifest 已被改写为更高的版本号，可直接覆盖注册已安装的新版本（降级）
                    InstallWithRegister(filePath, versionId);
                }
                catch (Exception firstEx)
                {
                    if (ModBedrockUwp.GetInstalledPackage() is null)
                        throw;

                    // 覆盖注册失败时，保留存档卸载后重新安装
                    ModBase.Log($"[Bedrock] 覆盖注册失败，改为保留存档卸载后重试: {firstEx.Message}",
                        ModBase.LogLevel.Normal);

                    ModBase.RunInUi(() =>
                    {
                        LabStatus.Text = Lang.Text("Bedrock.Install.Replacing");
                        LabDetail.Text = Lang.Text("Bedrock.Install.PreservingData");
                    });

                    ModBedrockUwp.UninstallPreservingData();
                    InstallWithRegister(filePath, versionId);
                }

                ModBase.Log($"[Bedrock] 安装完成: {versionId}",
                    ModBase.LogLevel.Normal,
                    userSummary: Lang.Text("Bedrock.Install.SuccessSummary", versionId));

                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.Success");
                    LabDetail.Text = Lang.Text("Bedrock.Install.InstalledVersion", versionId);
                    BtnLaunch.IsEnabled = true;
                    BtnLaunch.Text = Lang.Text("Bedrock.Action.Launch");
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "基岩版安装失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Bedrock.Install.FailedSummary", versionId, ex.Message));

                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.Failed");
                    LabDetail.Text = Lang.Text("Bedrock.Install.FailedHint", filePath);
                });
            }
            finally
            {
                _installingVersions.Remove(versionId);
                ModBase.RunInUi(() =>
                {
                    RefreshLocalVersions();
                    DetectBedrockInstallation();
                });
            }
        });
    }

    /// <summary>
    ///     参考 BedrockBoot：使用开发模式注册安装基岩版包。
    ///     开发模式（Add-AppxPackage -Register）安装的包不会被 Microsoft Store 自动更新。
    ///     对所有 UWP 格式（.appx / .msix / .appxbundle / .msixbundle）统一处理：解压 → 删除签名 → 注册。
    ///     GDK 的 .msixvc 走 <see cref="InstallGdkPackage" />，不会进入这里。
    /// </summary>
    /// <param name="filePath">安装包文件路径</param>
    /// <param name="versionId">版本标识</param>
    private void InstallWithRegister(string filePath, string versionId)
    {
        var extractDir = ModBedrockUwp.GetVersionDirectory(Path.Combine(BedrockVersionsDir, "extracted"), versionId);

        ModBase.Log($"[Bedrock] 解压安装包到: {extractDir}", ModBase.LogLevel.Normal);

        // 清理旧解压目录
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        // 解压安装包（.appx / .msix / .msixvc / .appxbundle 本质都是 ZIP 格式）
        try
        {
            ZipFile.ExtractToDirectory(filePath, extractDir);
        }
        catch (Exception ex)
        {
            // 解压失败说明是加密格式（如加密的 .msixvc），尝试直接安装
            ModBase.Log($"[Bedrock] 解压失败，尝试直接安装: {ex.Message}", ModBase.LogLevel.Normal);
            _ = ModBase.ShellAndGetOutputChecked("powershell.exe",
                ModBedrockUwp.GetAppxInstallArguments(filePath),
                timeout: 300000);
            return;
        }

        // 验证 AppxManifest.xml 存在；.appxbundle / .msixbundle 解压后只有内部包，需要再解一层
        var manifestPath = Path.Combine(extractDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            var innerPackage = Directory.GetFiles(extractDir, "*.appx")
                .Concat(Directory.GetFiles(extractDir, "*.msix"))
                .OrderByDescending(f => Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (innerPackage is null)
                throw new Exception(Lang.Text("Bedrock.Install.ManifestMissing"));

            var innerDir = Path.Combine(extractDir, "inner");
            if (Directory.Exists(innerDir)) Directory.Delete(innerDir, true);
            Directory.CreateDirectory(innerDir);
            ZipFile.ExtractToDirectory(innerPackage, innerDir);

            manifestPath = Path.Combine(innerDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                throw new Exception(Lang.Text("Bedrock.Install.ManifestMissing"));

            extractDir = innerDir;
            ModBase.Log($"[Bedrock] 已从包束中取出内部安装包: {Path.GetFileName(innerPackage)}",
                ModBase.LogLevel.Normal);
        }

        // 参考 BedrockBoot：删除签名文件，阻断 Microsoft Store 更新
        var sigPath = Path.Combine(extractDir, "AppxSignature.p7x");
        if (File.Exists(sigPath))
        {
            File.Delete(sigPath);
            ModBase.Log("[Bedrock] 已删除 AppxSignature.p7x，阻断 Store 自动更新", ModBase.LogLevel.Normal);
        }
        var blockMapPath = Path.Combine(extractDir, "AppxBlockMap.xml");
        if (File.Exists(blockMapPath)) File.Delete(blockMapPath);

        // 参考 BedrockLauncher.Core：改写 Manifest（提升版本号、TrustLevel、runFullTrust、SCCD、Extensions）
        // 旧版本包直接注册会因版本号更低、缺少受信任能力而失败
        ModBedrockUwp.EditManifest(extractDir, $"Minecraft {versionId}");
        ModBase.Log("[Bedrock] 已改写 AppxManifest.xml 以支持旧版本安装", ModBase.LogLevel.Normal);

        // 补全 .msixvc 增量更新包可能缺失的资源文件
        var splashPath = Path.Combine(extractDir, "MCSplashScreen.png");
        if (!File.Exists(splashPath))
        {
            ModBase.Log("[Bedrock] 补全缺失的 MCSplashScreen.png", ModBase.LogLevel.Normal);
            CreateMinimalPng(splashPath);
        }

        // 参考 BedrockBoot：检查开发者模式，-Register 需要开发者模式或旁加载模式
        if (!IsDeveloperModeEnabled())
        {
            ModBase.Log("[Bedrock] 开发者模式未启用，尝试自动启用...", ModBase.LogLevel.Normal);
            if (!EnableDeveloperMode())
            {
                ModBase.Log("[Bedrock] 无法启用开发者模式（需要管理员权限），回退到直接安装", ModBase.LogLevel.Normal);
                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Install.DeveloperModeHint");
                    LabDetail.Text = Lang.Text("Bedrock.Install.DeveloperModeDetail");
                });
            }
        }

        // 方式1：使用 Add-AppxPackage -Register 注册（开发模式，Store 不会自动更新）
        if (IsDeveloperModeEnabled())
        {
            try
            {
                _ = ModBase.ShellAndGetOutputChecked("powershell.exe",
                    ModBedrockUwp.GetAppxInstallArguments(manifestPath, register: true), timeout: 300000);
                var identity = BedrockPackageRegistration.ReadIdentity(manifestPath);
                if (identity is null || !IsRegisteredFromDirectory(identity, extractDir))
                    throw new IOException("系统未确认此基岩版已注册到目标目录。");
                ModBase.Log("[Bedrock] 开发模式注册成功，Store 不会自动更新此版本", ModBase.LogLevel.Normal);
                return;
            }
            catch (ModBase.ProcessExecutionException error) when (!error.Result.TimedOut)
            {
                ModBase.Log($"[Bedrock] -Register 失败（退出码 {error.Result.ExitCode}），尝试重新打包为 .appx。",
                    ModBase.LogLevel.Normal);
            }
        }

        // 方式2：重新打包为 .appx 后安装（回退方案）
        var appxPath = Path.Combine(Path.GetDirectoryName(filePath)!,
            $"Minecraft_Bedrock_{versionId}.pcl-{Guid.NewGuid():N}.appx");
        try
        {
            ZipFile.CreateFromDirectory(extractDir, appxPath);
            _ = ModBase.ShellAndGetOutputChecked("powershell.exe",
                ModBedrockUwp.GetAppxInstallArguments(appxPath), timeout: 300000);
            var identity = BedrockPackageRegistration.ReadIdentity(manifestPath);
            if (identity is null || ModBedrockUwp.GetInstalledPackage(identity) is null)
                throw new IOException("系统未确认基岩版已成功安装。");
        }
        finally
        {
            try { if (File.Exists(appxPath)) File.Delete(appxPath); }
            catch (IOException error) { ModBase.Log("[Bedrock] 临时重打包文件保留: " + error.Message); }
        }
    }

    #region 开发者模式检测（参考 BedrockBoot DeveloperModeHelper）

    /// <summary>
    ///     参考 BedrockBoot DeveloperModeHelper.IsDeveloperModeViaPowerShell：
    ///     检查注册表确认开发者模式或旁加载模式是否已启用。
    ///     开发者模式（AllowDevelopmentWithoutDevLicense = 1）或
    ///     旁加载模式（AllowAllTrustedApps = 1）均可支持 Add-AppxPackage -Register。
    /// </summary>
    private static bool IsDeveloperModeEnabled()
    {
        try
        {
            var result = ModBase.ShellAndGetOutputChecked("powershell.exe",
                "-NoProfile -Command \"" +
                "$paths = @('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock', " +
                "'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Appx'); " +
                "foreach ($p in $paths) { " +
                "  try { $v = Get-ItemProperty -Path $p -Name 'AllowDevelopmentWithoutDevLicense' -ErrorAction Stop; " +
                "    if ($v.AllowDevelopmentWithoutDevLicense -eq 1) { Write-Output 'true'; exit } " +
                "  } catch { } " +
                "}; " +
                "try { $v = Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Name 'AllowAllTrustedApps' -ErrorAction Stop; " +
                "  if ($v.AllowAllTrustedApps -eq 1) { Write-Output 'true'; exit } " +
                "} catch { }; " +
                "Write-Output 'false'\"",
                timeout: 10000);

            return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 开发者模式检测失败: {ex.Message}", ModBase.LogLevel.Normal);
            return false;
        }
    }

    /// <summary>
    ///     尝试启用开发者模式（需要管理员权限）。
    ///     参考 BedrockBoot CoreOptions.IsAutoOpenDevelopment。
    /// </summary>
    private static bool EnableDeveloperMode()
    {
        try
        {
            var result = ModBase.ShellAndGetOutputChecked("powershell.exe",
                "-NoProfile -Command \"" +
                "try { " +
                "  New-Item -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Force -ErrorAction SilentlyContinue | Out-Null; " +
                "  Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -ErrorAction Stop; " +
                "  Write-Output 'true' " +
                "} catch { Write-Output 'false' }\"",
                timeout: 10000);

            return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 启用开发者模式失败: {ex.Message}", ModBase.LogLevel.Normal);
            return false;
        }
    }

    #endregion

    /// <summary>
    ///     创建一个最小的 1x1 白色 PNG 文件，用于补全 .msixvc 增量更新包缺失的启动画面。
    /// </summary>
    private static void CreateMinimalPng(string path)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8BQDwAFeAFK5RqavAAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }


}
