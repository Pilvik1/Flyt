using Flyt.Core;

namespace Flyt.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "Authorization: Bearer [REDACTED]")]
    [InlineData("API_KEY=super-secret-value", "API_KEY=[REDACTED]")]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz123456", "token: [REDACTED]")]
    [InlineData("https://example.test/?access_token=hello&x=1", "access_token=[REDACTED]&x=1")]
    public void Redact_RemovesCommonSecretShapes(string input, string expectedFragment)
    {
        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Contains(expectedFragment, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesPrivateKeyBlocks()
    {
        const string input = "before\n-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----\nafter";

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Equal("before\n[REDACTED]\nafter", redacted);
    }

    [Fact]
    public void Redact_KeepsOnlyTheBoundedTail()
    {
        var input = "prefix" + new string('x', SensitiveDataRedactor.MaximumPersistedTextLength + 10);

        var redacted = SensitiveDataRedactor.Redact(input);

        Assert.Equal(SensitiveDataRedactor.MaximumPersistedTextLength, redacted.Length);
        Assert.DoesNotContain("prefix", redacted, StringComparison.Ordinal);
    }
}
