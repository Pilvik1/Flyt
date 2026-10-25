using Flyt.Core;

namespace Flyt.Tests;

public sealed class MissionReviewParserTests
{
    [Theory]
    [InlineData("looks good\nFLYT_REVIEW_PASS", MissionReviewVerdict.Pass)]
    [InlineData("issue found\nFLYT_REVIEW_BLOCK", MissionReviewVerdict.Block)]
    [InlineData("looks good", MissionReviewVerdict.Invalid)]
    [InlineData("FLYT_REVIEW_PASS FLYT_REVIEW_BLOCK", MissionReviewVerdict.Invalid)]
    public void Parse_FailsClosedOnAmbiguousOutput(string output, MissionReviewVerdict expected)
    {
        Assert.Equal(expected, MissionReviewParser.Parse(output));
    }
}
