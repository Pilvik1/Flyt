using Flyt.Core;

namespace Flyt.Providers;

public interface IProviderAdapter
{
    AgentProvider Provider { get; }

    string ExecutableName { get; }

    string InteractiveCommand { get; }

    Task<ProviderProbe> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record ProviderProbe(
    AgentProvider Provider,
    string ExecutableName,
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? Error);

