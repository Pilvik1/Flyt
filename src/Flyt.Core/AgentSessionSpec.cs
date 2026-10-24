namespace Flyt.Core;

public sealed record AgentSessionSpec(
    AgentProvider Provider,
    string Name,
    string WorkingDirectory)
{
    public static AgentSessionSpec Create(
        AgentProvider provider,
        string name,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        return new AgentSessionSpec(
            provider,
            name.Trim(),
            Path.GetFullPath(workingDirectory));
    }
}

