using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// A source of leaving-soon media items.
/// </summary>
public interface ILeavingSoonProvider
{
    /// <summary>
    /// Gets the provider kind ("maintainerr", "oxicleanarr").
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Fetches the items that are scheduled for deletion from this provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leaving-soon items; an empty list when none are scheduled.</returns>
    Task<IReadOnlyList<LeavingSoonItem>> GetLeavingSoonItemsAsync(CancellationToken cancellationToken);
}
