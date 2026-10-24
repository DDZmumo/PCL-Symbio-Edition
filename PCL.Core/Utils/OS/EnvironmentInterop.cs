using System;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;

namespace PCL.Core.Utils.OS;

public static partial class EnvironmentInterop
{
    private const string LogModule = "Environment";

    /// <summary>
    /// 读取环境变量并使用 <see cref="StringExtension.Convert{T}"/> 将其转换为指定类型并写入目标引用。
    /// </summary>
    /// <param name="key">环境变量名</param>
    /// <param name="target">需要写入的目标引用 (不存在该环境变量或转换失败时不会写入)</param>
    /// <param name="detailLog">是否在日志中输出变量值</param>
    /// <typeparam name="TValue">目标引用的类型</typeparam>
    /// <returns>是否成功写入目标引用</returns>
    public static bool ReadVariable<TValue>(string key, ref TValue target, bool detailLog = true)
    {
        var envValue = Environment.GetEnvironmentVariable(key);
        if (envValue is null) return false;
        var valueLog = detailLog ? $" = {envValue}" : string.Empty;
        LogWrapper.Debug(LogModule, $"读取到环境变量 {key}{valueLog}");
        var value = envValue.Convert<TValue>();
        if (value is null)
        {
            LogWrapper.Warn(LogModule, $"环境变量 {key} 类型转换失败");
            return false;
        }
        target = value;
        return true;
    }

    public static string? GetSecret(string key, bool readEnv = true, bool readEnvDebugOnly = false)
    {
        if (!SecretDictionary.TryGetValue(key, out var value) &&
            readEnv &&
#if !DEBUG
            !readEnvDebugOnly &&
#endif
            ReadVariable($"PCL_{key}", ref value, false)
        ) SecretDictionary[key] = value;
        return value;
    }

    /// <summary>
    /// 获取以字节数组形式存储的密钥（如各类内容密钥），避免密钥以字符串形式留在程序集内。
    /// </summary>
    public static byte[]? GetSecretBytes(string key)
    {
        if (SecretBytesDictionary.TryGetValue(key, out var bytes)) return bytes;
#if DEBUG
        // 调试构建下允许直接通过环境变量提供十六进制密钥
        var hex = Environment.GetEnvironmentVariable($"PCL_{key}");
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return Convert.FromHexString(hex.Trim());
        }
        catch (FormatException)
        {
            LogWrapper.Warn(LogModule, $"环境变量 PCL_{key} 不是合法的十六进制字符串");
            return null;
        }
#else
        return null;
#endif
    }
    
    /// <summary>
    /// 获取当前操作系统名称。
    /// </summary>
    /// <returns>返回小写的操作系统名称，如 "windows", "linux", "osx"。</returns>
    public static string GetCurrentOsName() {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsLinux())
            return "linux";
        return OperatingSystem.IsMacOS() 
            ? "osx"
            : "unknown";
    }
}
