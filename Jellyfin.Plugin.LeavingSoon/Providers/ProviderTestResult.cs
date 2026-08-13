namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// Result of a provider connection test.
/// </summary>
public class ProviderTestResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderTestResult"/> class.
    /// </summary>
    /// <param name="success">Whether the connection test succeeded.</param>
    /// <param name="message">A human-readable description of the outcome.</param>
    public ProviderTestResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    /// <summary>
    /// Gets a value indicating whether the provider endpoint responded successfully.
    /// </summary>
    public bool Success { get; }

    /// <summary>
    /// Gets a human-readable description of the outcome.
    /// </summary>
    public string Message { get; }
}
