using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

// SE 基岩版的界面构造使用 CE 的卡片、列表和按钮；安装与启动流程留在主 partial 中。
public partial class PageBedrockRight
{
    /// <summary>用与 CE 实例页相同的列表项显示本地版本，完整包信息放在悬停提示中。</summary>
    private FrameworkElement BuildLocalCard(string versionId, string filePath)
    {
        var isInstalling = _installingVersions.Contains(versionId);
        var isGdk = IsGdkVersion(versionId);
        var gdkReady = isGdk && ModBedrockGdk.IsInstalled(
            ModBedrockUwp.GetVersionDirectory(Path.Combine(BedrockVersionsDir, "extracted"), versionId));
        var isRegistered = string.Equals(_registeredVersionId, versionId, StringComparison.OrdinalIgnoreCase);
        var isInstalled = !isInstalling && (gdkReady || isRegistered);
        var isSelected = string.Equals(Config.Launch.BedrockSelectedVersion, versionId, StringComparison.OrdinalIgnoreCase);
        var detailParts = new List<string>();
        try
        {
            detailParts.Add(FormatFileSize(new FileInfo(filePath).Length));
        }
        catch
        {
            // 包可能刚被移动或删除，仍保留版本行以便重新安装或清理。
        }
        if (gdkReady)
            detailParts.Add(ModBedrockLoader.GetDisplayName(ModBedrockLoader.GetLoader(versionId)));

        var tags = new List<string>
        {
            isGdk ? "GDK" : "UWP",
            Lang.Text(isInstalling ? "Bedrock.Local.Tag.Installing"
                : isInstalled ? "Bedrock.Local.Tag.Installed" : "Bedrock.Local.Tag.Downloaded")
        };
        if (isSelected) tags.Add(Lang.Text("Bedrock.Local.Tag.Selected"));

        var row = new MyListItem
        {
            Title = versionId,
            Info = string.Join("  ·  ", detailParts),
            Tags = tags,
            Height = 42,
            SvgIcon = isGdk ? "lucide/gamepad-2" : "lucide/box",
            Type = MyListItem.CheckType.Clickable,
            Tag = versionId,
            IsEnabled = !isInstalling,
            ToolTip = filePath
        };
        if (isInstalled)
        {
            var packageVersion = TryReadExtractedIdentityVersion(versionId);
            if (!string.IsNullOrEmpty(packageVersion))
                row.ToolTip = Lang.Text("Bedrock.Status.Version", packageVersion) + Environment.NewLine + filePath;
        }

        var launch = new MyIconButton
        {
            SvgIcon = "lucide/play",
            IsEnabled = !isInstalling,
            ToolTip = isGdk ? Lang.Text("Bedrock.Local.LaunchGdk")
                : Lang.Text(isInstalled ? "Bedrock.Local.Launch" : "Bedrock.Local.SwitchLaunch")
        };
        launch.Click += (_, _) => LaunchLocalVersion(versionId);
        var buttons = new List<MyIconButton> { launch };
        if (gdkReady)
        {
            var loader = new MyIconButton
            {
                SvgIcon = "lucide/puzzle",
                Tag = versionId,
                IsEnabled = !isInstalling,
                ToolTip = Lang.Text("Bedrock.Loader.Tooltip",
                    ModBedrockLoader.GetDisplayName(ModBedrockLoader.GetLoader(versionId)))
            };
            loader.Click += LocalLoader_Click;
            buttons.Add(loader);
        }

        var install = new MyIconButton
        {
            SvgIcon = "lucide/package-plus",
            Tag = versionId,
            IsEnabled = !isInstalling,
            ToolTip = Lang.Text(isInstalling ? "Bedrock.Local.Installing"
                : isInstalled ? "Bedrock.Local.Reinstall" : "Bedrock.Local.Install")
        };
        install.Click += LocalInstall_Click;
        buttons.Add(install);
        var delete = new MyIconButton
        {
            SvgIcon = "lucide/trash-2",
            Theme = MyIconButton.Themes.Red,
            Tag = versionId,
            IsEnabled = !isInstalling,
            ToolTip = Lang.Text(isInstalled ? "Bedrock.Local.DeleteRegistered" : "Bedrock.Local.Delete")
        };
        delete.Click += LocalDelete_Click;
        buttons.Add(delete);
        row.Buttons = buttons;
        // 保留空白处的安装/重新安装操作；启动和加载器仍由各自的按钮触发。
        row.Click += (_, _) => LocalInstall_Click(install, EventArgs.Empty);
        return row;
    }

