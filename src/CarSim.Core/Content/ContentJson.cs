using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarSim.Core.Content;

/// <summary>Shared JSON settings for content files: snake_case, comments allowed, unknown fields rejected.</summary>
public static class ContentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
    };
}
