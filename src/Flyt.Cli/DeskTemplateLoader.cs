using System.Text.Json;
using System.Text.Json.Serialization;
using Flyt.Core;
using Flyt.Providers;

namespace Flyt.Cli;

internal static class DeskTemplateLoader
{
    private static readonly JsonSerializerOptions TemplateJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<(DeskTemplate Template, string Path)> LoadAsync(
        string reference,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var path = Resolve(reference, workspacePath);
        await using var stream = File.OpenRead(path);
        var template = await JsonSerializer.DeserializeAsync<DeskTemplate>(
            stream,
            TemplateJson,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Desk template '{path}' is empty.");
        template.Validate();
        return (template, path);
    }

    public static IReadOnlyList<AgentSessionSpec> BuildSessions(
        DeskTemplate template,
        string workspacePath,
        ProviderRegistry providers) =>
        template.Panes.Select(pane =>
        {
            var workingDirectory = string.IsNullOrWhiteSpace(pane.WorkingDirectory)
                ? workspacePath
                : Path.GetFullPath(pane.WorkingDirectory, workspacePath);
            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException(workingDirectory);
            }

            return AgentSessionSpec.Create(
                pane.Provider,
                pane.Name,
                workingDirectory,
                pane.Command ?? providers.BuildInteractiveCommand(pane.Provider, pane.ProviderSessionId),
                pane.Role,
                pane.Task,
                pane.Color,
                pane.Tags,
                pane.ProviderSessionId);
        }).ToArray();

    public static async Task<string> WriteStarterAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(workspacePath), ".flyt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "desk.json");
        if (File.Exists(path))
        {
            throw new IOException($"Desk template already exists: {path}");
        }

        var template = new DeskTemplate(
            "full-review",
            [
                new DeskTemplatePane(AgentProvider.Claude, "planner", "planner", "Plan the work", Tags: ["planning"]),
                new DeskTemplatePane(AgentProvider.Claude, "builder", "builder", "Implement", Tags: ["write", "expensive"]),
                new DeskTemplatePane(AgentProvider.Codex, "reviewer", "reviewer", "Independent review", Tags: ["review"]),
                new DeskTemplatePane(AgentProvider.Codex, "verifier", "verifier", "Run verification", Tags: ["verify", "expensive"]),
                new DeskTemplatePane(AgentProvider.Shell, "ops", "ops", "Operator shell", Tags: ["shell"]),
            ],
            Tags: ["development", "review"],
            MaxConcurrentExpensiveTasks: 2,
            MemoryWarningMegabytes: 12_000);

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(template, TemplateJson) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        return path;
    }

    public static async Task<string> WriteAsync(
        DeskTemplate template,
        string workspacePath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Validate();
        var directory = Path.Combine(Path.GetFullPath(workspacePath), ".flyt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "desk.json");
        if (File.Exists(path) && !overwrite)
        {
            throw new IOException($"Desk template already exists: {path}. Re-run with --force to replace it.");
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(template, TemplateJson) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string Resolve(string reference, string workspacePath)
    {
        var candidates = new List<string>();
        if (Path.IsPathRooted(reference))
        {
            candidates.Add(reference);
        }
        else
        {
            candidates.Add(Path.GetFullPath(reference, workspacePath));
            candidates.Add(Path.Combine(workspacePath, ".flyt", reference));
            candidates.Add(Path.Combine(workspacePath, ".flyt", $"{reference}.json"));
            if (string.Equals(reference, "default", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(workspacePath, ".flyt", "desk.json"));
            }

            candidates.Add(Path.Combine(StatePaths.StateDirectory, "templates", $"{reference}.json"));
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Desk template '{reference}' was not found. Checked: {string.Join(", ", candidates)}");
    }
}
