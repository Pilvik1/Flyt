using System.Diagnostics;
using Flyt.Core;

namespace Flyt.Terminal;

public sealed class SystemCommandExecutor : ICommandExecutor
{
    public async Task<CommandResult> RunAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(invocation);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {invocation.FileName}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }

    public async Task<int> RunInteractiveAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(invocation);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {invocation.FileName}.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static ProcessStartInfo CreateStartInfo(CommandInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

