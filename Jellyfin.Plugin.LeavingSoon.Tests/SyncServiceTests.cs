using System;
using System.IO;
using Jellyfin.Plugin.LeavingSoon.Services;
using Xunit;

namespace Jellyfin.Plugin.LeavingSoon.Tests;

/// <summary>
/// Tests for <see cref="SyncService.ResolveLinkSource"/> — movies symlink their
/// containing folder (like shows symlink their series folder), with a safe fallback
/// to the file when the movie has no dedicated folder.
/// </summary>
public class SyncServiceTests
{
    [Fact]
    public void ResolveLinkSource_MovieFileInDedicatedFolder_ReturnsFolder()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-svc-").FullName;
        try
        {
            var movieDir = Path.Combine(dir, "Movie (2020)");
            Directory.CreateDirectory(movieDir);
            var file = Path.Combine(movieDir, "Movie (2020).mkv");
            File.WriteAllText(file, "x");

            Assert.Equal(movieDir, SyncService.ResolveLinkSource(file));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveLinkSource_MovieFileInFolderWithSubdirectories_FallsBackToFile()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-svc-").FullName;
        try
        {
            var movieDir = Path.Combine(dir, "Movie (2020)");
            Directory.CreateDirectory(Path.Combine(movieDir, "Extras"));
            var file = Path.Combine(movieDir, "Movie (2020).mkv");
            File.WriteAllText(file, "x");

            // Folder has subdirectories -> could be a library root, so keep the file.
            Assert.Equal(file, SyncService.ResolveLinkSource(file));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveLinkSource_DirectoryPath_ReturnsAsIs()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-svc-").FullName;
        try
        {
            Assert.Equal(dir, SyncService.ResolveLinkSource(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolveLinkSource_NonExistentPath_ReturnsAsIs()
    {
        Assert.Equal("/media/nope.mkv", SyncService.ResolveLinkSource("/media/nope.mkv"));
    }
}
