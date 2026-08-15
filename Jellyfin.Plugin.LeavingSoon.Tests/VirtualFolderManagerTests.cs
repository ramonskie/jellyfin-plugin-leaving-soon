using System;
using Jellyfin.Plugin.LeavingSoon.Services;
using Xunit;

namespace Jellyfin.Plugin.LeavingSoon.Tests;

/// <summary>
/// Tests for <see cref="VirtualFolderManager.IsOwnedLocation"/> — the ownership guard
/// that decides whether an uninstall may destroy a library. Being too permissive lets
/// uninstall delete a real (admin-created) library; being too strict strands cleanup.
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
}