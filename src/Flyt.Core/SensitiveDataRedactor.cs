using System.Text.RegularExpressions;

namespace Flyt.Core;

public static partial class SensitiveDataRedactor
{
    public const string Replacement = "[REDACTED]";
    public const int MaximumPersistedTextLength = 32_768;

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = PrivateKeyPattern().Replace(value, Replacement);
        redacted = AuthorizationPattern().Replace(redacted, match => $"{match.Groups[1].Value}{Replacement}");
        redacted = SensitiveAssignmentPattern().Replace(redacted, match =>
            $"{match.Groups[1].Value}{match.Groups[2].Value}{Replacement}");
        redacted = KnownTokenPattern().Replace(redacted, Replacement);
        redacted = SensitiveQueryPattern().Replace(redacted, match =>
            $"{match.Groups[1].Value}{Replacement}");

        return redacted.Length <= MaximumPersistedTextLength
            ? redacted
            : redacted[^MaximumPersistedTextLength..];
    }

    [GeneratedRegex(
        @"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(
        @"(?im)\b(authorization\s*[:=]\s*(?:bearer|basic)\s+)[^\s\""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(
        @"(?im)\b((?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password|passwd|secret|token)\s*)([:=]\s*)[^\s,;&\""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(
        @"\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{16,}|sk-ant-[A-Za-z0-9_-]{16,}|github_pat_[A-Za-z0-9_]{20,}|gh[opusr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex(
        @"(?i)([?&](?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password|secret|token)=)[^&#\s]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryPattern();
}
