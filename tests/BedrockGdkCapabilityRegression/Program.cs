using PCL;
using PCL.Core.App;

var tests = 0;
var failures = 0;
var root = Path.Combine(Path.GetTempPath(), "PclGdkCapabilityRegression_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

void Check(string name, Action action)
{
    tests++;
    Secrets.BedrockReleaseCik = null;
    Secrets.BedrockPreviewCik = null;
    ModBedrockIsolation.ProcessProbeCount = 0;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}

Exception Capture(Action action)
{
    try { action(); }
    catch (Exception error) { return error; }
    throw new InvalidOperationException("Expected the operation to report a failure.");
}

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void RequireUnavailable(Exception error) => Require(
    error.GetType().Name == "GdkContentKeyUnavailableException",
    "Missing build capability must be classified separately from an unexpected installation error; got " +
    error.GetType().Name + ": " + error.Message);

try
{
    var package = Path.Combine(root, "Minecraft-release.msixvc");
    var preview = Path.Combine(root, "Minecraft-preview.msixvc");
    foreach (var length in new int?[] { null, 0, 16, 47 })
    {
        Check("release rejects missing or short build component [" + length + "]", () =>
        {
            Secrets.BedrockReleaseCik = length is { } count ? new byte[count] : null;
            RequireUnavailable(Capture(() => ModBedrockGdk.Extract(package, Path.Combine(root, "unused"))));
        });
    }
    Check("preview does not silently use release capability", () =>
    {
        Secrets.BedrockReleaseCik = new byte[48];
        RequireUnavailable(Capture(() => ModBedrockGdk.Extract(preview, Path.Combine(root, "unused"))));
    });
    Check("release does not silently use preview capability", () =>
    {
        Secrets.BedrockPreviewCik = new byte[48];
        RequireUnavailable(Capture(() => ModBedrockGdk.Extract(package, Path.Combine(root, "unused"))));
    });
    foreach (var channel in new[] { "release", "preview" })
    {
        Check(channel + " with a synthetic valid shape reaches package validation", () =>
        {
            if (channel == "release") Secrets.BedrockReleaseCik = new byte[48];
            else Secrets.BedrockPreviewCik = new byte[48];
            var error = Capture(() => ModBedrockGdk.Extract(channel == "release" ? package : preview,
                Path.Combine(root, "unused")));
            Require(error is FileNotFoundException, "Expected the absent fixture package to be checked after capability validation.");
        });
    }
    Check("unsupported install fails before probing game processes or creating staging", () =>
    {
        var versions = Path.Combine(root, "versions");
        var installed = Path.Combine(versions, "extracted", "26.52");
        Directory.CreateDirectory(Path.Combine(installed, "mods"));
        File.WriteAllText(Path.Combine(installed, "Minecraft.Windows.exe"), "old fixture");
        File.WriteAllText(Path.Combine(installed, "mods", "user.dll"), "keep fixture");
        var error = Capture(() => ModBedrockInstall.InstallGdk(package, versions, "26.52"));
        RequireUnavailable(error);
        Require(ModBedrockIsolation.ProcessProbeCount == 0, "Missing build capability should not inspect running games.");
        Require(!Directory.Exists(Path.Combine(versions, "extracted", ".staging")), "Unsupported install created staging directories.");
        Require(File.ReadAllText(Path.Combine(installed, "Minecraft.Windows.exe")) == "old fixture", "Old installation changed.");
        Require(File.ReadAllText(Path.Combine(installed, "mods", "user.dll")) == "keep fixture", "User mod changed.");
    });
    Check("pre-cancelled extraction preserves cancellation before capability error", () =>
    {
        var error = Capture(() => ModBedrockGdk.Extract(package, Path.Combine(root, "unused"),
            token: new CancellationToken(true)));
        Require(error is OperationCanceledException, "Cancellation must not be replaced by the missing-component message.");
    });
    Check("pre-cancelled install never probes processes", () =>
    {
        var error = Capture(() => ModBedrockInstall.InstallGdk(package, Path.Combine(root, "cancelled"),
            "26.52", token: new CancellationToken(true)));
        Require(error is OperationCanceledException, "Expected cancellation.");
        Require(ModBedrockIsolation.ProcessProbeCount == 0, "Cancelled install probed processes.");
    });
}
finally
{
    var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    var fullRoot = Path.GetFullPath(root);
    if (!fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(fullRoot).StartsWith("PclGdkCapabilityRegression_", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to clean an unexpected temporary root.");
    Directory.Delete(fullRoot, true);
}
Console.WriteLine($"{tests - failures}/{tests} passed");
return failures == 0 ? 0 : 1;