    /// <summary>
    ///     构建一个版本系列的折叠卡片。展开状态的卡片需要手动安装内容。
    /// </summary>
    private MyCard BuildSeriesCard(string series, List<JsonObject> items, bool expand)
    {
        var content = new StackPanel
        {
            Tag = "install",
            Margin = new Thickness(20, MyCard.SwapedHeight, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform()
        };

        void Install(StackPanel stack)
        {
            foreach (var version in items)
                stack.Children.Add(BuildVersionRow(version));
        }

        var card = new MyCard
        {
            Title = $"{series} ({items.Count})",
            Margin = new Thickness(0, 0, 0, 15),
            SwapControl = content,
            InstallMethod = Install
        };
        card.Children.Add(content);

        if (expand)
            card.StackInstall();
        else
            card.IsSwapped = true;

        return card;
    }

    private MyListItem BuildVersionRow(JsonObject version)
    {
        var versionId = (string)version["id"] ?? Lang.Text("Bedrock.List.UnknownVersion");
        var releaseDate = (string)version["releaseDate"] ?? "";
        var buildType = (string)version["buildType"] ?? "UWP";
        var available = (bool?)version["available"] ?? true;
        var webAvailable = (bool?)version["webAvailable"] ?? available;

        _versionAvailable[versionId] = available;
        _versionBuildTypes[versionId] = buildType;

        MyIconButton? downloadBtn = null;
        if (available)
        {
            downloadBtn = new MyIconButton
            {
                SvgIcon = "lucide/download",
                Tag = versionId
            };
            downloadBtn.Click += BedrockVersionDownload_Click;
            _versionButtons[versionId] = downloadBtn;
        }

        var tags = new List<string> { buildType };
        var row = new MyListItem
        {
            Title = versionId,
            Info = releaseDate,
            Tags = tags,
            Type = available ? MyListItem.CheckType.Clickable : MyListItem.CheckType.None,
            Height = 42,
            SvgIcon = "lucide/package",
            Tag = versionId,
            ToolTip = buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase)
                ? Lang.Text("Bedrock.List.GdkPortable") : null
        };

        if (available)
        {
            row.Buttons = new[] { downloadBtn! };
            row.Click += (_, _) => BedrockVersionDownload_Click(downloadBtn!, EventArgs.Empty);
        }
        else if (webAvailable)
        {
            // 应用内没有下载源，但 mcappx 站上有该版本：改为常显的「网站下载」文字入口，点击打开对应系列页
            var webBtn = new MyTextButton
            {
                Text = Lang.Text("Bedrock.Version.WebDownload"),
                FontSize = 12,
                Height = 18,
                Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
                ToolTip = Lang.Text("Bedrock.Version.WebDownloadTip"),
                Tag = versionId
            };
            webBtn.Click += BedrockVersionWebDownload_Click;
            MyListItem.SetColumnSpan(webBtn, 10);
            MyListItem.SetRowSpan(webBtn, 10);
            row.MinPaddingRight = 78;
            row.Children.Add(webBtn);
        }
        else
        {
            // 应用内没有下载源，mcappx 站上也没有这个版本：只标注无下载途径，不提供任何入口
            var noWayText = new TextBlock
            {
                Text = Lang.Text("Bedrock.List.NoSource"),
                FontSize = 12,
                Foreground = (Brush)FindResource("ColorBrushGray3"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
                IsHitTestVisible = false
            };
            MyListItem.SetColumnSpan(noWayText, 10);
            MyListItem.SetRowSpan(noWayText, 10);
            row.MinPaddingRight = 78;
            row.Children.Add(noWayText);
        }

        UpdateVersionButtonState(versionId);
        return row;
    }

}
