using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     从 GitHub Release 获取更新的更新源
/// </summary>
public class UpdatesGitHubModel : IUpdateSource
{
    private readonly string _owner;
    private readonly string _repo;
    private bool _fetched;
    private GithubReleaseModel? _latestRelease;

    public UpdatesGitHubModel(string owner, string repo, string name = "GitHub")
    {
        _owner = owner;
        _repo = repo;
        SourceName = name;
    }

    public string SourceName { get; set; }

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(_owner) && !string.IsNullOrWhiteSpace(_repo);
    }

    public bool RefreshCache()
    {
        var release = FetchLatestRelease();
        // 请求失败时保留上一次成功获取的结果，避免网络抖动把缓存的版本信息清空
        if (release is not null)
            _latestRelease = release;
        _fetched = true;
        return _latestRelease is not null;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetRelease();
        // 没有可用的 Release 时返回当前版本，避免检查更新时弹出错误
        if (release is null)
            return new VersionDataModel
            {
                Source = SourceName,
                VersionName = ModBase.versionBaseName,
                VersionCode = ModBase.versionCode,
                Changelog = ""
            };

        var versionName = NormalizeTag(release.TagName);
        return new VersionDataModel
        {
            Source = SourceName,
            VersionName = versionName,
            VersionCode = ParseVersionCode(versionName) ?? ModBase.versionCode,
            Sha256 = GetAssetSha256(SelectAsset(release, arch)),
            Changelog = release.Body ?? ""
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var release = GetRelease();
        if (release is null)
            return true;
        // 缺少 SHA256 时不进行更新，否则下载后的校验必然失败
        if (string.IsNullOrWhiteSpace(GetAssetSha256(SelectAsset(release, arch))))
            return true;
        try
        {
            return currentVersion >= SemVer.Parse(NormalizeTag(release.TagName));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Update] 无法解析 {_owner}/{_repo} 的版本号：{release.TagName}", ModBase.LogLevel.Developer);
            return true;
        }
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        return new VersionAnnouncementDataModel { Content = new List<VersionAnnouncementContentModel>() };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetDownloadInfo"), load =>
        {
            var url = SelectAsset(GetRelease(), arch)?.BrowserDownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
            load.output = new List<DownloadFile> { new(new[] { url }, output) };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"), new List<DownloadFile>()));
        return loaders;
    }

    private GithubReleaseModel? GetRelease()
    {
        if (!_fetched)
            RefreshCache();
        return _latestRelease;
    }

    private GithubReleaseModel? FetchLatestRelease()
    {
        try
        {
            var json = Requester.FetchString(
                $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest",
                new RequestParam { Retries = 2, Timeout = 15000, UseBrowserUserAgent = true });
            var release = ModBase.GetJson(json)?.ToObject<GithubReleaseModel>();
            if (release is null || release.Draft)
                return null;
            return release;
        }
        catch (Exception ex)
        {
            // 仓库没有 Release 或网络不可用时按“无更新”处理
            ModBase.Log(ex, $"[Update] 无法获取 {_owner}/{_repo} 的 Release 信息");
            return null;
        }
    }

    private static GithubAssetModel? SelectAsset(GithubReleaseModel? release, UpdateArch arch)
    {
        var assets = release?.Assets?
            .Where(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (assets is null || assets.Count == 0)
            return null;
        var archTag = arch == UpdateArch.arm64 ? "arm64" : "x64";
        return assets.FirstOrDefault(a => a.Name.Contains(archTag, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a => !a.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetAssetSha256(GithubAssetModel? asset)
    {
        var digest = asset?.Digest;
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;
    }

    private static string NormalizeTag(string? tag)
    {
        return (tag ?? "").Trim().TrimStart('v', 'V');
    }

    private static int? ParseVersionCode(string versionName)
    {
        var parts = versionName.Split('.', '-', '+');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch))
            return null;
        return major * 10000 + minor * 100 + patch;
    }

    private class GithubReleaseModel
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAssetModel>? Assets { get; set; }
    }

    private class GithubAssetModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
