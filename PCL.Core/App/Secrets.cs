using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID
    /// </summary>
    public static string MSOAuthClientId { get; } = EnvironmentInterop.GetSecret("MS_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty("d4915fd6-8245-4bd4-a150-396834b705b5");

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 遥测密钥
    /// </summary>
    public static string TelemetryKey { get; } = EnvironmentInterop.GetSecret("TELEMETRY_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 基岩版 GDK Release 版内容密钥（CIK，48 字节）
    /// </summary>
    public static string BedrockReleaseCik { get; } = EnvironmentInterop.GetSecret("BEDROCK_RELEASE_CIK", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 基岩版 GDK Preview / Beta 版内容密钥（CIK，48 字节）
    /// </summary>
    public static string BedrockPreviewCik { get; } = EnvironmentInterop.GetSecret("BEDROCK_PREVIEW_CIK", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();
}
