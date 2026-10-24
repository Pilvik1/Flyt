namespace Flyt.Core;

public sealed record AgentSessionSpec(
    AgentProvider Provider,
    string Name,
    string WorkingDirectory,
    string LaunchCommand)
{
    public static AgentSessionSpec Create(
        AgentProvider provider,
        string name,
        string workingDirectory,
        string launchCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchCommand);

        return new AgentSessionSpec(
            provider,
            name.Trim(),
            Path.GetFullPath(workingDirectory),
            launchCommand.Trim());
    }
}
