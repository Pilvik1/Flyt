namespace Flyt.Core;

public static class MissionReviewParser
{
    public const string PassMarker = "FLYT_REVIEW_PASS";
    public const string BlockMarker = "FLYT_REVIEW_BLOCK";

    public static MissionReviewVerdict Parse(string? output)
    {
        var hasPass = output?.Contains(PassMarker, StringComparison.Ordinal) == true;
        var hasBlock = output?.Contains(BlockMarker, StringComparison.Ordinal) == true;
        return (hasPass, hasBlock) switch
        {
            (true, false) => MissionReviewVerdict.Pass,
            (false, true) => MissionReviewVerdict.Block,
            _ => MissionReviewVerdict.Invalid,
        };
    }
}
