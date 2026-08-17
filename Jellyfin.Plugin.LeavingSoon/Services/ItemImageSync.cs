using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Services;

/// <summary>
/// Points each leaving-soon copy's Primary image at its original item's Primary image
/// file, so the symlink library shows the exact same cover as the original item -
/// including Maintainerr's countdown-bar overlays, which Jellyfin serves straight from
/// that file at request time.
/// </summary>
public class ItemImageSync
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ItemImageSync> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemImageSync"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager (item resolution and persistence).</param>
    /// <param name="fileSystem">The file system (builds file metadata from image paths).</param>
    /// <param name="logger">The logger.</param>
    public ItemImageSync(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<ItemImageSync> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Points each leaving-soon copy's Primary image at its original's Primary image file.
    /// The copy and the original are separate Jellyfin items (Jellyfin does not resolve
    /// symlinks), so the copy never inherits the original's custom/overlaid poster on its
    /// own; repointing the copy's image path makes Jellyfin serve the original's file.
    /// When Maintainerr re-renders an overlay, that file is rewritten and the copy picks
    /// the change up on the next sync without a re-upload or byte copy. The pass is a true
    /// no-op in the steady state: items whose copy already points at the original's file
    /// and whose file was not re-rendered are skipped, so nothing is persisted or pushed
    /// to clients when nothing changed.
    /// </summary>
    /// <remarks>
    /// Never throws: per-item failures are caught and logged so the image pass can never
    /// break the sync that drives it. Items whose copy is not indexed yet (e.g. right
    /// after a brand-new library triggers an asynchronous global scan) are skipped and
    /// picked up by the next poll.
    /// </remarks>
    /// <param name="items">The leaving-soon items for one library.</param>
    /// <param name="symlinkDir">The directory holding this library's symlinks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task ShareOriginalItemImagesAsync(
        IReadOnlyList<LeavingSoonItem> items,
        string symlinkDir,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            try
            {
                await ShareOriginalItemImageAsync(item, symlinkDir, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to share primary image for item {Id}", item.MediaServerId);
            }
        }
    }

    /// <summary>
    /// Resolves the symlink path a copy was created at, mirroring the reconciliation in
    /// <see cref="SyncService.SyncLibraryAsync"/> (movies symlink their containing folder,
    /// shows their series folder, flat movies the file itself).
    /// </summary>
    /// <param name="item">The leaving-soon item.</param>
    /// <param name="symlinkDir">The directory holding this library's symlinks.</param>
    /// <returns>The copy's symlink path, or null when the item has no source path.</returns>
    private static string? ResolveCopyPath(LeavingSoonItem item, string symlinkDir)
    {
        if (string.IsNullOrWhiteSpace(item.SourcePath))
        {
            return null;
        }

        var sourcePath = SyncService.ResolveLinkSource(item.SourcePath);
        return Path.Combine(symlinkDir, Path.GetFileName(sourcePath));
    }

    private async Task ShareOriginalItemImageAsync(LeavingSoonItem item, string symlinkDir, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(item.MediaServerId, out var originalId))
        {
            _logger.LogDebug("Skipping image share for {Id} - invalid media server id", item.MediaServerId);
            return;
        }

        var original = _libraryManager.GetItemById(originalId);
        if (original == null)
        {
            _logger.LogDebug("Skipping image share for {Id} - original item not found", item.MediaServerId);
            return;
        }

        // Only a local file path is shareable: a remote-only primary would be re-fetched
        // from its Path as a URL instead of served from disk.
        var originalImage = original.GetImageInfo(ImageType.Primary, 0);
        if (originalImage is not { IsLocalFile: true } || string.IsNullOrEmpty(originalImage.Path))
        {
            _logger.LogDebug("Skipping image share for {Id} - original has no local primary image", item.MediaServerId);
            return;
        }

        var symlinkPath = ResolveCopyPath(item, symlinkDir);
        if (symlinkPath == null)
        {
            return;
        }

        var copy = FindCopyItem(item, original, symlinkPath);
        if (copy == null)
        {
            _logger.LogDebug("Skipping image share for {Id} - copy at {Path} not indexed yet", item.MediaServerId, symlinkPath);
            return;
        }

        // Only repoint an existing local poster. SetImagePath updates an existing entry in
        // place and preserves its IsLocalFile flag (which is what lets Jellyfin serve the
        // shared file); a poster-less or remote-only copy is left alone.
        var copyImage = copy.GetImageInfo(ImageType.Primary, 0);
        if (copyImage is not { IsLocalFile: true })
        {
            _logger.LogDebug("Skipping image share for {Id} - copy at {Path} has no local primary image", item.MediaServerId, symlinkPath);
            return;
        }

        var imageFile = _fileSystem.GetFileInfo(originalImage.Path);

        // The image cache tags on the stored DateModified, so a re-rendered overlay only
        // surfaces once that value is refreshed from the file. Short-circuit only when the
        // copy already points at the original's file AND that file is unchanged since the
        // last sync; otherwise the shared-path update below refreshes DateModified.
        if (string.Equals(copyImage.Path, originalImage.Path, StringComparison.OrdinalIgnoreCase)
            && copyImage.DateModified == imageFile.LastWriteTimeUtc)
        {
            return;
        }

        copy.SetImagePath(ImageType.Primary, 0, imageFile);

        _logger.LogInformation("Pointing {Path} primary image at {Image}", symlinkPath, originalImage.Path);
        await _libraryManager
            .UpdateItemsAsync([copy], copy.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the indexed copy item for a leaving-soon item. Jellyfin records item paths
    /// against the symlink path (it does not resolve symlinks), so a folder-symlinked movie
    /// is indexed at the video file inside the linked folder, not at the folder itself;
    /// shows and flat movie files are indexed at the symlink path.
    /// </summary>
    /// <param name="item">The leaving-soon item.</param>
    /// <param name="original">The resolved original item (used for the movie file name).</param>
    /// <param name="symlinkPath">The symlink path the copy was created at.</param>
    /// <returns>The indexed copy item, or null when it is not indexed yet.</returns>
    private BaseItem? FindCopyItem(LeavingSoonItem item, BaseItem original, string symlinkPath)
    {
        var isShow = item.Type.Equals("show", StringComparison.OrdinalIgnoreCase);

        if (!isShow && Directory.Exists(symlinkPath))
        {
            // A folder-symlinked movie: the copy is indexed at <symlinkDir>/<folder>/<file>,
            // where the file name matches the original movie's. Fall back to the folder path
            // itself for the rare case where Jellyfin indexed the linked folder as an item.
            var moviePath = Path.Combine(symlinkPath, Path.GetFileName(original.Path));
            return _libraryManager.FindByPath(moviePath, false) ?? _libraryManager.FindByPath(symlinkPath, true);
        }

        return _libraryManager.FindByPath(symlinkPath, Directory.Exists(symlinkPath));
    }
}
