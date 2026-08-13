using System.Text.Json;

namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// Shared JSON serialization options.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>
    /// Gets the default JSON serializer options used by the providers.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
