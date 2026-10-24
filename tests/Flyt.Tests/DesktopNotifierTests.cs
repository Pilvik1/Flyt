using System.Text;
using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class DesktopNotifierTests
{
    [Fact]
    public async Task NotifyAsync_OnWindows_UsesEncodedNativeToastCommand()
    {
        var executor = new RecordingExecutor();
        var notifier = new DesktopNotifier(executor, isWindows: () => true);

        var result = await notifier.NotifyAsync("Flyt Desk", "reviewer needs input");

        Assert.True(result.NativeAttempted);
        Assert.True(result.NativeDelivered);
        var invocation = Assert.Single(executor.Invocations);
        Assert.Equal("powershell.exe", invocation.FileName);
        var arguments = invocation.Arguments.ToList();
        var encodedIndex = arguments.IndexOf("-EncodedCommand");
        Assert.True(encodedIndex >= 0);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[encodedIndex + 1]));
        Assert.Contains("ToastNotificationManager", script, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer needs input", invocation.Arguments);
    }

    [Fact]
    public async Task NotifyAsync_OffWindows_DoesNotSpawnProcess()
    {
        var executor = new RecordingExecutor();
        var notifier = new DesktopNotifier(executor, isWindows: () => false);

        var result = await notifier.NotifyAsync("Flyt Desk", "done");

        Assert.False(result.NativeAttempted);
        Assert.Empty(executor.Invocations);
    }

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<CommandInvocation> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
        }

        public Task<int> RunInteractiveAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
