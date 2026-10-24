using Flyt.Core;

namespace Flyt.Providers;

public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<AgentProvider, IProviderAdapter> _adapters;

    public ProviderRegistry(IEnumerable<IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(adapter => adapter.Provider);
    }

    public static ProviderRegistry CreateDefault() => new(
    [
        new CliProviderAdapter(AgentProvider.Claude, "claude", "claude", "--version"),
        new CliProviderAdapter(AgentProvider.Codex, "codex", "codex", "--version"),
        new CliProviderAdapter(AgentProvider.Shell, "pwsh", "pwsh", "--version"),
    ]);

    public IProviderAdapter Get(AgentProvider provider) =>
        _adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"No adapter is registered for {provider}.");

    public string BuildInteractiveCommand(AgentProvider provider, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || provider == AgentProvider.Shell)
        {
            return Get(provider).InteractiveCommand;
        }

        var quoted = $"'{sessionId.Replace("'", "''", StringComparison.Ordinal)}'";
        return provider switch
        {
            AgentProvider.Claude => $"claude --resume {quoted}",
            AgentProvider.Codex => $"codex resume {quoted}",
            _ => Get(provider).InteractiveCommand,
        };
    }

    public async Task<IReadOnlyList<ProviderProbe>> ProbeAllAsync(
        CancellationToken cancellationToken = default)
    {
        var probes = await Task.WhenAll(
            _adapters.Values.Select(adapter => adapter.ProbeAsync(cancellationToken))).ConfigureAwait(false);
        return probes.OrderBy(probe => probe.Provider).ToArray();
    }
}
