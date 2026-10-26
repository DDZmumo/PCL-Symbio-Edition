using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageBedrockLeft
{
    private const string XboxAppAumid = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private static readonly string[] BedrockPackageFamilies =
    [
        "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "Microsoft.Minecraft_8wekyb3d8bbwe"
    ];

    private bool isLoad;

    public PageBedrockLeft()
    {
        InitializeComponent();
        Loaded += PageBedrockLeft_Loaded;
    }

    private void PageBedrockLeft_Loaded(object sender, RoutedEventArgs e)
    {
        // 每次回到该页面时都重新读取账号与状态，切换账号 / 安装版本后能及时反映
        RefreshAccount();
        RefreshStatus();

        if (isLoad) return;
        isLoad = true;

        // 首次进入时通知右侧页面执行一次完整检测
        if (ModMain.frmBedrockRight is not null)
            ModMain.frmBedrockRight.RefreshPage();

        RefreshStatus();
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.frmBedrockRight is not null)
            ModMain.frmBedrockRight.BtnLaunch_Click(sender, e);
    }

    #region 账号

    /// <summary>
    ///     读取当前 Windows 登录的 Xbox 账号信息。
    /// </summary>
    private void RefreshAccount()
    {
        string? gamertag = null;
        string? mail = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\XboxLive");
            gamertag = key?.GetValue("ModernGamertag") as string ?? key?.GetValue("Gamertag") as string;
            mail = key?.GetValue("UserName") as string;
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 读取 Xbox 账号失败：{ex.Message}", ModBase.LogLevel.Debug);
        }

        if (string.IsNullOrWhiteSpace(gamertag))
        {
            LabAvatar.Text = "?";
            LabGamertag.Text = Lang.Text("Bedrock.Account.NotLoggedIn");
            LabAccountMail.Text = Lang.Text("Bedrock.Account.SignInHint");
            BtnSwitchAccount.Text = Lang.Text("Bedrock.Account.SignIn");
            LabAccountHint.Text = Lang.Text("Bedrock.Account.Hint");
            return;
        }

        LabAvatar.Text = gamertag.Length > 1 && char.IsHighSurrogate(gamertag[0])
            ? gamertag[..2]
            : gamertag[..1].ToUpperInvariant();
        LabGamertag.Text = gamertag;
        LabAccountMail.Text = string.IsNullOrWhiteSpace(mail) ? Lang.Text("Bedrock.Account.MicrosoftAccount") : mail;
        BtnSwitchAccount.Text = Lang.Text("Bedrock.Account.Switch");
        LabAccountHint.Text = Lang.Text("Bedrock.Account.SwitchHint");
    }

    private void RefreshAccount_Click(object sender, EventArgs e)
    {
        RefreshAccount();
        RefreshStatus();
    }

    private void BtnSwitchAccount_Click(object sender, MouseButtonEventArgs e)
    {
        if (OpenXboxApp())
        {
            LabAccountHint.Text = Lang.Text("Bedrock.Account.SwitchHint");
            return;
        }

        LabAccountHint.Text = Lang.Text("Bedrock.Account.OpenXboxFailed");
        HintService.Hint(LabAccountHint.Text, HintType.Error);
        ModBase.OpenWebsite("https://account.microsoft.com/");
    }

    private static bool OpenXboxApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{XboxAppAumid}",
                UseShellExecute = true
            });
            ModBase.Log("[Bedrock] 已打开 Xbox 应用");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 打开 Xbox 应用失败：{ex.Message}", ModBase.LogLevel.Debug);
            return false;
        }
    }

    #endregion

    #region 快捷操作

    private void OpenEditor_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.frmBedrockRight is not { IsBedrockInstalled: true })
        {
            ModMain.MyMsgBox(Lang.Text("Bedrock.Editor.NotInstalled"), Lang.Text("Bedrock.Editor.Title"));
            return;
        }

        if (ModBedrockUwp.LaunchEditor()) return;
        ModMain.MyMsgBox(Lang.Text("Bedrock.Editor.Failed"), Lang.Text("Bedrock.Editor.Title"));
    }

    private void OpenSavesFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var dataDir = FindBedrockDataDir();
        if (dataDir is null)
        {
            ModMain.MyMsgBox(Lang.Text("Bedrock.Folder.NotFound"), Lang.Text("Bedrock.Shortcuts.OpenSaves"));
            return;
        }

        var saves = Path.Combine(dataDir, "games", "com.mojang", "minecraftWorlds");
        OpenFolder(Directory.Exists(saves) ? saves : dataDir, Lang.Text("Bedrock.Folder.Saves"));
    }

    private void OpenPackageFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft", "bedrock", "versions");
        if (!Directory.Exists(dir))
        {
            ModMain.MyMsgBox(Lang.Text("Bedrock.Folder.NoPackages"), Lang.Text("Bedrock.Shortcuts.OpenPackages"));
            return;
        }

        OpenFolder(dir, Lang.Text("Bedrock.Folder.Packages"));
    }

    private void OpenScreenshotFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var dataDir = FindBedrockDataDir();
        var shots = dataDir is null ? null : Path.Combine(dataDir, "screenshots");
        if (shots is not null && Directory.Exists(shots))
        {
            OpenFolder(shots, Lang.Text("Bedrock.Folder.Screenshots"));
            return;
        }

        // 回退到 Xbox Game Bar 的截图目录
        var gameBarShots = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        if (Directory.Exists(gameBarShots))
        {
            OpenFolder(gameBarShots, Lang.Text("Bedrock.Folder.Screenshots"));
            return;
        }

        ModMain.MyMsgBox(Lang.Text("Bedrock.Folder.NoScreenshots"), Lang.Text("Bedrock.Shortcuts.OpenScreenshots"));
    }

    private void OpenXboxApp_Click(object sender, MouseButtonEventArgs e)
    {
        if (OpenXboxApp()) return;
        ModMain.MyMsgBox(Lang.Text("Bedrock.Folder.XboxNotInstalled"), Lang.Text("Bedrock.Shortcuts.OpenXbox"));
    }

    private static string? FindBedrockDataDir()
    {
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        foreach (var family in BedrockPackageFamilies)
        {
            var dir = Path.Combine(packages, family, "LocalState");
            if (Directory.Exists(dir)) return dir;
        }

        return null;
    }

    private static void OpenFolder(string path, string name)
    {
        if (!Directory.Exists(path))
        {
            ModMain.MyMsgBox(Lang.Text("Bedrock.Folder.NotFoundPath", name, path),
                Lang.Text("Bedrock.Folder.OpenTitle", name));
            return;
        }

        ModBase.OpenExplorer(path.TrimEnd('\\') + "\\");
    }

    #endregion

    #region 状态

    public void RefreshStatus()
    {
        var right = ModMain.frmBedrockRight;
        if (right is null)
        {
            // 右侧页面尚未创建时也要把状态重置为“未检测到”，避免显示上一次的残留信息
            LabVersionText.Text = Lang.Text("Bedrock.Status.NotFound");
            return;
        }

        var installed = right.IsBedrockInstalled;
        LabVersionText.Text = installed
            ? string.IsNullOrEmpty(right.BedrockVersion)
                ? Lang.Text("Bedrock.Status.Ready")
                : Lang.Text("Bedrock.Status.Version", right.BedrockVersion)
            : Lang.Text("Bedrock.Status.NotFound");
    }

    #endregion
}
