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
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            throw;
        }

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
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(CommandInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            UseShellExecute = false,
            WorkingDirectory = invocation.WorkingDirectory ?? string.Empty,
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
