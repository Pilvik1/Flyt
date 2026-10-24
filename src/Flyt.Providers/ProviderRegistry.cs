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

    public async Task<IReadOnlyList<ProviderProbe>> ProbeAllAsync(
        CancellationToken cancellationToken = default)
    {
        var probes = await Task.WhenAll(
            _adapters.Values.Select(adapter => adapter.ProbeAsync(cancellationToken))).ConfigureAwait(false);
        return probes.OrderBy(probe => probe.Provider).ToArray();
    }
}

