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
    public void LaunchBedrock()
    {
        if (_launching) return;
        if (string.IsNullOrEmpty(BedrockInstallPath))
        {
            DetectBedrockInstallation();
            return;
        }

        BeginLaunchProgress(Lang.Text("Bedrock.Status.CallingSystem"));

        var requestedPath = BedrockInstallPath;
        var requestedVersion = BedrockVersion;
        var isPortable = _isGdkPortable;

        // 启动同样需要调用 PowerShell 与注册包，放到后台线程执行
        _ = Task.Run(() =>
        {
            Process? process = null;
            BedrockIsolationSession? isolationSession = null;
            var watchingExit = false;
            try
            {
                // GDK 便携版不经过系统包注册，直接运行解包目录里的主程序，可与其他版本同时运行
                if (isPortable)
                {
                    var installPath = requestedPath!;

                    // 版本隔离：启动前把共享数据目录指向该版本自己的目录，游戏退出后还原
                    if (Config.Launch.BedrockVersionIsolation)
                    {
                        SetLaunchStage(0.08, Lang.Text("Bedrock.Launch.Stage.Isolation"));
                        isolationSession = ModBedrockIsolation.BeginSession(installPath,
                            requestedVersion ?? Path.GetFileName(installPath));
                    }

                    SetLaunchStage(0.25, Lang.Text("Bedrock.Launch.Stage.Starting"));

                    // 原版加载器优先尝试注册式启动：注册后的包具备包身份与完整授权，
                    // 不会像裸跑那样被当作试用版而要求「解锁完整游戏」；
                    // LeviLamina 需要注入 DLL，只能直接运行解包目录里的主程序。
                    // preload 目录内有 DLL 时同样必须裸跑，注册式启动由 explorer 拉起进程，无法注入。
                    var loader = ModBedrockLoader.GetLoader(requestedVersion);
                    var hasPreload = ModBedrockMods.GetPreloadFiles(installPath).Length > 0;
                    if (!hasPreload && loader == ModBedrockLoader.Vanilla &&
                        TryLaunchGdkRegistered(installPath, requestedVersion))
                    {
                        SetLaunchStage(0.6, Lang.Text("Bedrock.Launch.Stage.WaitingWindow"));
                    }
                    else
                    {
                        process = ModBedrockGdk.Launch(installPath, null, requestedVersion)
                                  ?? throw new FileNotFoundException(Lang.Text("Bedrock.Gdk.ExecutableMissing"));
                        SetLaunchStage(0.55, Lang.Text("Bedrock.Launch.Stage.WaitingWindow"));
                    }

                    // 版本隔离的还原放到后台：等游戏进程退出后再恢复共享目录
                    if (isolationSession is not null)
                    {
                        ModBedrockIsolation.RestoreAfterGameExit(isolationSession, process);
                        watchingExit = true;
                    }

                    if (!WaitForGameWindow(process, TimeSpan.FromSeconds(60)))
                    {
                        // 进程在出窗口前就退出了，说明启动确实失败，不能报成功
                        if (process is { HasExited: true })
                            throw new InvalidOperationException(Lang.Text("Bedrock.Launch.Exited", process.ExitCode));
                        ModBase.Log("[Bedrock] 60 秒内未检测到游戏窗口，游戏可能仍在启动",
                            ModBase.LogLevel.Hint);
                    }

                    ModBase.Log($"[Bedrock] 正在启动 GDK 版本: {installPath}", ModBase.LogLevel.Normal);
                    SetLaunchStage(0.9);
                    EndLaunchProgress(true, Lang.Text("Bedrock.Status.Launched"), installPath);
                    return;
                }

                // 优先用 shell:AppsFolder 启动已注册的包：旧版 Manifest 被设为 AppListEntry=none，
                // minecraft:// 协议可能被商店版或其他版本抢占
                SetLaunchStage(0.4, Lang.Text("Bedrock.Launch.Stage.Starting"));
                if (ModBedrockUwp.LaunchPackage())
                {
                    ModBase.Log("[Bedrock] 正在启动 Minecraft Bedrock Edition...");
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "minecraft://",
                        UseShellExecute = true
                    });
                    ModBase.Log("[Bedrock] 正在通过 minecraft:// 启动 Minecraft Bedrock Edition...");
                }

                SetLaunchStage(0.6, Lang.Text("Bedrock.Launch.Stage.WaitingWindow"));
                if (!WaitForGameWindow(null, TimeSpan.FromSeconds(60)))
                    ModBase.Log("[Bedrock] 60 秒内未检测到游戏窗口，游戏可能仍在启动",
                        ModBase.LogLevel.Hint);

                SetLaunchStage(0.9);
                EndLaunchProgress(true, Lang.Text("Bedrock.Status.Launched"),
                    BedrockInstallPath ?? "Minecraft Bedrock Edition");
            }
            catch (Exception ex)
            {
                ModBase.Log($"[Bedrock] 启动失败: {ex.Message}", ModBase.LogLevel.Hint,
                    Lang.Text("Bedrock.Launch.Error"));
                EndLaunchProgress(false, Lang.Text("Bedrock.Status.LaunchFailed"), ex.Message);
            }
            finally
            {
                process?.Dispose();
                if (isolationSession is not null && !watchingExit)
                    ModBedrockIsolation.Restore(isolationSession);
            }
        });
    }

    #region 注册式启动（GDK）

    /// <summary>
    ///     以注册方式启动 GDK 版本，等价于 BedrockBoot 的 RegisterPackageAsync + 开发部署。
    ///     直接运行 Minecraft.Windows.exe 没有包身份与商店授权，游戏会按试用版处理并要求解锁完整游戏；
    ///     注册后的包以开发部署身份运行，可直接进入完整游戏。无法注册时返回 false，由调用方退回直接启动。
    /// </summary>
    private bool TryLaunchGdkRegistered(string extractDir, string? versionId)
    {
        try
        {
            var manifestPath = Path.Combine(extractDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                ModBase.Log("[Bedrock] 解包目录内没有 AppxManifest.xml，改用直接启动", ModBase.LogLevel.Hint);
                return false;
            }

            if (!IsDeveloperModeEnabled())
            {
                ModBase.Log("[Bedrock] 未开启开发者模式，无法以注册方式启动，本次直接运行（可能被当作试用版）",
                    ModBase.LogLevel.Hint, userSummary: Lang.Text("Bedrock.Register.NoDeveloperMode"));
                return false;
            }

            var identityName = ReadManifestIdentity(manifestPath);
            if (string.IsNullOrEmpty(identityName))
            {
                ModBase.Log("[Bedrock] 无法从清单读取包名，改用直接启动", ModBase.LogLevel.Hint);
                return false;
            }

            // 已按该目录注册过就直接启动，避免每次启动都跑一次耗时的 PowerShell 注册
            if (!IsRegisteredFromDirectory(identityName, extractDir))
            {
                SetLaunchStage(0.35, Lang.Text("Bedrock.Launch.Stage.Register"));

                // 改写过的清单与旧签名不匹配，注册前按 BedrockBoot 的方式删除签名文件
                var signaturePath = Path.Combine(extractDir, "AppxSignature.p7x");
                if (File.Exists(signaturePath)) File.Delete(signaturePath);

                if (!IsManifestEdited(manifestPath))
                {
                    ModBedrockUwp.EditManifest(extractDir, $"Minecraft {versionId}");
                    ModBase.Log("[Bedrock] 已改写 GDK 清单以支持注册启动", ModBase.LogLevel.Normal);
                }

                _ = ModBase.ShellAndGetOutputChecked("powershell.exe",
                    ModBedrockUwp.GetAppxInstallArguments(manifestPath, register: true),
                    timeout: 300000);
                if (!IsRegisteredFromDirectory(identityName, extractDir))
                    throw new IOException("系统未确认基岩版已注册到目标目录。");

                ModBase.Log($"[Bedrock] 已注册版本 {versionId}，以开发部署身份启动可避免「解锁完整游戏」提示",
                    ModBase.LogLevel.Normal);
            }

            if (!ModBedrockUwp.LaunchPackage(identityName))
            {
                ModBase.Log("[Bedrock] 已注册但无法通过 AppsFolder 启动，改用直接启动", ModBase.LogLevel.Hint);
                return false;
            }

            ModBase.Log($"[Bedrock] 正在以注册方式启动 GDK 版本 {versionId}", ModBase.LogLevel.Normal);
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "以注册方式启动 GDK 版本失败，改用直接启动", ModBase.LogLevel.Hint);
            return false;
        }
    }

    /// <summary>该目录是否正是当前注册的包，是的话无需重复注册。</summary>
    private static bool IsRegisteredFromDirectory(string packageName, string directory)
    {
        try
        {
            return ModBedrockRegistration.IsRegisteredFromDirectory(packageName, directory);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadManifestIdentity(string manifestPath)
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(manifestPath),
                "<Identity\\s[^>]*Name=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清单是否已被改写（改写后 AppListEntry 会被设为 none，版本号也会变化）。</summary>
    private static bool IsManifestEdited(string manifestPath)
    {
        try
        {
            return Regex.IsMatch(File.ReadAllText(manifestPath),
                "AppListEntry\\s*=\\s*\"none\"", RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
