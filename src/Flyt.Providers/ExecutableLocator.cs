namespace Flyt.Providers;

public static class ExecutableLocator
{
    public static string? Find(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (Path.IsPathRooted(executableName) && File.Exists(executableName))
        {
            return Path.GetFullPath(executableName);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var extensions = GetExecutableExtensions(executableName);
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(cleanDirectory, executableName + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string[] GetExecutableExtensions(string executableName)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(executableName))
        {
            return [string.Empty];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        return string.IsNullOrWhiteSpace(pathExt)
            ? [".exe", ".cmd", ".bat"]
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }
}
