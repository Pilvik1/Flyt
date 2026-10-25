using Flyt.Core;

namespace Flyt.Tests;

public sealed class DeterministicCommandParserTests
{
    [Fact]
    public void Parse_PreservesQuotedArgumentsWithoutInvokingShell()
    {
        var command = DeterministicCommandParser.Parse(
            "dotnet test \"Flyt.slnx\" --filter 'Category=Fast Tests'",
            Environment.CurrentDirectory);

        Assert.Equal("dotnet", command.FileName);
        Assert.Equal(["test", "Flyt.slnx", "--filter", "Category=Fast Tests"], command.Arguments);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), command.WorkingDirectory);
    }

    [Theory]
    [InlineData("dotnet test | tee output.txt")]
    [InlineData("dotnet test && echo passed")]
    [InlineData("dotnet test > output.txt")]
    [InlineData("dotnet test; echo passed")]
    public void Parse_RejectsShellComposition(string command)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DeterministicCommandParser.Parse(command, Environment.CurrentDirectory));

        Assert.Contains("shell metacharacter", exception.Message, StringComparison.Ordinal);
    }
}
