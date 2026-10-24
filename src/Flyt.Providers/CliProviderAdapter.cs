using System.Diagnostics;
using Flyt.Core;

namespace Flyt.Providers;

public sealed class CliProviderAdapter(
    AgentProvider provider,
    string executableName,
    string interactiveCommand,
    params string[] versionArguments) : IProviderAdapter
{
    public AgentProvider Provider { get; } = provider;

    public string ExecutableName { get; } = executableName;

    public string InteractiveCommand { get; } = interactiveCommand;

    public async Task<ProviderProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var path = ExecutableLocator.Find(ExecutableName);
        if (path is null)
        {
            return new ProviderProbe(
                Provider,
                ExecutableName,
                IsAvailable: false,
                ExecutablePath: null,
                Version: null,
                Error: $"{ExecutableName} was not found on PATH.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in versionArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {ExecutableName}.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = string.Join(
                ' ',
                new[] { await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false) }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();

            return new ProviderProbe(
                Provider,
                ExecutableName,
                process.ExitCode == 0,
                path,
                string.IsNullOrWhiteSpace(output) ? null : output,
                process.ExitCode == 0 ? null : $"Version command exited with {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ProviderProbe(
                Provider,
                ExecutableName,
                IsAvailable: false,
                path,
                Version: null,
                Error: exception.Message);
        }
    }
}

