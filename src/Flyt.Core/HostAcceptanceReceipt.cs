using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace Flyt.Core;

public enum AcceptanceCheckStatus
{
    Passed,
    Failed,
    NotRun,
}

public sealed record AcceptanceCheck(string Name, AcceptanceCheckStatus Status, string Evidence);

public sealed record HostAcceptanceReceipt(
    string SchemaVersion,
    DateTimeOffset CapturedAt,
    string FlytVersion,
    string OperatingSystem,
    string TerminalHost,
    string PsmuxVersion,
    IReadOnlyDictionary<string, string> ProviderVersions,
    string PresentationMode,
    IReadOnlyList<AcceptanceCheck> Checks)
{
    [JsonIgnore]
    public bool Passed => Checks.All(check => check.Status == AcceptanceCheckStatus.Passed);
}

public static class HostAcceptanceReceiptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        HostAcceptanceReceipt receipt,
        string basePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var fullBasePath = Path.GetFullPath(Path.ChangeExtension(basePath, null));
        var directory = Path.GetDirectoryName(fullBasePath)
            ?? throw new ArgumentException("Receipt path must have a parent directory.", nameof(basePath));
        Directory.CreateDirectory(directory);
        var jsonPath = $"{fullBasePath}.json";
        var markdownPath = $"{fullBasePath}.md";
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(receipt, JsonOptions),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            markdownPath,
            ToMarkdown(receipt),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }

    public static string ToMarkdown(HostAcceptanceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var builder = new StringBuilder()
            .AppendLine("# Flyt target-host acceptance receipt")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- **Result:** {(receipt.Passed ? "PASS" : "INCOMPLETE / FAIL")}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **Captured:** {receipt.CapturedAt:O}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **Flyt:** {receipt.FlytVersion}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **OS:** {receipt.OperatingSystem}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **Terminal:** {receipt.TerminalHost}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **psmux:** {receipt.PsmuxVersion}")
            .AppendLine(CultureInfo.InvariantCulture, $"- **Presentation:** {receipt.PresentationMode}")
            .AppendLine()
            .AppendLine("## Provider versions");
        foreach (var provider in receipt.ProviderVersions.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- **{provider.Key}:** {provider.Value}");
        }

        builder.AppendLine().AppendLine("## Checks");
        foreach (var check in receipt.Checks)
        {
            var marker = check.Status switch
            {
                AcceptanceCheckStatus.Passed => "PASS",
                AcceptanceCheckStatus.Failed => "FAIL",
                _ => "NOT RUN",
            };
            builder.AppendLine(CultureInfo.InvariantCulture, $"- **{marker} — {check.Name}:** {check.Evidence}");
        }

        builder.AppendLine()
            .AppendLine("This receipt intentionally excludes prompts, provider session IDs, tokens, environment variables, usernames, and filesystem paths.");
        return builder.ToString();
    }
}
