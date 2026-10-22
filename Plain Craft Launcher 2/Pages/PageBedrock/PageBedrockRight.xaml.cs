using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using PCL.Core.App.Localization;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public partial class PageBedrockRight
{
    private bool isLoad;
    private readonly HashSet<string> _downloadingVersions = [];
    private readonly HashSet<string> _installingVersions = [];
    private readonly HashSet<string> _localVersionIds = [];
    private readonly Dictionary<string, MyIconButton> _versionButtons = [];
    private readonly Dictionary<string, bool> _versionAvailable = [];
    private readonly Dictionary<string, string> _versionBuildTypes = [];
    private JsonArray? _versionCache;
    private string _versionFilter = "all";
    private string? _installedVersionId;

    private static string BedrockVersionsDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".minecraft", "bedrock", "versions");

    public PageBedrockRight()
    {
        InitializeComponent();
        FilterAll.Check += (_, _) =>
        {
            _versionFilter = "all";
            ApplyVersionFilter();
        };
        FilterUwp.Check += (_, _) =>
        {
            _versionFilter = "uwp";
            ApplyVersionFilter();
        };
        FilterGdk.Check += (_, _) =>
        {
            _versionFilter = "gdk";
            ApplyVersionFilter();
        };
        SearchBox.TextChanged += (_, _) => ApplyVersionFilter();
        Loaded += PageBedrockRight_Loaded;
    }

    private void PageBedrockRight_Loaded(object sender, RoutedEventArgs e)
    {
        if (isLoad) return;
        isLoad = true;

        DetectBedrockInstallation();
        RefreshLocalVersions();
        LoadVersionList();
    }

    #region 启动检测

    public void RefreshPage()
    {
        DetectBedrockInstallation();
        RefreshLocalVersions();
        LoadVersionList();
    }

    public bool IsBedrockInstalled { get; private set; }
    public string? BedrockVersion { get; private set; }
    public string? BedrockInstallPath { get; private set; }

    private bool _detecting;
    private bool _isStorePackage;

    public void DetectBedrockInstallation()
    {
        if (_detecting) return;
        _detecting = true;

        // 检测需要调用 PowerShell 并遍历目录，放到后台线程执行，避免界面卡住导致按钮看起来点不动
        _ = Task.Run(() =>
        {
            var bedrockPath = FindBedrockInstallation();
            var (bedrockVersion, signatureKind) = GetBedrockPackageInfo();
            Dispatcher.Invoke(() =>
            {
                _detecting = false;
                ApplyInstallationState(bedrockPath, bedrockVersion, signatureKind);
            });
        });
    }

    private void ApplyInstallationState(string? bedrockPath, string? bedrockVersion, string? signatureKind)
    {
        IsBedrockInstalled = !string.IsNullOrEmpty(bedrockPath);
        BedrockVersion = bedrockVersion;
        BedrockInstallPath = bedrockPath;
        _isStorePackage = string.Equals(signatureKind, "Store", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(bedrockPath))
        {
            ShapeStatusDot.Fill = (Brush)FindResource("ColorBrush3");
            LabStatus.Text = Lang.Text("Bedrock.Status.Detected");
            LabDetail.Text = bedrockPath;
            LabVersion.Text = string.IsNullOrEmpty(bedrockVersion)
                ? "Minecraft Bedrock Edition"
                : Lang.Text("Bedrock.Status.Version", bedrockVersion);
            LabSelectedVersion.Text = Lang.Text("Bedrock.Status.Ready");
            BtnLaunch.IsEnabled = true;
            BtnLaunch.Text = Lang.Text("Bedrock.Action.Launch");
        }
        else
        {
            ShapeStatusDot.Fill = (Brush)FindResource("ColorBrushGray4");
            LabStatus.Text = Lang.Text("Bedrock.Status.NotFound");
            LabDetail.Text = Lang.Text("Bedrock.Status.DownloadOrImportHint");
            LabVersion.Text = Lang.Text("Bedrock.Status.NotInstalled");
            LabSelectedVersion.Text = Lang.Text("Bedrock.Status.NotFound");
            BtnLaunch.IsEnabled = true;
            BtnLaunch.Text = Lang.Text("Bedrock.Action.Download");
        }

        // 检测结果会影响「已安装」标记，重新渲染本地版本行
        RefreshLocalVersions();
        RefreshUpdateHint();
        // 同步刷新左侧页面的状态，否则左侧会一直停留在检测前的“未检测到”
        ModMain.frmBedrockLeft?.RefreshStatus();
    }

    /// <summary>
    ///     版本列表加载完成（或重新检测）后，比较已安装包版本与最新正式版。
    ///     商店版包由 Microsoft Store 负责更新，商店安装的包无法由启动器侧载升级。
    /// </summary>
    private void RefreshUpdateHint()
    {
        if (BtnUpdate is null) return;

        if (!IsBedrockInstalled || !_isStorePackage || _versionCache is null)
        {
            BtnUpdate.Visibility = Visibility.Collapsed;
            return;
        }

        JsonObject? latest = null;
        foreach (var node in _versionCache)
        {
            if (node is JsonObject version && version["packageVersion"] is not null)
            {
                latest = version;
                break;
            }
        }

        var latestPackageVersion = latest is null ? "" : (string)latest["packageVersion"] ?? "";
        if (string.IsNullOrEmpty(latestPackageVersion) ||
            string.IsNullOrEmpty(BedrockVersion) ||
            !Version.TryParse(latestPackageVersion, out var latestVer) ||
            !Version.TryParse(BedrockVersion, out var currentVer) ||
            currentVer >= latestVer)
        {
            BtnUpdate.Visibility = Visibility.Collapsed;
            return;
        }

        var latestGameVersion = (string)latest["id"] ?? latestPackageVersion;
        BtnUpdate.Text = Lang.Text("Bedrock.Action.UpdateToVersion", latestGameVersion);
        BtnUpdate.Visibility = Visibility.Visible;
        LabVersion.Text = Lang.Text("Bedrock.Status.VersionWithLatest", BedrockVersion, latestGameVersion);
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 商店版基岩版由 Microsoft Store 分发，只能通过商店更新
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-windows-store://downloadsandupdates",
                UseShellExecute = true
            });
            LabDetail.Text = Lang.Text("Bedrock.Store.Opened");
            ModBase.Log("[Bedrock] 正在打开 Microsoft Store 更新页面");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开 Microsoft Store 失败", ModBase.LogLevel.Hint,
                Lang.Text("Bedrock.Store.CannotOpen"));
        }
    }

    private static (string? Version, string? SignatureKind) GetBedrockPackageInfo()
    {
        try
        {
            var output = ModBase.ShellAndGetOutput("powershell.exe",
                "-NoProfile -Command \"$p = Get-AppxPackage -Name 'Microsoft.Minecraft*' | Select-Object -First 1; if ($p) { $p.Version.ToString() + '|' + $p.SignatureKind }\"",
                timeout: 10000);
            if (!string.IsNullOrEmpty(output) && !output.Contains("Exception"))
            {
                var trimmed = output.Trim();
                if (trimmed.Length == 0) return (null, null);

                var parts = trimmed.Split('|');
                var version = parts[0].Trim();
                var signatureKind = parts.Length > 1 ? parts[1].Trim() : "";
                return (version.Length == 0 ? null : version,
                    signatureKind.Length == 0 ? null : signatureKind);
            }
        }
        catch { }
        return (null, null);
    }

    private static string? FindBedrockInstallation()
    {
        try
        {
            var output = ModBase.ShellAndGetOutput("powershell.exe",
                "-NoProfile -Command \"Get-AppxPackage -Name Microsoft.Minecraft* | Select-Object -ExpandProperty InstallLocation\"",
                timeout: 10000);
            if (!string.IsNullOrEmpty(output) && !output.Contains("Exception"))
            {
                var trimmed = output.Trim();
                if (Directory.Exists(trimmed))
                    return trimmed;
            }
        }
        catch { }

        try
        {
            // 新版为 Minecraft.Windows.exe，1.16 及更早的旧版为 Minecraft.Win10.DX11.exe
            foreach (var exeName in new[] { "Minecraft.Windows.exe", "Minecraft.Win10.DX11.exe" })
            {
                var path = SearchForFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), exeName);
                if (!string.IsNullOrEmpty(path)) return path;

                path = SearchForFile(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), exeName);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { }

        try
        {
            var path = SearchForDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.mojang");
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch { }

        return null;
    }

    private static string? SearchForFile(string rootDir, string fileName)
    {
        try
        {
            foreach (var file in Directory.GetFiles(rootDir, fileName, SearchOption.AllDirectories))
                return file;
        }
        catch { }
        return null;
    }

    private static string? SearchForDirectory(string rootDir, string dirName)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(rootDir, dirName, SearchOption.AllDirectories))
                return dir;
        }
        catch { }
        return null;
    }

    public void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (!IsBedrockInstalled)
        {
            // 尚未检测到安装（或检测仍在后台进行），刷新一次检测并引导到下载列表
            DetectBedrockInstallation();
            PanVersionList.BringIntoView();
            return;
        }
        LaunchBedrock();
    }

    public void LaunchBedrock()
    {
        var launchingText = Lang.Text("Bedrock.Status.Launching");
        LabStatus.Text = launchingText;
        LabDetail.Text = Lang.Text("Bedrock.Status.CallingSystem");

        // 启动同样需要调用 PowerShell 读取包信息，放到后台线程执行
        _ = Task.Run(() =>
        {
            try
            {
                // 优先用 shell:AppsFolder 启动已注册的包：旧版 Manifest 被设为 AppListEntry=none，
                // minecraft:// 协议可能被商店版或其他版本抢占
                if (ModBedrockUwp.LaunchPackage())
                {
                    ModBase.Log("[Bedrock] 正在启动 Minecraft Bedrock Edition...");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "minecraft://",
                    UseShellExecute = true
                });

                ModBase.Log("[Bedrock] 正在通过 minecraft:// 启动 Minecraft Bedrock Edition...");
            }
            catch (Exception ex)
            {
                ModBase.Log($"[Bedrock] 启动失败: {ex.Message}", ModBase.LogLevel.Hint,
                    Lang.Text("Bedrock.Launch.Error"));
                Dispatcher.Invoke(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Status.LaunchFailed");
                    LabDetail.Text = ex.Message;
                });
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    if (LabStatus.Text != launchingText) return;
                    LabStatus.Text = Lang.Text("Bedrock.Status.Detected");
                    LabDetail.Text = BedrockInstallPath ?? "Minecraft Bedrock Edition";
                });
            }
        });
    }

    #endregion

    #region 本地版本管理

    private void RefreshLocalVersions()
    {
        PanLocalVersions.Children.Clear();
        _localVersionIds.Clear();
        UpdateInstalledVersionId();

        var items = new List<(string Id, string FilePath)>();
        if (Directory.Exists(BedrockVersionsDir))
        {
            foreach (var dir in Directory.GetDirectories(BedrockVersionsDir))
            {
                // 跳过安装时的临时解压目录
                if (Path.GetFileName(dir).Equals("extracted", StringComparison.OrdinalIgnoreCase)) continue;

                var files = GetPackageFiles(dir);
                if (files.Count == 0) continue;

                items.Add((Path.GetFileName(dir), files[0]));
            }
        }

        // 与在线列表保持一致：按版本号降序
        items.Sort((a, b) => ModDownload.CompareBedrockVersions(b.Id, a.Id));

        foreach (var item in items)
        {
            _localVersionIds.Add(item.Id);
            PanLocalVersions.Children.Add(BuildLocalRow(item.Id, item.FilePath));
        }

        if (items.Count == 0)
            PanLocalVersions.Children.Add(CreateHintText(Lang.Text("Bedrock.Local.Empty")));

        RefreshVersionRowStates();
    }

    /// <summary>
    ///     判断当前安装的基岩版是否由启动器从某个本地版本注册而来。
    ///     注册时解压目录为 extracted\&lt;版本号&gt;（包束会多一层 inner）。
    /// </summary>
    private void UpdateInstalledVersionId()
    {
        _installedVersionId = null;
        if (string.IsNullOrEmpty(BedrockInstallPath)) return;

        try
        {
            var root = Path.GetFullPath(Path.Combine(BedrockVersionsDir, "extracted"))
                .TrimEnd(Path.DirectorySeparatorChar);
            var installPath = Path.GetFullPath(BedrockInstallPath).TrimEnd(Path.DirectorySeparatorChar);
            if (!installPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;

            var relative = installPath[(root.Length + 1)..];
            var segment = relative.Split(Path.DirectorySeparatorChar)[0];
            if (!string.IsNullOrEmpty(segment))
                _installedVersionId = segment;
        }
        catch
        {
            // 路径异常时忽略，仅影响「已安装」标记
        }
    }

    private MyListItem BuildLocalRow(string versionId, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var fileSize = "";
        try
        {
            fileSize = FormatFileSize(new FileInfo(filePath).Length);
        }
        catch { }

        var isInstalling = _installingVersions.Contains(versionId);
        var isInstalled = !isInstalling &&
                          string.Equals(_installedVersionId, versionId, StringComparison.OrdinalIgnoreCase);

        var installBtn = new MyIconButton
        {
            SvgIcon = "lucide/package-plus",
            Tag = versionId,
            IsEnabled = !isInstalling
        };
        installBtn.ToolTip = isInstalling
            ? Lang.Text("Bedrock.Local.Installing")
            : isInstalled
                ? Lang.Text("Bedrock.Local.Reinstall")
                : Lang.Text("Bedrock.Local.Install");
        installBtn.Click += LocalInstall_Click;

        var buttons = new List<MyIconButton>();
        if (isInstalled)
        {
            var launchBtn = new MyIconButton
            {
                SvgIcon = "lucide/play",
                Tag = versionId
            };
            launchBtn.ToolTip = Lang.Text("Bedrock.Local.Launch");
            launchBtn.Click += LocalLaunch_Click;
            buttons.Add(launchBtn);
        }

        buttons.Add(installBtn);

        var deleteBtn = new MyIconButton
        {
            SvgIcon = "lucide/trash-2",
            Theme = MyIconButton.Themes.Red,
            Tag = versionId,
            IsEnabled = !isInstalling
        };
        deleteBtn.ToolTip = Lang.Text("Bedrock.Local.Delete");
        deleteBtn.Click += LocalDelete_Click;
        buttons.Add(deleteBtn);

        var row = new MyListItem
        {
            Title = versionId,
            Info = $"{fileSize}  ·  {fileName}",
            Tags = isInstalling
                ? Lang.Text("Bedrock.Local.Tag.Installing")
                : isInstalled
                    ? Lang.Text("Bedrock.Local.Tag.Installed")
                    : Lang.Text("Bedrock.Local.Tag.Downloaded"),
            Type = MyListItem.CheckType.Clickable,
            Height = 42,
            SvgIcon = "lucide/box",
            Tag = versionId
        };
        row.Buttons = buttons.ToArray();
        row.Click += (_, _) => LocalInstall_Click(installBtn, EventArgs.Empty);
        return row;
    }

    private void LocalLaunch_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        ModBase.Log($"[Bedrock] 正在启动本地版本 {versionId}", ModBase.LogLevel.Normal);
        LaunchBedrock();
    }

    private void LocalInstall_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        if (_installingVersions.Contains(versionId))
            return;

        var dir = Path.Combine(BedrockVersionsDir, versionId);
        var allFiles = GetPackageFiles(dir);
        if (allFiles.Count == 0) return;

        StartInstall(allFiles[0], versionId);
    }

    private void StartInstall(string filePath, string versionId)
    {
        if (_installingVersions.Contains(versionId))
            return;

        if (Path.GetExtension(filePath).Equals(".msixvc", StringComparison.OrdinalIgnoreCase))
        {
            ModBase.Log("[Bedrock] .msixvc 需要内容密钥，无法侧载安装",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Bedrock.Version.GdkUnsupported"));
            return;
        }

        _installingVersions.Add(versionId);
        RefreshLocalVersions();

        LabStatus.Text = Lang.Text("Bedrock.Install.Installing");
        LabDetail.Text = Lang.Text("Bedrock.Install.InstallingVersion", versionId);
        BtnLaunch.IsEnabled = false;

        InstallBedrockPackage(filePath, versionId);
    }

    private void LocalDelete_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        var dir = Path.Combine(BedrockVersionsDir, versionId);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);

            ModBase.Log($"[Bedrock] 已删除本地版本: {versionId}",
                ModBase.LogLevel.Normal,
                userSummary: Lang.Text("Bedrock.Local.Deleted", versionId));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "删除本地版本失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.Local.DeleteFailed", ex.Message));
        }

        RefreshLocalVersions();
    }

    /// <summary>
    ///     导入手动下载的旧版本安装包：读取包内 Identity 版本号 → 复制到版本目录 → 直接开始安装。
    /// </summary>
    private void ImportPackage_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Lang.Text("Bedrock.Import.DialogTitle"),
            Filter = Lang.Text("Bedrock.Import.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var sourcePath = dialog.FileName;
        if (Path.GetExtension(sourcePath).Equals(".msixvc", StringComparison.OrdinalIgnoreCase))
        {
            ModBase.Log("[Bedrock] .msixvc 需要内容密钥，无法侧载安装",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Bedrock.Version.GdkUnsupported"));
            return;
        }

        LabStatus.Text = Lang.Text("Bedrock.Import.Importing");
        LabDetail.Text = Path.GetFileName(sourcePath);
        BtnImport.IsEnabled = false;

        Task.Run(() =>
        {
            try
            {
                var identityVersion = TryReadPackageIdentityVersion(sourcePath);
                var versionId = ResolveImportedVersionId(identityVersion, sourcePath);

                var destDir = Path.Combine(BedrockVersionsDir, versionId);
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));
                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath),
                        StringComparison.OrdinalIgnoreCase))
                    File.Copy(sourcePath, destPath, true);

                ModBase.Log($"[Bedrock] 已导入安装包: {versionId} -> {destPath}",
                    ModBase.LogLevel.Normal,
                    userSummary: Lang.Text("Bedrock.Import.Success", versionId));

                ModBase.RunInUi(() =>
                {
                    RefreshLocalVersions();
                    StartInstall(destPath, versionId);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "导入基岩版安装包失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Bedrock.Import.FailedWithMessage", ex.Message));
                ModBase.RunInUi(() =>
                {
                    LabStatus.Text = Lang.Text("Bedrock.Import.Failed");
                    LabDetail.Text = ex.Message;
                });
            }
            finally
            {
                ModBase.RunInUi(() => BtnImport.IsEnabled = true);
            }
        });
    }

    /// <summary>
    ///     从安装包（或包束）的清单中读取 Identity 的 Version。
    /// </summary>
    private static string? TryReadPackageIdentityVersion(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var manifestName in new[] { "AppxManifest.xml", "AppxBundleManifest.xml" })
            {
                var entry = archive.GetEntry(manifestName);
                if (entry is null) continue;

                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                var identity = doc.Root?.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Identity");
                var version = identity?.Attribute("Version")?.Value;
                if (!string.IsNullOrEmpty(version)) return version;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log($"[Bedrock] 读取安装包版本信息失败: {ex.Message}", ModBase.LogLevel.Normal);
        }

        return null;
    }

    /// <summary>
    ///     用安装包的 Identity 版本号匹配 mcappx 的版本数据，得到可读的版本名。
    ///     匹配失败时退回 Identity 版本号或文件名。
    /// </summary>
    private string ResolveImportedVersionId(string? identityVersion, string sourcePath)
    {
        if (!string.IsNullOrEmpty(identityVersion))
        {
            var normalized = identityVersion.Trim();
            // Identity 固定为四段，mcappx 的 ID 省略了末尾的 .0
            if (normalized.Split('.').Length == 4 && normalized.EndsWith(".0", StringComparison.Ordinal))
                normalized = normalized[..^2];

            if (_versionCache is not null)
            {
                foreach (var node in _versionCache)
                {
                    if (node is not JsonObject version) continue;
                    if (string.Equals((string)version["packageVersion"], normalized,
                            StringComparison.OrdinalIgnoreCase))
                        return (string)version["id"] ?? normalized;
                }
            }

            return normalized;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileNameWithoutExtension(sourcePath), @"\d+\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : Path.GetFileNameWithoutExtension(sourcePath);
    }

    private static TextBlock CreateHintText(string text)
    {
        var hint = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("ColorBrushGray3"),
            Margin = new Thickness(5d, 8d, 5d, 8d)
        };
        return hint;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static readonly string[] BedrockPackageExtensions = [".appx", ".msix", ".appxbundle", ".msixbundle", ".msixvc"];

    /// <summary>
    ///     获取目录内的基岩版安装包文件（按扩展名过滤）。
    /// </summary>
    private static List<string> GetPackageFiles(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory)
            .Where(f => BedrockPackageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>
    ///     判断 URL 末段是否为安装包文件名。FE3 解析出的地址无扩展名，不能覆盖默认文件名。
    /// </summary>
    private static bool IsPackageFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        return BedrockPackageExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
    }

    #endregion

    #region 在线版本下载

    private void LoadVersionList()
    {
        try
        {
            var versionListLoader = new ModLoader.LoaderTask<string, JsonObject>(
                "BedrockVersionList", ModDownload.DlBedrockListMain);
            versionListLoader.PreviewFinish += _ =>
            {
                ModBase.RunInUiWait(() => BuildVersionList(versionListLoader.output));
            };
            versionListLoader.Start();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "加载基岩版版本列表失败", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.List.LoadFailed"));
        }
    }

    private void BuildVersionList(JsonObject versionData)
    {
        try
        {
            _versionCache = versionData["versions"] as JsonArray;
            ApplyVersionFilter();
            RefreshUpdateHint();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "构建基岩版版本列表失败", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.List.BuildFailed"));
        }
    }

    /// <summary>
    ///     按搜索框关键词与 UWP/GDK 过滤器重建版本列表，并按系列分组为可折叠卡片。
    /// </summary>
    private void ApplyVersionFilter()
    {
        if (PanVersionList is null) return;

        PanVersionList.Children.Clear();
        _versionButtons.Clear();
        _versionAvailable.Clear();
        _versionBuildTypes.Clear();

        if (_versionCache is null || _versionCache.Count == 0)
        {
            LabVersionSummary.Text = Lang.Text("Bedrock.List.Empty");
            PanVersionList.Children.Add(CreateHintText(Lang.Text("Bedrock.List.EmptyHint")));
            return;
        }

        var keyword = (SearchBox.Text ?? "").Trim();

        // 数据已按版本号降序排列，这里顺序分组即可
        var grouped = new List<(string Series, List<JsonObject> Items)>();
        var total = 0;

        foreach (var node in _versionCache)
        {
            if (node is not JsonObject version) continue;

            var versionId = (string)version["id"] ?? "";
            var buildType = (string)version["buildType"] ?? "UWP";

            if (keyword.Length > 0 && !versionId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_versionFilter == "uwp" && !buildType.Equals("UWP", StringComparison.OrdinalIgnoreCase))
                continue;
            if (_versionFilter == "gdk" && !buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase))
                continue;

            var series = (string)version["series"] ?? Lang.Text("Bedrock.List.OtherSeries");
            if (grouped.Count == 0 || grouped[^1].Series != series)
                grouped.Add((series, new List<JsonObject>()));
            grouped[^1].Items.Add(version);
            total++;
        }

        LabVersionSummary.Text = keyword.Length > 0
            ? Lang.Text("Bedrock.List.Found", total)
            : Lang.Text("Bedrock.List.Total", total);

        if (total == 0)
        {
            LabVersionSummary.Text = Lang.Text("Bedrock.List.NoMatch");
            PanVersionList.Children.Add(CreateHintText(Lang.Text("Bedrock.List.NoMatchHint")));
            return;
        }

        // 搜索时展开全部结果，否则只展开最新的一组
        var expandAll = keyword.Length > 0;
        for (var i = 0; i < grouped.Count; i++)
            PanVersionList.Children.Add(BuildSeriesCard(grouped[i].Series, grouped[i].Items, expandAll || i == 0));
    }

    /// <summary>
    ///     构建一个版本系列的折叠卡片。展开状态的卡片需要手动安装内容。
    /// </summary>
    private MyCard BuildSeriesCard(string series, List<JsonObject> items, bool expand)
    {
        var content = new StackPanel
        {
            Tag = "install",
            Margin = new Thickness(15, 36, 15, 0)
        };

        void Install(StackPanel stack)
        {
            foreach (var version in items)
                stack.Children.Add(BuildVersionRow(version));
        }

        var card = new MyCard
        {
            Title = $"{series}  ({items.Count})",
            Margin = new Thickness(0, 0, 0, 8),
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

        _versionAvailable[versionId] = available;
        _versionBuildTypes[versionId] = buildType;

        var downloadBtn = new MyIconButton
        {
            SvgIcon = "lucide/download",
            Tag = versionId
        };
        downloadBtn.Click += BedrockVersionDownload_Click;
        _versionButtons[versionId] = downloadBtn;

        var tags = new List<string> { buildType };
        if (!available) tags.Add(Lang.Text("Bedrock.List.NoSource"));
        // GDK 版本的 .msixvc 需要内容密钥，启动器无法侧载安装
        if (buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase)) tags.Add(Lang.Text("Bedrock.List.SideloadUnsupported"));

        var row = new MyListItem
        {
            Title = versionId,
            Info = releaseDate,
            Tags = tags,
            Type = MyListItem.CheckType.Clickable,
            Height = 42,
            SvgIcon = "lucide/package",
            Tag = versionId
        };
        row.Buttons = new[] { downloadBtn };
        row.Click += (_, _) => BedrockVersionDownload_Click(downloadBtn, EventArgs.Empty);

        UpdateVersionButtonState(versionId);
        return row;
    }

    /// <summary>
    ///     刷新单个版本行右侧按钮的图标、可用性与提示。
    /// </summary>
    private void UpdateVersionButtonState(string versionId)
    {
        if (!_versionButtons.TryGetValue(versionId, out var btn)) return;

        var available = !_versionAvailable.TryGetValue(versionId, out var value) || value;
        var buildType = _versionBuildTypes.TryGetValue(versionId, out var type) ? type : "UWP";
        var isGdk = buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase);

        if (_downloadingVersions.Contains(versionId))
        {
            btn.SvgIcon = "lucide/refresh-cw";
            btn.IsEnabled = false;
            btn.ToolTip = Lang.Text("Bedrock.Version.Downloading");
        }
        else if (_installingVersions.Contains(versionId))
        {
            btn.SvgIcon = "lucide/refresh-cw";
            btn.IsEnabled = false;
            btn.ToolTip = Lang.Text("Bedrock.Local.Installing");
        }
        else if (_localVersionIds.Contains(versionId))
        {
            btn.SvgIcon = "lucide/circle-check";
            btn.IsEnabled = false;
            btn.ToolTip = isGdk
                ? Lang.Text("Bedrock.Version.DownloadedGdk")
                : Lang.Text("Bedrock.Version.Downloaded");
        }
        else if (!available)
        {
            btn.SvgIcon = "lucide/external-link";
            btn.IsEnabled = true;
            btn.ToolTip = Lang.Text("Bedrock.Version.NoSourceTooltip");
        }
        else
        {
            btn.SvgIcon = "lucide/download";
            btn.IsEnabled = true;
            btn.ToolTip = Lang.Text("Bedrock.Version.DownloadTooltip");
        }
    }

    private void RefreshVersionRowStates()
    {
        foreach (var versionId in _versionButtons.Keys.ToList())
            UpdateVersionButtonState(versionId);
    }

    private async void BedrockVersionDownload_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        if (_downloadingVersions.Contains(versionId))
            return;

        var buildType = ModDownload.GetBedrockBuildType(versionId);

        _downloadingVersions.Add(versionId);
        UpdateVersionButtonState(versionId);

        try
        {
            var downloadInfo = await ModDownload.GetBedrockDownloadUrlsAsync(versionId);
            if (downloadInfo is not null)
            {
                var (urls, md5, resolvedBuildType) = downloadInfo.Value;
                if (urls.Count > 0)
                {
                    StartBedrockDownload(urls, md5, versionId, resolvedBuildType);
                    return;
                }
            }

            _downloadingVersions.Remove(versionId);
            UpdateVersionButtonState(versionId);

            var webUrl = $"https://www.mcappx.com/bedrock/{versionId}/";
            ModBase.OpenWebsite(webUrl);
            LabDetail.Text = Lang.Text("Bedrock.Version.PageOpened", versionId);
            ModBase.Log(
                $"[Bedrock] 无法解析 {versionId} 的下载地址（{buildType}），打开网页: {webUrl}",
                ModBase.LogLevel.Normal,
                userSummary: Lang.Text("Bedrock.Download.OpeningPage", versionId));
        }
        catch (Exception ex)
        {
            _downloadingVersions.Remove(versionId);
            UpdateVersionButtonState(versionId);
            ModBase.Log(ex, "解析基岩版下载地址失败", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.Download.ResolveFailedSummary", versionId, ex.Message));
        }
    }

    private void StartBedrockDownload(List<string> urls, string? md5, string versionId, string buildType)
    {
        try
        {
            var saveDir = Path.Combine(BedrockVersionsDir, versionId);
            Directory.CreateDirectory(saveDir);

            // GDK 版是 .msixvc，UWP 版是 .appx
            var extension = buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase) ? ".msixvc" : ".appx";
            var fileName = "Minecraft_Bedrock_" + versionId + extension;

            // FE3 解析出的地址不带扩展名，此时保留默认文件名
            try
            {
                var uri = new Uri(urls[0]);
                var seg = uri.Segments[^1].TrimEnd('/');
                if (IsPackageFileName(seg))
                    fileName = seg;
            }
            catch { }

            var savePath = Path.Combine(saveDir, fileName);

            var checker = md5 is not null
                ? new ModBase.FileChecker(hash: md5, canUseExistsFile: true)
                : new ModBase.FileChecker(canUseExistsFile: true);

            var downloadFile = new DownloadFile(urls, savePath, checker);

            var loaderName = Lang.Text("Bedrock.Download.Title") + " " + versionId;
            var downloadLoader = new LoaderDownload(
                loaderName,
                new List<DownloadFile> { downloadFile }
            );

            var combo = new ModLoader.LoaderCombo<string>(
                loaderName,
                new List<ModLoader.LoaderBase> { downloadLoader }
            );

            _downloadingVersions.Add(versionId);
            UpdateVersionButtonState(versionId);

            // 下载完成后刷新本地版本列表
            combo.OnStateChangedUi += (_, newState, _) =>
            {
                if (newState is ModBase.LoadState.Finished)
                {
                    _downloadingVersions.Remove(versionId);
                    ModBase.RunInUi(() =>
                    {
                        RefreshLocalVersions();
                        LabStatus.Text = Lang.Text("Bedrock.Version.DownloadComplete");
                        LabDetail.Text = buildType.Equals("GDK", StringComparison.OrdinalIgnoreCase)
                            ? Lang.Text("Bedrock.Version.DownloadSavedGdk", versionId)
                            : Lang.Text("Bedrock.Version.DownloadSaved", versionId);
                    });
                }
                else if (newState is ModBase.LoadState.Failed or ModBase.LoadState.Aborted)
                {
                    _downloadingVersions.Remove(versionId);
                    ModBase.RunInUi(() => UpdateVersionButtonState(versionId));
                }
            };

            ModLoader.LoaderTaskbarAdd(combo);
            combo.Start();

            ModBase.Log(
                $"[Bedrock] 下载任务已添加: {versionId} ({urls.Count} 个 CDN 源)",
                ModBase.LogLevel.Normal,
                userSummary: Lang.Text("Bedrock.Download.Started", versionId, urls.Count));
        }
        catch (Exception ex)
        {
            _downloadingVersions.Remove(versionId);
            UpdateVersionButtonState(versionId);
            ModBase.Log(ex, "基岩版下载失败", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.Download.FailedSummary", ex.Message));
        }
    }

    #endregion

    #region 安装

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
    ///     对所有格式（.appx / .msix / .msixvc / .appxbundle）统一处理：解压 → 删除签名 → 注册。
    /// </summary>
    /// <param name="filePath">安装包文件路径</param>
    /// <param name="versionId">版本标识</param>
    private void InstallWithRegister(string filePath, string versionId)
    {
        var extractDir = Path.Combine(BedrockVersionsDir, "extracted", versionId);

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
            var fallbackResult = ModBase.ShellAndGetOutput("powershell.exe",
                $"-NoProfile -Command \"Add-AppxPackage -Path '{filePath}' -ErrorAction Stop 2>&1\"",
                timeout: 300000);
            if (fallbackResult.Contains("Exception") || fallbackResult.Contains("错误"))
                throw new Exception(fallbackResult.Length > 300 ? fallbackResult[..300] : fallbackResult);
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
            var registerResult = ModBase.ShellAndGetOutput("powershell.exe",
                $"-NoProfile -Command \"Add-AppxPackage -Register '{manifestPath}' -ErrorAction Stop 2>&1\"",
                timeout: 300000);

            if (!registerResult.Contains("Exception") && !registerResult.Contains("错误"))
            {
                ModBase.Log("[Bedrock] 开发模式注册成功，Store 不会自动更新此版本", ModBase.LogLevel.Normal);
                return;
            }

            ModBase.Log($"[Bedrock] -Register 失败，尝试重新打包为 .appx: {registerResult.Trim()}", ModBase.LogLevel.Normal);
        }

        // 方式2：重新打包为 .appx 后安装（回退方案）
        var appxPath = Path.Combine(Path.GetDirectoryName(filePath)!, $"Minecraft_Bedrock_{versionId}.appx");
        if (File.Exists(appxPath)) File.Delete(appxPath);
        ZipFile.CreateFromDirectory(extractDir, appxPath);

        var appxResult = ModBase.ShellAndGetOutput("powershell.exe",
            $"-NoProfile -Command \"Add-AppxPackage -Path '{appxPath}' -ErrorAction Stop 2>&1\"",
            timeout: 300000);

        if (appxResult.Contains("Exception") || appxResult.Contains("错误"))
            throw new Exception(appxResult.Length > 300 ? appxResult[..300] : appxResult);
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
            var result = ModBase.ShellAndGetOutput("powershell.exe",
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
            var result = ModBase.ShellAndGetOutput("powershell.exe",
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
        // 有效的 1x1 白色 PNG（base64），使用 System.Drawing 生成
        try
        {
            // 尝试通过 PowerShell 生成（利用系统自带的 GDI+）
            var psScript = $@"
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(1, 1)
$bmp.SetPixel(0, 0, [System.Drawing.Color]::White)
$bmp.Save('{path.Replace("'", "''")}', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
";
            ModBase.ShellAndGetOutput("powershell.exe",
                $"-NoProfile -Command \"{psScript}\"",
                timeout: 10000);
        }
        catch
        {
            // 回退：写入硬编码的最小 PNG
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8BQDwAFeAFK5RqavAAAAABJRU5ErkJggg==");
            File.WriteAllBytes(path, png);
        }
    }

    #endregion
}
