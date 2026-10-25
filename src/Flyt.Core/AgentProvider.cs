using System.Text.Json.Serialization;

namespace Flyt.Core;

public enum AgentProvider
{
    Claude,
    Codex,
    [JsonStringEnumMemberName("opencode")]
    OpenCode,
    Shell,
}
