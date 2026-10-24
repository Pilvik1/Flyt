namespace Flyt.Core;

public sealed record AgentSessionSpec(
    AgentProvider Provider,
    string Name,
    string WorkingDirectory,
    string LaunchCommand,
    string Role,
    string Task,
    string Color,
    IReadOnlyList<string> Tags,
    string? ProviderSessionId)
{
    public static AgentSessionSpec Create(
        AgentProvider provider,
        string name,
        string workingDirectory,
        string launchCommand,
        string? role = null,
        string? task = null,
        string? color = null,
        IReadOnlyList<string>? tags = null,
        string? providerSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchCommand);

        return new AgentSessionSpec(
            provider,
            name.Trim(),
            Path.GetFullPath(workingDirectory),
            launchCommand.Trim(),
            DeskPalette.NormalizeRole(role ?? name),
            string.IsNullOrWhiteSpace(task) ? name.Trim() : task.Trim(),
            DeskPalette.NormalizeColor(color ?? DeskPalette.ForRole(role ?? name)),
            (tags ?? []).Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            string.IsNullOrWhiteSpace(providerSessionId) ? null : providerSessionId.Trim());
    }
}
