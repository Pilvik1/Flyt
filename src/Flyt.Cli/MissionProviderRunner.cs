using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Flyt.Core;
using Flyt.Providers;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record MissionProviderRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    MissionProviderMetadata Metadata)
{
    public bool IsSuccess => ExitCode == 0;
}

internal interface IMissionProviderRunner
{
    Task<MissionProviderRunResult> RunAsync(
        AgentProvider provider,
        MissionProviderRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class CommandMissionProviderRunner(ICommandExecutor executor) : IMissionProviderRunner
{
    public async Task<MissionProviderRunResult> RunAsync(
        AgentProvider provider,
        MissionProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await executor.RunAsync(
            MissionProviderCommandBuilder.Build(provider, request),
            cancellationToken).ConfigureAwait(false);
        return new MissionProviderRunResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            new MissionProviderMetadata(
                provider == AgentProvider.Claude ? "claude-print-json" : "codex-exec-json",
                Version: null,
                SessionId: null,
                TurnId: null));
    }
}

internal sealed class RoutingMissionProviderRunner(
    ICommandExecutor executor,
    ICodexAppServerConnectionFactory? connectionFactory = null,
    string? codexTransport = null) : IMissionProviderRunner
{
    private readonly CommandMissionProviderRunner _command = new(executor);
    private readonly CodexAppServerMissionProviderRunner _appServer = new(
        executor,
        connectionFactory ?? new ProcessCodexAppServerConnectionFactory());
    private readonly string _codexTransport = codexTransport
        ?? Environment.GetEnvironmentVariable("FLYT_CODEX_TRANSPORT")
        ?? "app-server";

    public Task<MissionProviderRunResult> RunAsync(
        AgentProvider provider,
        MissionProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (provider != AgentProvider.Codex
            || string.Equals(_codexTransport, "exec", StringComparison.OrdinalIgnoreCase))
        {
            return _command.RunAsync(provider, request, cancellationToken);
        }

        if (!string.Equals(_codexTransport, "app-server", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported FLYT_CODEX_TRANSPORT '{_codexTransport}'. Expected app-server or exec.");
        }

        return _appServer.RunAsync(provider, request, cancellationToken);
    }
}

internal sealed class CodexAppServerMissionProviderRunner(
    ICommandExecutor executor,
    ICodexAppServerConnectionFactory connectionFactory) : IMissionProviderRunner
{
    public async Task<MissionProviderRunResult> RunAsync(
        AgentProvider provider,
        MissionProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (provider != AgentProvider.Codex)
        {
            throw new NotSupportedException("The Codex app-server runner only supports Codex.");
        }

        string? version = null;
        try
        {
            var probe = await executor.RunAsync(
                new CommandInvocation("codex", ["--version"], request.Workspace.WorktreePath),
                cancellationToken).ConfigureAwait(false);
            if (probe.IsSuccess)
            {
                version = FirstNonEmpty(probe.StandardOutput, probe.StandardError);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            version = null;
        }

        await using var connection = await connectionFactory.StartAsync(
            request.Workspace.WorktreePath,
            cancellationToken).ConfigureAwait(false);
        var protocol = new CodexAppServerProtocol(connection);
        var result = await protocol.RunAsync(
            MissionProviderCommandBuilder.BuildPrompt(request),
            request.Workspace.WorktreePath,
            request.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        return result with
        {
            Metadata = result.Metadata with { Version = version },
        };
    }

    private static string? FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

internal sealed class CodexAppServerProtocol(ICodexAppServerConnection connection)
{
    private const int MaximumCapturedCharacters = 64_000;
    private readonly StringBuilder _output = new();
    private string? _threadId;
    private string? _turnId;

    public async Task<MissionProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            var flytVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            await SendAsync(
                new
                {
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        clientInfo = new { name = "flyt", title = "Flyt", version = flytVersion },
                        capabilities = new { },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            await ReadResponseAsync(1, cancellationToken).ConfigureAwait(false);
            await SendAsync(
                new { method = "initialized", @params = new { } },
                cancellationToken).ConfigureAwait(false);

            await SendAsync(
                new
                {
                    id = 2,
                    method = "thread/start",
                    @params = new
                    {
                        cwd = workingDirectory,
                        approvalPolicy = "never",
                        sandbox = readOnly ? "read-only" : "workspace-write",
                        ephemeral = false,
                        threadSource = "user",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var threadResponse = await ReadResponseAsync(2, cancellationToken).ConfigureAwait(false);
            _threadId = RequiredString(threadResponse, "result", "thread", "id");

            await SendAsync(
                new
                {
                    id = 3,
                    method = "turn/start",
                    @params = new
                    {
                        threadId = _threadId,
                        input = new[] { new { type = "text", text = prompt } },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var turnResponse = await ReadResponseAsync(3, cancellationToken).ConfigureAwait(false);
            _turnId = RequiredString(turnResponse, "result", "turn", "id");

            while (true)
            {
                var line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return await FailedAsync(
                        "Codex app-server closed stdout before turn/completed.",
                        cancellationToken).ConfigureAwait(false);
                }

                using var document = Parse(line);
                var root = document.RootElement;
                if (IsServerRequest(root))
                {
                    await RejectServerRequestAsync(root, cancellationToken).ConfigureAwait(false);
                    return await FailedAsync(
                        "Codex requested interactive input or approval; Flyt Mission mode fails closed.",
                        cancellationToken).ConfigureAwait(false);
                }

                CaptureAgentOutput(root);
                if (!MethodEquals(root, "turn/completed"))
                {
                    continue;
                }

                var status = OptionalString(root, "params", "turn", "status") ?? "unknown";
                await connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
                var error = await connection.GetStandardErrorAsync().ConfigureAwait(false);
                var succeeded = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
                return new MissionProviderRunResult(
                    succeeded ? 0 : 1,
                    _output.ToString(),
                    succeeded ? error : Join(error, $"Codex turn ended with status '{status}'."),
                    Metadata());
            }
        }
        catch (OperationCanceledException)
        {
            await InterruptAndStopAsync().ConfigureAwait(false);
            throw;
        }
        catch (JsonException exception)
        {
            return await FailedAsync(
                "Codex app-server emitted malformed JSON: " + exception.Message,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ReadResponseAsync(int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Codex app-server closed stdout before response {id}.");
            using var document = Parse(line);
            var root = document.RootElement;
            if (IsServerRequest(root))
            {
                await RejectServerRequestAsync(root, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Codex requested interactive input or approval during app-server startup.");
            }

            CaptureAgentOutput(root);
            if (!root.TryGetProperty("id", out var responseId)
                || responseId.ValueKind != JsonValueKind.Number
                || responseId.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    "Codex app-server request failed: " + SensitiveDataRedactor.Redact(error.ToString()));
            }

            return root.Clone();
        }
    }

    private async Task RejectServerRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = root.GetProperty("id").Clone();
        await SendAsync(
            new
            {
                id,
                error = new
                {
                    code = -32000,
                    message = "Flyt Mission mode does not support interactive provider requests.",
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InterruptAndStopAsync()
    {
        try
        {
            if (_threadId is not null && _turnId is not null)
            {
                await SendAsync(
                    new
                    {
                        id = 9001,
                        method = "turn/interrupt",
                        @params = new { threadId = _threadId, turnId = _turnId },
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The transport may already be gone. Process termination below remains authoritative.
        }

        await connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<MissionProviderRunResult> FailedAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        var error = await connection.GetStandardErrorAsync().ConfigureAwait(false);
        return new MissionProviderRunResult(1, _output.ToString(), Join(error, message), Metadata());
    }

    private MissionProviderMetadata Metadata() =>
        new("codex-app-server-v2", Version: null, _threadId, _turnId);

    private void CaptureAgentOutput(JsonElement root)
    {
        if (MethodEquals(root, "item/agentMessage/delta"))
        {
            Append(OptionalString(root, "params", "delta"));
            return;
        }

        if (_output.Length > 0 || !MethodEquals(root, "item/completed"))
        {
            return;
        }

        var text = OptionalString(root, "params", "item", "text")
                   ?? OptionalString(root, "params", "item", "message");
        Append(text);
    }

    private void Append(string? value)
    {
        if (string.IsNullOrEmpty(value) || _output.Length >= MaximumCapturedCharacters)
        {
            return;
        }

        var count = Math.Min(value.Length, MaximumCapturedCharacters - _output.Length);
        _output.Append(value.AsSpan(0, count));
    }

    private static JsonDocument Parse(string line) => JsonDocument.Parse(line);

    private static bool IsServerRequest(JsonElement root) =>
        root.TryGetProperty("id", out _)
        && root.TryGetProperty("method", out var method)
        && method.ValueKind == JsonValueKind.String;

    private static bool MethodEquals(JsonElement root, string expected) =>
        root.TryGetProperty("method", out var method)
        && string.Equals(method.GetString(), expected, StringComparison.Ordinal);

    private static string RequiredString(JsonElement root, params string[] path) =>
        OptionalString(root, path)
        ?? throw new InvalidOperationException(
            "Codex app-server response omitted " + string.Join('.', path) + ".");

    private static string? OptionalString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private Task SendAsync(object payload, CancellationToken cancellationToken) =>
        connection.WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken);

    private static string Join(string first, string second) =>
        string.Join(Environment.NewLine, new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal interface ICodexAppServerConnectionFactory
{
    Task<ICodexAppServerConnection> StartAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

internal interface ICodexAppServerConnection : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    Task<string> GetStandardErrorAsync();

    Task StopAsync(CancellationToken cancellationToken = default);
}

internal sealed class ProcessCodexAppServerConnectionFactory : ICodexAppServerConnectionFactory
{
    public Task<ICodexAppServerConnection> StartAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ICodexAppServerConnection>(
            ProcessCodexAppServerConnection.Start(workingDirectory));
    }
}

internal sealed class ProcessCodexAppServerConnection : ICodexAppServerConnection
{
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private bool _stopped;

    private ProcessCodexAppServerConnection(Process process)
    {
        _process = process;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public static ProcessCodexAppServerConnection Start(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start codex app-server.");
        return new ProcessCodexAppServerConnection(process);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
        _process.StandardOutput.ReadLineAsync(cancellationToken);

    Task<string?> ICodexAppServerConnection.ReadLineAsync(CancellationToken cancellationToken) =>
        ReadLineAsync(cancellationToken).AsTask();

    public async Task<string> GetStandardErrorAsync()
    {
        if (!_process.HasExited)
        {
            return string.Empty;
        }

        return await _standardError.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _process.StandardInput.Close();
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _process.Dispose();
    }
}
