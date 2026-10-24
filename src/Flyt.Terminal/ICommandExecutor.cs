using Flyt.Core;

namespace Flyt.Terminal;

public interface ICommandExecutor
{
    Task<CommandResult> RunAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default);

    Task<int> RunInteractiveAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

