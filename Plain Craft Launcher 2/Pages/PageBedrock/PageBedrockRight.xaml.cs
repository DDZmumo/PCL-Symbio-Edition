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
    private bool isLoad;
    private readonly HashSet<string> _downloadingVersions = [];
    private readonly HashSet<string> _installingVersions = [];
    private readonly HashSet<string> _localVersionIds = [];
    private readonly Dictionary<string, MyIconButton> _versionButtons = [];
    private readonly Dictionary<string, bool> _versionAvailable = [];
    private readonly Dictionary<string, string> _versionBuildTypes = [];
    private JsonArray? _versionCache;
    private string _versionFilter = "all";

    // 当前已注册（正在使用）的本地版本。UWP 包的 PFN 固定，同一时刻只能注册一个版本，
    // 因此“共存”体现为每个版本各占一个解压目录，启动时按目录切换注册。
    private string? _registeredVersionId;
    private bool _switchingVersion;

    // 状态行当前展示「点击复制安装路径」时，保存可复制的路径；其它状态文案下为 null
    private string? _detailCopyPath;

    // 启动进度：实际阶段进度 + 100ms 平滑显示（与 Java 版启动页一致）
    private readonly DispatcherTimer _launchProgressTimer;
    private double _launchActualProgress;
    private double _launchShownProgress;
    private bool _launching;
    private int _launchGeneration;

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
        _launchProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _launchProgressTimer.Tick += LaunchProgressTimer_Tick;
        // 状态文案会被启动、安装等流程频繁改写，统一在这里同步“路径可复制”的提示与悬停内容
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(LabDetail, (_, _) => UpdateDetailCopyState());
        Loaded += PageBedrockRight_Loaded;
    }

    /// <summary>状态行是否为「点击复制安装路径」，据此决定鼠标指针与悬停显示的路径。</summary>
    private void UpdateDetailCopyState()
    {
        var isPathHint = LabDetail.Text == Lang.Text("Bedrock.Status.CopyPathHint");
        _detailCopyPath = isPathHint ? BedrockInstallPath : null;
        LabDetail.ToolTip = string.IsNullOrEmpty(_detailCopyPath) ? null : _detailCopyPath;
        LabDetail.Cursor = string.IsNullOrEmpty(_detailCopyPath) ? null : Cursors.Hand;
    }

    private void LabDetail_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_detailCopyPath)) return;

        try
        {
            Clipboard.SetText(_detailCopyPath);
            HintService.Hint(Lang.Text("Bedrock.Status.PathCopied"), HintType.Success);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] 复制安装路径失败");
            HintService.Hint(Lang.Text("Bedrock.Status.PathCopyFailed"), HintType.Error);
        }
    }

    private void PageBedrockRight_Loaded(object sender, RoutedEventArgs e)
    {
        if (isLoad) return;
        isLoad = true;

        // 上次启动器退出时若隔离未还原，%APPDATA% 下会残留目录联接，启动时先恢复共享数据目录
        _ = Task.Run(() =>
        {
            if (!ModBedrockIsolation.IsAnyGameRunning())
                ModBedrockIsolation.Restore();
        });

        CheckIsolation.Checked = Config.Launch.BedrockVersionIsolation;
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
    // GDK 便携版：解包后直接运行 Minecraft.Windows.exe，不注册系统包
    private bool _isGdkPortable;

    public void DetectBedrockInstallation()
    {
        if (_detecting) return;
        _detecting = true;

        // 检测需要调用 PowerShell 并遍历目录，放到后台线程执行，避免界面卡住导致按钮看起来点不动
        _ = Task.Run(() =>
        {
            // 已注册的 UWP 包优先；没有注册包时回退到启动器解包的 GDK 便携版
            var portable = FindPortableGdkInstallation();
            var bedrockPath = FindBedrockInstallation() ?? portable?.Path;
            var (bedrockVersion, signatureKind) = GetBedrockPackageInfo();
            if (portable is not null && string.Equals(bedrockPath, portable.Value.Path, StringComparison.OrdinalIgnoreCase))
                bedrockVersion = portable.Value.Version;
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
        _isGdkPortable = !string.IsNullOrEmpty(bedrockPath) && IsPortableGdkPath(bedrockPath);
        if (!string.IsNullOrEmpty(bedrockPath))
        {
            ShapeStatusDot.Fill = (Brush)FindResource("ColorBrush3");
            LabStatus.Text = Lang.Text("Bedrock.Status.Detected");
            LabDetail.Text = Lang.Text("Bedrock.Status.CopyPathHint");
            LabVersion.Text = string.IsNullOrEmpty(bedrockVersion)
                ? "Minecraft Bedrock Edition"
                : Lang.Text("Bedrock.Status.Version", bedrockVersion);
            BtnLaunch.IsEnabled = true;
            BtnLaunch.Text = Lang.Text("Bedrock.Action.Launch");
        }
        else
        {
            ShapeStatusDot.Fill = (Brush)FindResource("ColorBrushGray4");
            LabStatus.Text = Lang.Text("Bedrock.Status.NotFound");
            LabDetail.Text = Lang.Text("Bedrock.Status.DownloadOrImportHint");
            LabVersion.Text = Lang.Text("Bedrock.Status.NotInstalled");
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
    ///     版本隔离开关：GDK 便携版可以在启动前把共享数据目录指向版本自己的目录。
    /// </summary>
    private void CheckIsolation_OnChange(object sender, bool user)
    {
        // 仅处理用户操作，代码里设置 Checked 时不做任何事
        if (!user || sender is not MyCheckBox checkBox) return;

        if (checkBox.Checked != true)
        {
            if (ModBedrockIsolation.IsAnyGameRunning())
            {
                checkBox.SetChecked(true, false);
                ModBase.Log("[Bedrock] 基岩版正在运行，无法关闭版本隔离", ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Bedrock.Isolation.RunningHint"));
                return;
            }

            if (!ModBedrockIsolation.Restore())
            {
                checkBox.SetChecked(true, false);
                ModBase.Log("[Bedrock] 数据目录尚未安全还原，请先检查隔离备份。", ModBase.LogLevel.Hint);
                return;
            }
            Config.Launch.BedrockVersionIsolation = false;
            ModBase.Log("[Bedrock] 已关闭基岩版版本隔离", ModBase.LogLevel.Normal);
            return;
        }

        if (!_isGdkPortable || string.IsNullOrEmpty(BedrockInstallPath))
        {
            checkBox.SetChecked(false, false);
            ModBase.Log("[Bedrock] 版本隔离仅支持 GDK 便携版", ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Bedrock.Isolation.GdkOnly"));
            return;
        }

        if (ModBedrockIsolation.IsAnyGameRunning())
        {
            checkBox.SetChecked(false, false);
            ModBase.Log("[Bedrock] 请先关闭正在运行的基岩版", ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Bedrock.Isolation.RunningHint"));
            return;
        }

        try
        {
            ModBedrockIsolation.Apply(BedrockInstallPath, BedrockVersion ?? Path.GetFileName(BedrockInstallPath));
            Config.Launch.BedrockVersionIsolation = true;
            ModBase.Log("[Bedrock] 已启用基岩版版本隔离", ModBase.LogLevel.Normal);
        }
        catch (Exception ex)
        {
            checkBox.SetChecked(false, false);
            ModBase.Log(ex, "启用基岩版版本隔离失败", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.Isolation.Failed", ex.Message));
        }
    }

    /// <summary>
    ///     版本列表加载完成（或重新检测）后，比较已安装包版本与最新正式版。
    ///     商店版包由 Microsoft Store 负责更新，商店安装的包无法由启动器侧载升级。
    /// </summary>
    private void RefreshUpdateHint()
    {
        if (BtnUpdate is null) return;

        if (!IsBedrockInstalled || !_isStorePackage || _isGdkPortable || _versionCache is null)
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
            var output = ModBase.ShellAndGetOutputChecked("powershell.exe",
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
            var output = ModBase.ShellAndGetOutputChecked("powershell.exe",
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

    /// <summary>
    ///     该版本是否为 GDK 版（版本目录内保存的是 .msixvc 而不是 .appx）。
    /// </summary>
    private static bool IsGdkVersion(string versionId)
    {
        var dir = ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId);
        return GetPackageFiles(dir).Any(ModBedrockGdk.IsGdkPackage);
    }

    /// <summary>
    ///     路径是否为启动器解包出来的 GDK 便携版目录（extracted\&lt;版本号&gt;）。
    /// </summary>
    private static bool IsPortableGdkPath(string path)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(BedrockVersionsDir, "extracted"))
                .TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            var relative = full[(root.Length + 1)..];
            var versionId = relative.Split(Path.DirectorySeparatorChar)[0];
            return !string.IsNullOrEmpty(versionId) &&
                   IsGdkVersion(versionId) &&
                   ModBedrockGdk.IsInstalled(full);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     在已解包目录中寻找可用的 GDK 便携版，取版本号最高的一个。
    /// </summary>
    private static (string Path, string Version)? FindPortableGdkInstallation()
    {
        try
        {
            var extractedRoot = Path.Combine(BedrockVersionsDir, "extracted");
            if (!Directory.Exists(extractedRoot)) return null;

            (string Path, string Version)? best = null;
            foreach (var dir in Directory.GetDirectories(extractedRoot))
            {
                var versionId = Path.GetFileName(dir);
                if (!IsGdkVersion(versionId) || !ModBedrockGdk.IsInstalled(dir)) continue;
                if (best is null || ModDownload.CompareBedrockVersions(versionId, best.Value.Version) > 0)
                    best = (dir, versionId);
            }

            return best;
        }
        catch
        {
            return null;
        }
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

        // 记住的选中版本优先：当前注册的不是它时，先切换到该版本
        var selected = Config.Launch.BedrockSelectedVersion;
        if (!string.IsNullOrEmpty(selected) && _localVersionIds.Contains(selected) &&
            !string.Equals(selected, _registeredVersionId, StringComparison.OrdinalIgnoreCase))
        {
            LaunchLocalVersion(selected);
            return;
        }

        LaunchBedrock();
    }

    #region 启动进度

    /// <summary>开始显示启动进度条，并禁用启动按钮直到本次启动结束。</summary>
    private void BeginLaunchProgress(string detail)
    {
        _launching = true;
        _launchGeneration++;
        _launchActualProgress = 0.02d;
        _launchShownProgress = 0d;
        ProgressBedrockFinished.Width = new GridLength(0d, GridUnitType.Star);
        ProgressBedrockUnfinished.Width = new GridLength(1d, GridUnitType.Star);
        PanLaunchProgress.Visibility = Visibility.Visible;
        LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Run;
        BtnLaunch.IsEnabled = false;
        LabStatus.Text = Lang.Text("Bedrock.Status.Launching");
        LabDetail.Text = detail;
        _launchProgressTimer.Start();
    }

    /// <summary>推进到实际阶段进度（只会前进），可在后台线程调用。</summary>
    private void SetLaunchStage(double progress, string? detail = null)
    {
        void Apply()
        {
            if (!_launching) return;
            if (progress > _launchActualProgress) _launchActualProgress = progress;
            if (detail is not null) LabDetail.Text = detail;
        }

        if (Dispatcher.CheckAccess()) Apply();
        else ModBase.RunInUi(Apply);
    }

    /// <summary>结束本次启动：成功时先补满进度条再收起，失败时立即收起并显示错误。</summary>
    private void EndLaunchProgress(bool success, string status, string detail)
    {
        void Apply()
        {
            LabStatus.Text = status;
            LabDetail.Text = detail;
            LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Stop;

            if (!success)
            {
                _launching = false;
                _launchProgressTimer.Stop();
                PanLaunchProgress.Visibility = Visibility.Collapsed;
                BtnLaunch.IsEnabled = true;
                return;
            }

            _launchActualProgress = 1d;
            var generation = ++_launchGeneration;
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                ModBase.RunInUi(() =>
                {
                    // 期间用户可能已经开始下一次启动，只有仍是本次启动才收起
                    if (generation != _launchGeneration) return;
                    _launching = false;
                    _launchProgressTimer.Stop();
                    PanLaunchProgress.Visibility = Visibility.Collapsed;
                    BtnLaunch.IsEnabled = true;
                });
            });
        }

        if (Dispatcher.CheckAccess()) Apply();
        else ModBase.RunInUi(Apply);
    }

    private void LaunchProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (!_launching) return;
        // 向实际进度靠一点，避免进度条看起来一直在跳
        if (_launchActualProgress >= _launchShownProgress)
            _launchShownProgress += (_launchActualProgress - _launchShownProgress) * 0.2d + 0.005d;
        if (_launchActualProgress <= _launchShownProgress)
            _launchShownProgress = _launchActualProgress;
        if (_launchShownProgress > 1d) _launchShownProgress = 1d;

        // 每次 Tick 用同名动画覆盖上一帧，让两列宽度平滑跟随
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaGridLengthWidth(ProgressBedrockFinished,
                _launchShownProgress - ProgressBedrockFinished.Width.Value, 260,
                ease: new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaGridLengthWidth(ProgressBedrockUnfinished,
                1d - _launchShownProgress - ProgressBedrockUnfinished.Width.Value, 260,
                ease: new ModAnimation.AniEaseOutFluent())
        }, "Bedrock Launch Progress");
    }

    /// <summary>等待游戏窗口出现；拿不到进程句柄时（注册式启动）按进程名查找。</summary>
    private static bool WaitForGameWindow(Process? process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process is not null)
                {
                    if (process.HasExited) return false;
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero) return true;
                }
                else
                {
                    foreach (var name in new[] { "Minecraft.Windows", "Minecraft.Win10.DX11" })
                    foreach (var running in Process.GetProcessesByName(name))
                        using (running)
                        {
                            running.Refresh();
                            if (running.MainWindowHandle != IntPtr.Zero) return true;
                        }
                }
            }
            catch { }

            Thread.Sleep(200);
        }

        return false;
    }

    #endregion



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
            PanLocalVersions.Children.Add(BuildLocalCard(item.Id, item.FilePath));
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
        _registeredVersionId = null;
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
                _registeredVersionId = segment;
        }
        catch
        {
            // 路径异常时忽略，仅影响「已安装」标记
        }
    }

    /// <summary>
    ///     读取解包目录中 AppxManifest 的 Identity 版本号，用于版本卡片的副标题。
    /// </summary>
    private static string? TryReadExtractedIdentityVersion(string versionId)
    {
        var (_, manifestPath) = FindExtractedVersion(versionId);
        if (manifestPath is null) return null;

        try
        {
            var identity = XDocument.Load(manifestPath).Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Identity");
            return identity?.Attribute("Version")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     查找版本解压后的可注册目录。包束会多解一层 inner。
    /// </summary>
    private static (string? Directory, string? Manifest) FindExtractedVersion(string versionId)
    {
        var extractDir = ModBedrockUwp.GetVersionDirectory(Path.Combine(BedrockVersionsDir, "extracted"), versionId);
        var innerDir = Path.Combine(extractDir, "inner");

        var innerManifest = Path.Combine(innerDir, "AppxManifest.xml");
        if (File.Exists(innerManifest)) return (innerDir, innerManifest);

        var manifest = Path.Combine(extractDir, "AppxManifest.xml");
        return File.Exists(manifest) ? (extractDir, manifest) : (null, null);
    }

    /// <summary>
    ///     选择该 GDK 版本的模组加载器：原版不做注入，LeviLamina 注入 mods 目录内的 DLL。
    /// </summary>
    private void LocalLoader_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        var current = ModBedrockLoader.GetLoader(versionId);
        var isLeviLamina = current == ModBedrockLoader.LeviLamina;
        List<MyMsgCardOption> options =
        [
            new()
            {
                Title = Lang.Text("Bedrock.Loader.Vanilla"),
                Description = Lang.Text("Bedrock.Loader.Vanilla.Description"),
                SvgIcon = "lucide/box",
                Tag = isLeviLamina ? "" : Lang.Text("Bedrock.Loader.CurrentTag")
            },
            new()
            {
                Title = Lang.Text("Bedrock.Loader.LeviLamina"),
                Description = Lang.Text("Bedrock.Loader.LeviLamina.Description"),
                SvgIcon = "lucide/puzzle",
                Tag = isLeviLamina ? Lang.Text("Bedrock.Loader.CurrentTag") : ""
            }
        ];

        var selected = ModMain.MyMsgBoxCardSelect(options, isLeviLamina ? 1 : 0,
            Lang.Text("Bedrock.Loader.SelectTitle"),
            Lang.Text("Bedrock.Loader.SelectCaption", versionId),
            button2: Lang.Text("Common.Action.Cancel"));
        if (selected is null) return;

        ModBedrockLoader.SetLoader(versionId,
            selected == 1 ? ModBedrockLoader.LeviLamina : ModBedrockLoader.Vanilla);
        RefreshLocalVersions();
    }

    /// <summary>
    ///     启动指定本地版本，并把它记为当前选中版本（之后「启动基岩版」会跟随）。
    /// </summary>
    private void LaunchLocalVersion(string versionId)
    {
        if (_switchingVersion)
            return;

        // GDK 便携版：解包目录里有主程序就能直接启动，不需要改动系统包注册
        if (IsGdkVersion(versionId))
        {
            var gdkDir = ModBedrockUwp.GetVersionDirectory(
                Path.Combine(BedrockVersionsDir, "extracted"), versionId);
            if (ModBedrockGdk.IsInstalled(gdkDir))
            {
                ActivateGdkVersion(versionId, gdkDir);
                return;
            }

            var files = GetPackageFiles(ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId));
            if (files.Count == 0)
            {
                ModBase.Log($"[Bedrock] 版本 {versionId} 的安装包已被删除，无法解包启动",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Bedrock.Local.PackageMissing"));
                return;
            }

            // 尚未解包：先解包，安装完成后不会注册系统包
            StartInstall(files[0], versionId);
            return;
        }

        // 已经是当前注册的版本，直接启动
        if (string.Equals(_registeredVersionId, versionId, StringComparison.OrdinalIgnoreCase))
        {
            SelectVersion(versionId);
            LaunchBedrock();
            return;
        }

        _switchingVersion = true;
        LabStatus.Text = Lang.Text("Bedrock.Status.Switching");
        LabDetail.Text = Lang.Text("Bedrock.Status.SwitchingDetail", versionId);
        BtnLaunch.IsEnabled = false;

        Task.Run(() =>
        {
            string? installedPath = null;
            Exception? error = null;

            try
            {
                ModBase.Log($"[Bedrock] 正在切换到版本 {versionId}", ModBase.LogLevel.Normal);

                // 切换注册前先结束正在运行的基岩版
                ModBedrockUwp.StopMinecraftProcesses();

                var (targetDir, manifestPath) = FindExtractedVersion(versionId);
                if (manifestPath is not null)
                {
                    _ = ModBase.ShellAndGetOutputChecked("powershell.exe",
                        ModBedrockUwp.GetAppxInstallArguments(manifestPath, register: true),
                        timeout: 300000);
                    var identity = BedrockPackageRegistration.ReadIdentity(manifestPath);
                    if (identity is null || !IsRegisteredFromDirectory(identity, targetDir!))
                        throw new IOException("系统未确认此基岩版已注册到目标目录。");
                }

                if (targetDir is null)
                {
                    // 解压目录缺失或被清理：用已下载的安装包重新解压并注册
                    var files = GetPackageFiles(ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId));
                    if (files.Count == 0)
                        throw new FileNotFoundException(Lang.Text("Bedrock.Local.PackageMissing"));
                    InstallWithRegister(files[0], versionId);
                    (targetDir, _) = FindExtractedVersion(versionId);
                }

                installedPath = targetDir;
                ModBase.Log($"[Bedrock] 已切换到版本 {versionId}", ModBase.LogLevel.Normal);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ModBase.RunInUi(() =>
            {
                _switchingVersion = false;
                BtnLaunch.IsEnabled = true;

                if (error is not null)
                {
                    ModBase.Log(error, $"切换到基岩版 {versionId} 失败",
                        ModBase.LogLevel.Feedback,
                        userSummary: Lang.Text("Bedrock.Launch.Error"));
                    LabStatus.Text = Lang.Text("Bedrock.Status.LaunchFailed");
                    LabDetail.Text = error.Message;
                    RefreshLocalVersions();
                    return;
                }

                IsBedrockInstalled = true;
                BedrockInstallPath = installedPath;
                BedrockVersion = versionId;
                _isStorePackage = false;
                BtnUpdate.Visibility = Visibility.Collapsed;
                SelectVersion(versionId);
                RefreshLocalVersions();
                // 左侧状态栏也同步显示切换后的版本
                ModMain.frmBedrockLeft?.RefreshStatus();
                LaunchBedrock();
            });
        });
    }

    /// <summary>
    ///     将启动目标切换为 GDK 便携版并直接启动。不触碰系统包注册，因此其他版本仍可继续运行。
    /// </summary>
    private void ActivateGdkVersion(string versionId, string extractDir)
    {
        IsBedrockInstalled = true;
        BedrockInstallPath = extractDir;
        BedrockVersion = versionId;
        _isStorePackage = false;
        _isGdkPortable = true;
        BtnUpdate.Visibility = Visibility.Collapsed;
        ShapeStatusDot.Fill = (Brush)FindResource("ColorBrush3");
        LabVersion.Text = Lang.Text("Bedrock.Status.Version", versionId);
        SelectVersion(versionId);
        RefreshLocalVersions();
        ModMain.frmBedrockLeft?.RefreshStatus();
        LaunchBedrock();
    }

    /// <summary>记住选中的版本，重启启动器后「启动基岩版」仍会跟随它。</summary>
    private void SelectVersion(string versionId)
    {
        Config.Launch.BedrockSelectedVersion = versionId;
    }

    private void LocalInstall_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        if (_installingVersions.Contains(versionId))
            return;

        var dir = ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId);
        var allFiles = GetPackageFiles(dir);
        if (allFiles.Count == 0) return;

        StartInstall(allFiles[0], versionId);
    }

    private void StartInstall(string filePath, string versionId)
    {
        if (_installingVersions.Contains(versionId))
            return;

        // GDK 版是加密的 .msixvc，不能侧载注册，改为解密解包成便携版
        if (ModBedrockGdk.IsGdkPackage(filePath))
        {
            StartInstallGdk(filePath, versionId);
            return;
        }

        _installingVersions.Add(versionId);
        RefreshLocalVersions();

        LabStatus.Text = Lang.Text("Bedrock.Install.Installing");
        LabDetail.Text = Lang.Text("Bedrock.Install.InstallingVersion", versionId);
        BtnLaunch.IsEnabled = false;

        InstallBedrockPackage(filePath, versionId);
    }

    /// <summary>
    ///     GDK 版安装：解包 .msixvc 到独立目录后直接可运行，不做系统包注册。
    /// </summary>
    private void StartInstallGdk(string filePath, string versionId)
    {
        if (_installingVersions.Contains(versionId))
            return;

        try
        {
            ModBedrockGdk.EnsureExtractionAvailable(filePath);
        }
        catch (GdkContentKeyUnavailableException error)
        {
            ShowGdkInstallUnavailable(error);
            return;
        }

        _installingVersions.Add(versionId);
        RefreshLocalVersions();

        LabStatus.Text = Lang.Text("Bedrock.Gdk.Extracting");
        LabDetail.Text = Lang.Text("Bedrock.Gdk.ExtractingDetail", versionId);
        BtnLaunch.IsEnabled = false;

        InstallGdkPackage(filePath, versionId);
    }

    private void ShowGdkInstallUnavailable(GdkContentKeyUnavailableException error)
    {
        // Only capability names are logged; never include key material or turn this expected limitation into feedback.
        ModBase.Log("[Bedrock] 当前构建缺少安装组件: " +
                    (error.IsPreview ? "BEDROCK_PREVIEW_CIK" : "BEDROCK_RELEASE_CIK"), ModBase.LogLevel.Normal);
        LabStatus.Text = Lang.Text("Bedrock.Gdk.InstallUnavailable");
        LabDetail.Text = error.Message;
        HintService.Hint(error.Message, HintType.Error);
    }

    private void LocalDelete_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        var packageDir = ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId);
        var extractDir = ModBedrockUwp.GetVersionDirectory(
            Path.Combine(BedrockVersionsDir, "extracted"), versionId);
        var isRegistered = string.Equals(_registeredVersionId, versionId, StringComparison.OrdinalIgnoreCase);
        var isGdk = IsGdkVersion(versionId);

        Task.Run(() =>
        {
            try
            {
                if (ModBedrockIsolation.IsAnyGameRunning())
                    throw new InvalidOperationException("请先关闭正在运行的基岩版，再删除本地版本。");
                // GDK may have been registered for package-identity activation. Remove only a registration
                // whose identity and InstallLocation match this version; preserve application data.
                if (isGdk)
                    ModBedrockRegistration.UnregisterPortable(extractDir);
                // 正在使用的 UWP 版本要先卸载，否则删除目录后会留下失效的包注册
                else if (isRegistered)
                    ModBedrockUwp.UninstallPreservingData();

                if (Directory.Exists(packageDir))
                    Directory.Delete(packageDir, true);
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);

                ModBase.Log($"[Bedrock] 已删除本地版本: {versionId}",
                    ModBase.LogLevel.Normal,
                    userSummary: Lang.Text("Bedrock.Local.Deleted", versionId));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "删除本地版本失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Bedrock.Local.DeleteFailed", ex.Message));
                return;
            }

            ModBase.RunInUi(() =>
            {
                if (isRegistered)
                {
                    IsBedrockInstalled = false;
                    BedrockInstallPath = null;
                    BedrockVersion = null;
                    _isGdkPortable = false;
                    DetectBedrockInstallation();
                }
                else
                {
                    RefreshLocalVersions();
                }
            });
        });
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

        LabStatus.Text = Lang.Text("Bedrock.Import.Importing");
        LabDetail.Text = Path.GetFileName(sourcePath);
        BtnImport.IsEnabled = false;

        Task.Run(() =>
        {
            try
            {
                var identityVersion = TryReadPackageIdentityVersion(sourcePath);
                var versionId = ResolveImportedVersionId(identityVersion, sourcePath);

                var destDir = ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId);
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

    private void BedrockVersionWebDownload_Click(object sender, EventArgs e)
    {
        if (sender is not MyTextButton btn || btn.Tag is not string versionId) return;

        var pageUrl = GetMcAppxPageUrl(versionId);
        ModBase.OpenWebsite(pageUrl);
        LabDetail.Text = Lang.Text("Bedrock.Version.PageOpened", versionId);
    }

    /// <summary>
    ///     刷新单个版本行右侧按钮的图标、可用性与提示。
    /// </summary>
    private void UpdateVersionButtonState(string versionId)
    {
        if (!_versionButtons.TryGetValue(versionId, out var btn)) return;

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

    /// <summary>
    ///     生成 mcappx 上对应版本的页面地址。
    ///     mcappx 的路径是「基岩版 / 系列」，而不是完整版本号：直接拼完整版本号会打开 404 页面。
    ///     26.x 起版本号第二段是补丁号，系列按十位归并（26.45 属于 26.40）；
    ///     更早的版本第二段本身就是系列（0.12.0 属于 0.12，1.21.120.21 属于 1.21）。
    /// </summary>
    private static string GetMcAppxPageUrl(string versionId)
    {
        var parts = versionId.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            var seriesMinor = major >= 20 ? minor / 10 * 10 : minor;
            return $"https://www.mcappx.com/bedrock/{major}.{seriesMinor}/";
        }
        return "https://www.mcappx.com/bedrock/";
    }

    private async void BedrockVersionDownload_Click(object sender, EventArgs e)
    {
        if (sender is not MyIconButton btn || btn.Tag is not string versionId)
            return;

        // 没有 Appx 下载源的版本不提供任何下载入口
        if (_versionAvailable.TryGetValue(versionId, out var available) && !available)
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

            var webUrl = GetMcAppxPageUrl(versionId);
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
            var saveDir = ModBedrockUwp.GetVersionDirectory(BedrockVersionsDir, versionId);
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

    #endregion
}
