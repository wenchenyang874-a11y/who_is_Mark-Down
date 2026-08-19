using System.Text.Json;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Validates a task-toggle message emitted by WIMD's host script. The source line
/// is only a lookup hint; the editor separately verifies real Markdown task syntax
/// before changing one state character.
/// </summary>
public sealed record PreviewTaskToggleRequest(int RequestId, int SourceLine, bool IsCompleted)
{
    public const int MaximumSourceLine = 10_000_000;

    public static bool TryCreate(JsonElement root, out PreviewTaskToggleRequest? request)
    {
        request = null;
        if (!root.TryGetProperty("requestId", out JsonElement requestIdElement)
            || !requestIdElement.TryGetInt32(out int requestId)
            || requestId <= 0
            || !root.TryGetProperty("sourceLine", out JsonElement sourceLineElement)
            || !sourceLineElement.TryGetInt32(out int sourceLine)
            || sourceLine is < 0 or > MaximumSourceLine
            || !root.TryGetProperty("completed", out JsonElement completedElement)
            || completedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        request = new PreviewTaskToggleRequest(requestId, sourceLine, completedElement.GetBoolean());
        return true;
    }
}

