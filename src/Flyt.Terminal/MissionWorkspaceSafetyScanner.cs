using Flyt.Core;

namespace Flyt.Terminal;

public sealed class MissionWorkspaceSafetyScanner : IMissionSafetyScanner
{
    private const long MaximumTextScanBytes = 1_048_576;

    public async Task<IReadOnlyList<MissionSafetyFinding>> ScanAsync(
        MissionWorkspace workspace,
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(changedPaths);
        var findings = new List<MissionSafetyFinding>();
        var root = Path.GetFullPath(workspace.WorktreePath);

        foreach (var changedPath in changedPaths)
        {
            var normalized = changedPath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
            if (!IsBelowRoot(root, fullPath))
            {
                findings.Add(new MissionSafetyFinding(changedPath, "Path escapes the Mission worktree."));
                continue;
            }

            if (LooksLikeSensitiveFile(changedPath))
            {
                findings.Add(new MissionSafetyFinding(changedPath, "Sensitive filename is blocked."));
                continue;
            }

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length > MaximumTextScanBytes)
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (content.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(content, SensitiveDataRedactor.Redact(content), StringComparison.Ordinal))
            {
                findings.Add(new MissionSafetyFinding(
                    changedPath,
                    "Content matches a known secret pattern."));
            }
        }

        return findings;
    }

    private static bool LooksLikeSensitiveFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var extension = Path.GetExtension(name);
        return name == ".env"
               || name.StartsWith(".env.", StringComparison.Ordinal)
               || name is "credentials.json" or "service-account.json" or "id_rsa" or "id_ed25519"
               || extension is ".pem" or ".key" or ".p12" or ".pfx";
    }

    private static bool IsBelowRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathFullyQualified(relative);
    }
}
