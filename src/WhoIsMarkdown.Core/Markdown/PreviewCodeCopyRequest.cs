using System.Text.Json;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Represents code copied from a host-enhanced preview block. The host validates
/// message shape and size before the text reaches the Windows clipboard boundary.
/// </summary>
public sealed record PreviewCodeCopyRequest(int RequestId, string Code)
{
    public const int MaximumCodeLength = 4 * 1024 * 1024;

    public static bool TryCreate(JsonElement root, out PreviewCodeCopyRequest? request)
    {
        request = null;
        if (!root.TryGetProperty("requestId", out JsonElement requestIdElement)
            || !requestIdElement.TryGetInt32(out int requestId)
            || requestId <= 0
            || !root.TryGetProperty("code", out JsonElement codeElement)
            || codeElement.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        string? code = codeElement.GetString();
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumCodeLength)
        {
            return false;
        }

        request = new PreviewCodeCopyRequest(requestId, code);
        return true;
    }
}
