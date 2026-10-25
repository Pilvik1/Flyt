using System.Text.RegularExpressions;

namespace Flyt.Terminal;

public sealed record TerminalViewport(int Width, int Height);

public sealed record TerminalRequirement(int MinimumWidth, int MinimumHeight);

public sealed record TerminalCompatibilityReport(
    Version? PsmuxVersion,
    TerminalRequirement Requirement,
    TerminalViewport? Viewport,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsSupported => Errors.Count == 0;
}

public static partial class TerminalCompatibility
{
    public static readonly Version MinimumPsmuxVersion = new(3, 4, 0);

    public static TerminalRequirement RequirementFor(int paneCount) => paneCount switch
    {
        1 => new TerminalRequirement(80, 24),
        2 => new TerminalRequirement(100, 28),
        3 => new TerminalRequirement(120, 32),
        4 => new TerminalRequirement(120, 36),
        5 or 6 => new TerminalRequirement(160, 40),
        7 or 8 => new TerminalRequirement(200, 48),
        9 or 10 => new TerminalRequirement(220, 50),
        _ => throw new ArgumentOutOfRangeException(nameof(paneCount), "Flyt supports one to ten panes."),
    };

    public static TerminalCompatibilityReport Evaluate(
        string psmuxVersionOutput,
        int paneCount,
        TerminalViewport? viewport)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var version = ParsePsmuxVersion(psmuxVersionOutput);
        var requirement = RequirementFor(paneCount);

        if (version is null)
        {
            errors.Add("Flyt could not parse the installed psmux version. Install psmux 3.4.0 or newer.");
        }
        else if (version < MinimumPsmuxVersion)
        {
            errors.Add($"psmux {version} is unsupported; Flyt requires {MinimumPsmuxVersion} or newer.");
        }

        if (viewport is null)
        {
            warnings.Add("Terminal dimensions are unavailable; viewport fit could not be verified.");
        }
        else if (viewport.Width < requirement.MinimumWidth || viewport.Height < requirement.MinimumHeight)
        {
            errors.Add(
                $"A {paneCount}-pane Desk requires at least {requirement.MinimumWidth}x{requirement.MinimumHeight}; "
                + $"the current viewport is {viewport.Width}x{viewport.Height}.");
        }

        return new TerminalCompatibilityReport(version, requirement, viewport, errors, warnings);
    }

    public static Version? ParsePsmuxVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionPattern().Match(value);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version)
            ? version
            : null;
    }

    public static TerminalViewport? TryGetCurrentViewport()
    {
        if (Console.IsOutputRedirected)
        {
            return null;
        }

        try
        {
            return Console.WindowWidth > 0 && Console.WindowHeight > 0
                ? new TerminalViewport(Console.WindowWidth, Console.WindowHeight)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public sealed record PsmuxPresentationProfile(bool ColorEnabled, bool UnicodeEnabled, bool HighContrast)
{
    public static PsmuxPresentationProfile FromEnvironment()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var ascii = IsEnabled("FLYT_ASCII");
        var highContrast = IsEnabled("FLYT_HIGH_CONTRAST");
        return new PsmuxPresentationProfile(!noColor, !ascii, highContrast);
    }

    private static bool IsEnabled(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
