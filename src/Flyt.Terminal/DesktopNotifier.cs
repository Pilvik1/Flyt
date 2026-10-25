using System.Text;
using Flyt.Core;

namespace Flyt.Terminal;

public sealed record DesktopNotificationResult(bool NativeAttempted, bool NativeAccepted, string? Error);

public sealed class DesktopNotifier(
    ICommandExecutor executor,
    Func<bool>? isWindows = null)
{
    private readonly ICommandExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public async Task<DesktopNotificationResult> NotifyAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!_isWindows())
        {
            return new DesktopNotificationResult(false, false, null);
        }

        var titlePayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
        var messagePayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        var script = $$"""
            $title = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{titlePayload}}'))
            $message = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{messagePayload}}'))
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            $template = [Windows.UI.Notifications.ToastTemplateType]::ToastText02
            $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent($template)
            $nodes = $xml.GetElementsByTagName('text')
            [void]$nodes.Item(0).AppendChild($xml.CreateTextNode($title))
            [void]$nodes.Item(1).AppendChild($xml.CreateTextNode($message))
            $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Flyt').Show($toast)
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            var result = await _executor.RunAsync(
                new CommandInvocation(
                    "powershell.exe",
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded]),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new DesktopNotificationResult(true, true, null)
                : new DesktopNotificationResult(
                    true,
                    false,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? $"powershell.exe exited with {result.ExitCode}."
                        : result.StandardError.Trim());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DesktopNotificationResult(true, false, exception.Message);
        }
    }
}
