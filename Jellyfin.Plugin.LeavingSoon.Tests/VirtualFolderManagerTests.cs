using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LeavingSoon.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LeavingSoon.Tests;

/// <summary>
/// Tests for <see cref="VirtualFolderManager.IsOwnedLocation"/> — the ownership guard
/// that decides whether an uninstall may destroy a library. Being too permissive lets
/// uninstall delete a real (admin-created) library; being too strict strands cleanup.
/// Also covers the cover-refresh helpers: the item-kind gate that mirrors the provider's
/// collage query, and the bounded wait used to defer the cover until items are indexed.
/// </summary>
public class VirtualFolderManagerTests
{
    private static readonly string OwnedPath = "/var/lib/jellyfin/leaving-soon/movies";

    [Fact]
    public void IsOwnedLocation_ExactMatch_ReturnsTrue()
    {
        Assert.True(VirtualFolderManager.IsOwnedLocation(OwnedPath, OwnedPath));
    }

    [Fact]
    public void IsOwnedLocation_ChildPath_ReturnsTrue()
    {
        Assert.True(VirtualFolderManager.IsOwnedLocation(OwnedPath + "/child", OwnedPath));
    }

    [Fact]
    public void IsOwnedLocation_DeepChildPath_ReturnsTrue()
    {
        Assert.True(VirtualFolderManager.IsOwnedLocation(OwnedPath + "/child/grandchild", OwnedPath));
    }

    [Fact]
    public void IsOwnedLocation_SiblingPrefix_ReturnsFalse()
    {
        // /leaving-soon/movies2 must NOT match the owned /leaving-soon/movies.
        Assert.False(VirtualFolderManager.IsOwnedLocation(OwnedPath + "2", OwnedPath));
    }

    [Fact]
    public void IsOwnedLocation_UnrelatedPath_ReturnsFalse()
    {
        Assert.False(VirtualFolderManager.IsOwnedLocation("/media/real-library", OwnedPath));
    }

    [Fact]
    public void IsOwnedLocation_EmptyOwnedPath_FailsClosed()
    {
        // A blank owned path (misconfigured/empty BasePath) must not treat everything as
        // owned — otherwise uninstall would delete any library matching the configured name.
        Assert.False(VirtualFolderManager.IsOwnedLocation("/anything", string.Empty));
        Assert.False(VirtualFolderManager.IsOwnedLocation("/anything", " "));
    }

    [Fact]
    public void IsOwnedLocation_CaseVariant_NotOwnedOnCaseSensitiveSystems()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows filesystems are case-insensitive; OrdinalIgnoreCase is correct there.
        }

        // On Linux/macOS /Leaving-Soon and /leaving-soon are distinct directories.
        Assert.False(VirtualFolderManager.IsOwnedLocation("/media/Leaving-Soon/movies", "/media/leaving-soon/movies"));
    }

    [Fact]
    public void GetIncludeItemTypes_Movies_ReturnsMovieOnly()
    {
        Assert.Equal(new[] { BaseItemKind.Movie }, VirtualFolderManager.GetIncludeItemTypes(CollectionType.movies));
    }

    [Fact]
    public void GetIncludeItemTypes_TvShows_ReturnsSeriesOnly()
    {
        Assert.Equal(new[] { BaseItemKind.Series }, VirtualFolderManager.GetIncludeItemTypes(CollectionType.tvshows));
    }

    [Fact]
    public void GetIncludeItemTypes_Null_ReturnsDefaultMixedSet()
    {
        var kinds = VirtualFolderManager.GetIncludeItemTypes(null);
        Assert.Contains(BaseItemKind.Video, kinds);
        Assert.Contains(BaseItemKind.Movie, kinds);
        Assert.Contains(BaseItemKind.Series, kinds);
    }

    [Fact]
    public async Task WaitForConditionAsync_ConditionAlreadyTrue_ReturnsTrue()
    {
        Assert.True(await VirtualFolderManager.WaitForConditionAsync(() => true, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task WaitForConditionAsync_BecomesTrue_ReturnsTrue()
    {
        var count = 0;
        bool Condition()
        {
            count++;
            return count >= 3;
        }

        Assert.True(await VirtualFolderManager.WaitForConditionAsync(Condition, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task WaitForConditionAsync_TimesOut_ReturnsFalse()
    {
        var start = DateTime.UtcNow;
        var result = await VirtualFolderManager.WaitForConditionAsync(() => false, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.False(result);
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task WaitForConditionAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => VirtualFolderManager.WaitForConditionAsync(() => false, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), cts.Token));
    }
}