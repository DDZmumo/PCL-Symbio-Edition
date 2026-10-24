using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL;

/// <summary>
///     基岩版版本隔离（仅 GDK 便携版）。
///     参考 BedrockBoot：GDK 版把游戏数据放在 %APPDATA%\Minecraft Bedrock，隔离即把该目录指向版本自己的数据目录。
///     BedrockBoot 通过 PreloadCpp.dll 挂钩文件 API 实现；启动器没有注入能力，改为启动前用目录联接（junction）
///     把共享目录指向隔离目录，游戏退出后再还原。
/// </summary>
public static class ModBedrockIsolation
{
    /// <summary>共享数据目录被替换后，原目录的备份后缀。</summary>
    private const string SharedBackupSuffix = ".pcl-shared";

    private static readonly string[] SharedRootNames = ["Minecraft Bedrock", "Minecraft Bedrock Preview"];

    private static readonly string[] GameProcessNames = ["Minecraft.Windows", "Minecraft.Win10.DX11"];

    /// <summary>该 GDK 版本对应的共享数据目录（正式版与预览版不同）。</summary>
    public static string GetSharedRoot(string gameDirectory)
    {
        var isPreview = IsPreviewBuild(gameDirectory);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            isPreview ? "Minecraft Bedrock Preview" : "Minecraft Bedrock");
    }

    /// <summary>该版本的独立数据目录。</summary>
    public static string GetIsolationRoot(string versionId)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft", "bedrock", "isolation", versionId);
    }

    /// <summary>是否有基岩版正在运行。切换目录联接前必须确认没有进程占用。</summary>
    public static bool IsAnyGameRunning()
    {
        foreach (var name in GameProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) continue;
                foreach (var process in processes) process.Dispose();
                return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    ///     启用隔离：把共享目录改名备份，再建立指向该版本独立目录的目录联接。
    /// </summary>
    public static void Apply(string gameDirectory, string versionId)
    {
        // 上次异常退出可能残留联接，先还原再重新建立
        Restore();

        var shared = GetSharedRoot(gameDirectory);
        var backup = shared + SharedBackupSuffix;
        var isolation = GetIsolationRoot(versionId);

        Directory.CreateDirectory(isolation);

        if (Directory.Exists(shared) && !IsRedirected(shared))
            Directory.Move(shared, backup);

        CreateJunction(shared, isolation);
        ModBase.Log($"[Bedrock] 已启用版本隔离: {shared} -> {isolation}", ModBase.LogLevel.Normal);
    }

    /// <summary>
    ///     关闭隔离：删除目录联接并把备份的共享目录移回原位。
    ///     启动器上次异常退出时，也可以在启动时调用此方法做恢复。
    /// </summary>
    public static void Restore()
    {
        foreach (var name in SharedRootNames)
        {
            try
            {
                RestoreRoot(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name));
            }
            catch (Exception ex)
            {
                ModBase.Log($"[Bedrock] 还原版本隔离失败 ({name}): {ex.Message}", ModBase.LogLevel.Normal);
            }
        }
    }

    private static void RestoreRoot(string shared)
    {
        var backup = shared + SharedBackupSuffix;

        // 联接本身存在但目标已被删除时，Directory.Exists 会返回 false，这里直接按属性判断
        if (IsRedirected(shared))
        {
            Directory.Delete(shared);
            ModBase.Log($"[Bedrock] 已移除数据目录联接: {shared}", ModBase.LogLevel.Normal);
        }

        if (!Directory.Exists(backup)) return;

        if (Directory.Exists(shared))
        {
            // 共享目录已被重新创建：保留备份，避免覆盖用户数据
            ModBase.Log($"[Bedrock] 共享目录已存在，保留隔离前的备份: {backup}", ModBase.LogLevel.Normal);
            return;
        }

        Directory.Move(backup, shared);
        ModBase.Log($"[Bedrock] 已还原共享数据目录: {backup} -> {shared}", ModBase.LogLevel.Normal);
    }

    /// <summary>目录是否为目录联接 / 符号链接。</summary>
    private static bool IsRedirected(string path)
    {
        try
        {
            var attributes = new DirectoryInfo(path).Attributes;
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     创建目录联接。mklink /J 不需要管理员权限，也没有开启开发者模式的限制。
    /// </summary>
    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 cmd.exe 创建目录联接。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !IsRedirected(linkPath))
            throw new IOException(string.IsNullOrWhiteSpace(output)
                ? "创建目录联接失败。"
                : output.Trim());
    }

    /// <summary>从解包目录的 AppxManifest 判断是否为预览版（数据目录名不同）。</summary>
    private static bool IsPreviewBuild(string gameDirectory)
    {
        try
        {
            if (!Directory.Exists(gameDirectory)) return false;

            string? identityName = null;
            var manifest = Directory.GetFiles(gameDirectory, "AppxManifest.xml", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (manifest is not null)
            {
                var match = Regex.Match(File.ReadAllText(manifest),
                    "<Identity\\s[^>]*Name=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (match.Success) identityName = match.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(identityName))
                return identityName.Contains("Beta", StringComparison.OrdinalIgnoreCase) ||
                       identityName.Contains("Preview", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 读取版本标识失败，按正式版处理: {ex.Message}", ModBase.LogLevel.Normal);
        }

        return gameDirectory.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
               gameDirectory.Contains("beta", StringComparison.OrdinalIgnoreCase);
    }
}
