using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class MissionWorkspaceSafetyScannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flyt-safety-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Scan_BlocksSensitiveNamesAndKnownSecretPatterns()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"), "SAFE=value\n");
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "settings.txt"),
            "api_key=secret-value\n");
        var workspace = CreateWorkspace();

        var findings = await new MissionWorkspaceSafetyScanner().ScanAsync(
            workspace,
            [".env", "settings.txt"]);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.Path == ".env");
        Assert.Contains(findings, finding => finding.Path == "settings.txt");
        Assert.DoesNotContain(findings, finding => finding.Reason.Contains("secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_AllowsOrdinarySourceFiles()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Program.cs"), "Console.WriteLine(\"hello\");\n");

        var findings = await new MissionWorkspaceSafetyScanner().ScanAsync(
            CreateWorkspace(),
            ["Program.cs"]);

        Assert.Empty(findings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private MissionWorkspace CreateWorkspace()
    {
        var now = DateTimeOffset.UtcNow;
        return new MissionWorkspace(
            Guid.NewGuid(),
            _directory,
            _directory,
            "flyt/test",
            "base",
            "head",
            now,
            now);
    }
}
