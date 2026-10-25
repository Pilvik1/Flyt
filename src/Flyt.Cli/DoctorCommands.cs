using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static readonly JsonSerializerOptions AcceptanceTemplateJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static async Task<int> RunInteractiveAcceptanceAsync(
        string[] args,
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        IReadOnlyList<ProviderProbe> providerProbes,
        CommandResult psmuxProbe,
        CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Interactive acceptance requires an attached terminal.");
        }

        var name = PsmuxLayoutPlanner.NormalizeSessionName(
            GetOption(args, "--name") ?? $"flyt-acceptance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        var workspace = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
        var allowSmall = HasFlag(args, "--allow-small-terminal");
        var keepDesk = HasFlag(args, "--keep-desk");
        var receiptBase = GetOption(args, "--receipt")
            ?? Path.Combine(StatePaths.StateDirectory, "acceptance", $"acceptance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        var checks = new List<AcceptanceCheck>();
        await EnsureInteractiveCompatibilityAsync(psmux, 2, allowSmall, cancellationToken).ConfigureAwait(false);
        var started = false;
        string? temporaryTemplatePath = null;
        var codexPaneName = "reviewer";
        var hasResumeMetadata = false;
        string[] startArguments =
        [
            "--name", name,
            "--path", workspace,
            "--sessions", "claude:builder,codex:reviewer",
        ];

        if (GetOption(args, "--template") is { } templateReference)
        {
            var (sourceTemplate, _) = await DeskTemplateLoader.LoadAsync(
                templateReference,
                workspace,
                cancellationToken).ConfigureAwait(false);
            var claudePane = sourceTemplate.Panes.FirstOrDefault(value => value.Provider == AgentProvider.Claude)
                ?? throw new ArgumentException("The acceptance template requires a Claude pane.");
            var codexPane = sourceTemplate.Panes.FirstOrDefault(value => value.Provider == AgentProvider.Codex)
                ?? throw new ArgumentException("The acceptance template requires a Codex pane.");
            codexPaneName = codexPane.Name;
            hasResumeMetadata = !string.IsNullOrWhiteSpace(claudePane.ProviderSessionId)
                                && !string.IsNullOrWhiteSpace(codexPane.ProviderSessionId);
            var acceptanceTemplate = new DeskTemplate(
                name,
                [claudePane, codexPane],
                Tags: ["acceptance"],
                MaxConcurrentExpensiveTasks: 1,
                MemoryWarningMegabytes: sourceTemplate.MemoryWarningMegabytes);
            temporaryTemplatePath = Path.Combine(
                Path.GetTempPath(),
                $"flyt-acceptance-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(
                temporaryTemplatePath,
                JsonSerializer.Serialize(acceptanceTemplate, AcceptanceTemplateJsonOptions),
                cancellationToken).ConfigureAwait(false);
            startArguments = ["--name", name, "--path", workspace, "--template", temporaryTemplatePath];
        }

        AnsiConsole.Write(
            new Panel(
                "This disposable two-pane proof launches authenticated Claude and Codex sessions.\n\n"
                + "Inside the Desk:\n"
                + "1. Ask Claude to reply only [aqua]FLYT_CLAUDE_OK[/].\n"
                + "2. Ask Codex to reply only [aqua]FLYT_CODEX_OK[/].\n"
                + "3. Test [aqua]Alt+1/2[/], [aqua]Ctrl+b Z[/], [aqua]Ctrl+b O[/], and a multiline send.\n"
                + "4. Confirm the active border, role labels, and state text are readable.\n"
                + "5. Detach with [aqua]Ctrl+b d[/] to return to this checklist.")
                .Header("[bold aqua]Target-host acceptance[/]")
                .RoundedBorder());
        if (!AnsiConsole.Confirm("Start the disposable acceptance Desk?", defaultValue: true))
        {
            if (temporaryTemplatePath is not null)
            {
                File.Delete(temporaryTemplatePath);
            }

            return 2;
        }

        try
        {
            var startedCode = await StartDeskAsync(
                psmux,
                store,
                providers,
                startArguments,
                cancellationToken).ConfigureAwait(false);
            if (startedCode != 0)
            {
                return startedCode;
            }

            started = true;
            var attachCode = await psmux.AttachAsync(name, cancellationToken).ConfigureAwait(false);
            checks.Add(Check(
                "Interactive psmux attach/detach",
                attachCode == 0,
                attachCode == 0 ? "The operator returned from the live Desk." : $"psmux exited with {attachCode}."));
            checks.Add(ConfirmCheck("Authenticated Claude response", "Claude authenticated and returned FLYT_CLAUDE_OK."));
            checks.Add(ConfirmCheck("Authenticated Codex response", "Codex authenticated and returned FLYT_CODEX_OK."));
            checks.Add(ConfirmCheck("Keyboard navigation and zoom", "Alt+1/2 and zoom/unzoom worked."));
            checks.Add(ConfirmCheck("Popup and multiline send", "The action popup and multiline paste worked without corruption."));
            checks.Add(ConfirmCheck("Visual contrast", "Role/state labels and active/attention borders were readable."));

            await FocusDeskPaneAsync(
                psmux,
                store,
                ["--name", name, "--pane", codexPaneName],
                cancellationToken).ConfigureAwait(false);
            await psmux.StopAsync(name, cancellationToken).ConfigureAwait(false);
            var missingSnapshot = await DeskRuntime.ReconcileAsync(
                psmux,
                store,
                applyStyles: false,
                captureFailures: true,
                cancellationToken).ConfigureAwait(false);
            var detectedMissing = missingSnapshot.Desks.Single(value => value.SessionName == name).Status == DeskStatus.Missing;
            var recoveryCode = await ReopenDeskAsync(
                psmux,
                store,
                providers,
                ["--name", name],
                cancellationToken).ConfigureAwait(false);
            var livePanes = await psmux.ListPanesAsync(name, cancellationToken).ConfigureAwait(false);
            var recoveredDesk = await store.GetDeskAsync(name, cancellationToken).ConfigureAwait(false);
            var recoveryPassed = detectedMissing
                                 && recoveryCode == 0
                                 && livePanes.Count == 2
                                 && livePanes.Select(value => value.PaneId).Distinct(StringComparer.Ordinal).Count() == 2
                                 && string.Equals(recoveredDesk?.FocusedPaneName, codexPaneName, StringComparison.OrdinalIgnoreCase);
            checks.Add(Check(
                "Unexpected psmux loss and recovery",
                recoveryPassed,
                recoveryPassed
                    ? "Missing state was detected; two unique panes and the prior focus were restored."
                    : "Recovery did not preserve the expected two-pane identity/focus contract."));
            if (hasResumeMetadata)
            {
                AnsiConsole.MarkupLine(
                    "[aqua]Resume check:[/] the recovered Desk is reopening both template-backed provider sessions. "
                    + "Detach with [aqua]Ctrl+b d[/] after confirming prior context.");
                var resumedAttachCode = await psmux.AttachAsync(name, cancellationToken).ConfigureAwait(false);
                checks.Add(Check(
                    "Provider-specific resume",
                    resumedAttachCode == 0 && AnsiConsole.Confirm(
                        "Did both providers resume the expected prior sessions?",
                        defaultValue: true),
                    "Both template-backed provider session IDs reopened with their expected prior context."));
            }
            else
            {
                checks.Add(new AcceptanceCheck(
                    "Provider-specific resume",
                    AcceptanceCheckStatus.NotRun,
                    "No provider session IDs were supplied. Re-run with --template pointing to a two-provider template with providerSessionId values."));
            }

            Console.Write("\u001b]9;Flyt target-host notification proof\u001b\\\a");
            var notification = await new DesktopNotifier(CommandExecutor)
                .NotifyAsync("Flyt acceptance", "Confirm that this notification is visible.", cancellationToken)
                .ConfigureAwait(false);
            var visible = notification.NativeAccepted
                          && AnsiConsole.Confirm("Did a native Windows notification appear?", defaultValue: true);
            checks.Add(Check(
                "Human-visible Windows notification",
                visible,
                notification.NativeAccepted
                    ? visible ? "Windows accepted the request and the operator confirmed display." : "Windows accepted the request, but display was not confirmed."
                    : $"Windows rejected the request: {notification.Error ?? "unknown error"}"));

            var profile = PsmuxPresentationProfile.FromEnvironment();
            var receipt = new HostAcceptanceReceipt(
                "1.0",
                DateTimeOffset.UtcNow,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                DetectTerminalHost(),
                OneLine(FirstNonEmpty(psmuxProbe.StandardOutput, psmuxProbe.StandardError)),
                providerProbes.ToDictionary(
                    probe => probe.Provider.ToString(),
                    probe => OneLine(probe.Version ?? (probe.IsAvailable ? "available" : "unavailable")),
                    StringComparer.Ordinal),
                PresentationLabel(profile),
                checks);
            var paths = await HostAcceptanceReceiptWriter.WriteAsync(
                receipt,
                receiptBase,
                cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine(
                $"[{(receipt.Passed ? "green" : "yellow")}]Acceptance {(receipt.Passed ? "passed" : "incomplete or failed")}.[/]");
            AnsiConsole.MarkupLine($"JSON receipt: [aqua]{Markup.Escape(paths.JsonPath)}[/]");
            AnsiConsole.MarkupLine($"Markdown receipt: [aqua]{Markup.Escape(paths.MarkdownPath)}[/]");
            return receipt.Passed ? 0 : 2;
        }
        finally
        {
            if (started && !keepDesk)
            {
                try
                {
                    await StopDeskAsync(
                        psmux,
                        store,
                        ["--name", name],
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AnsiConsole.MarkupLine($"[yellow]Acceptance Desk cleanup warning:[/] {Markup.Escape(exception.Message)}");
                }
            }

            if (temporaryTemplatePath is not null)
            {
                try
                {
                    File.Delete(temporaryTemplatePath);
                }
                catch (IOException exception)
                {
                    AnsiConsole.MarkupLine($"[yellow]Temporary template cleanup warning:[/] {Markup.Escape(exception.Message)}");
                }
            }
        }
    }

    private static AcceptanceCheck ConfirmCheck(string name, string evidence) =>
        Check(name, AnsiConsole.Confirm($"Pass: {name}?", defaultValue: true), evidence);

    private static AcceptanceCheck Check(string name, bool passed, string evidence) =>
        new(name, passed ? AcceptanceCheckStatus.Passed : AcceptanceCheckStatus.Failed, evidence);

    private static string DetectTerminalHost() =>
        Environment.GetEnvironmentVariable("WT_SESSION") is not null
            ? "Windows Terminal"
            : Environment.GetEnvironmentVariable("TERM_PROGRAM") is not null
                ? "TERM_PROGRAM host"
                : "Console host";

    private static string PresentationLabel(PsmuxPresentationProfile profile) =>
        $"{(profile.ColorEnabled ? "colour" : "no-colour")}, "
        + $"{(profile.UnicodeEnabled ? "unicode" : "ASCII")}, "
        + $"{(profile.HighContrast ? "high-contrast" : "standard-contrast")}";

    private static string OneLine(string value) => string.Join(
        ' ',
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
