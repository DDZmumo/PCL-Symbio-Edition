using System.Diagnostics;

namespace PCL.Core.App
{
    // Synthetic capability fixtures only; this host never links production Secrets or reads environment keys.
    internal static class Secrets
    {
        internal static byte[]? BedrockReleaseCik { get; set; }
        internal static byte[]? BedrockPreviewCik { get; set; }
    }
}

namespace PCL.Core.App.Localization
{
    internal static class Lang
    {
        internal static string Text(string key, params object[] values) => key;
    }
}

namespace PCL
{
    internal static class ModBase
    {
        internal enum LogLevel { Normal, Hint }
        internal static void Log(string message, LogLevel level = LogLevel.Normal, string? userSummary = null) { }
    }
    internal static class ModBedrockIsolation
    {
        internal static int ProcessProbeCount;
        internal static bool IsAnyGameRunning() { ProcessProbeCount++; return false; }
    }
    internal static class ModBedrockLoader
    {
        internal const string LeviLamina = "levilamina";
        internal static string GetLoader(string? version) => "vanilla";
    }
    internal static class ModBedrockMods
    {
        internal static string[] GetModFiles(string directory) => [];
        internal static string[] GetPreloadFiles(string directory) => [];
        internal static string GetPreloadDirectory(string directory) => Path.Combine(directory, "preload");
        internal static Process? Launch(string executable, string directory, string? arguments, List<string> mods) =>
            throw new InvalidOperationException("The capability regression host must never launch a game.");
    }
}
