using System.Text;

namespace Flyt.Core;

public static class DeterministicCommandParser
{
    private static readonly HashSet<char> ShellMetacharacters = ['|', '&', ';', '>', '<', '`'];

    public static CommandInvocation Parse(string command, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (var character in command)
        {
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (ShellMetacharacters.Contains(character))
            {
                throw new ArgumentException(
                    $"Verification commands cannot contain shell metacharacter '{character}'.",
                    nameof(command));
            }

            if (char.IsWhiteSpace(character))
            {
                AddToken(tokens, current);
                continue;
            }

            current.Append(character);
        }

        if (quote is not null)
        {
            throw new ArgumentException("Verification command contains an incomplete quote or escape.", nameof(command));
        }

        AddToken(tokens, current);
        if (tokens.Count == 0)
        {
            throw new ArgumentException("Verification command is empty.", nameof(command));
        }

        return new CommandInvocation(tokens[0], tokens.Skip(1).ToArray(), Path.GetFullPath(workingDirectory));
    }

    private static void AddToken(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}
