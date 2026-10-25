namespace Flyt.Core;

public sealed record CommandInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null)
{
    public override string ToString() =>
        string.Join(' ', new[] { FileName }.Concat(Arguments.Select(Quote)));

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
