using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LeavingSoon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LeavingSoon.Tests;

/// <summary>
/// Tests for <see cref="SymlinkManager"/> symlink listing and removal, including
/// directory symlinks (shows are symlinked as their series folder).
/// </summary>
public class SymlinkManagerTests
{
    private static SymlinkManager CreateManager() =>
        new(NullLogger<SymlinkManager>.Instance, null!);

    [Fact]
    public void ListSymlinks_ReturnsFileAndDirectorySymlinks()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            var targetFile = Path.Combine(dir, "target.mkv");
            File.WriteAllText(targetFile, "x");
            var targetDir = Path.Combine(dir, "targetfolder");
            Directory.CreateDirectory(targetDir);

            var fileLink = Path.Combine(dir, "Movie.mkv");
            var dirLink = Path.Combine(dir, "Show");
            File.CreateSymbolicLink(fileLink, targetFile);
            Directory.CreateSymbolicLink(dirLink, targetDir);

            var links = CreateManager().ListSymlinks(dir);

            var names = links.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Movie.mkv", "Show" }, names);
            Assert.Contains(links, l => l.Name == "Show" && !string.IsNullOrEmpty(l.Target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ListSymlinks_IgnoresRegularFiles()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "regular.txt"), "x");

            Assert.Empty(CreateManager().ListSymlinks(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemoveSymlink_RemovesDirectorySymlink()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            var targetDir = Path.Combine(dir, "target");
            Directory.CreateDirectory(targetDir);
            var link = Path.Combine(dir, "Show");
            Directory.CreateSymbolicLink(link, targetDir);

            CreateManager().RemoveSymlink(link);

            Assert.False(Directory.Exists(link));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemoveSymlink_RemovesFileSymlink()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            var targetFile = Path.Combine(dir, "target.mkv");
            File.WriteAllText(targetFile, "x");
            var link = Path.Combine(dir, "Movie.mkv");
            File.CreateSymbolicLink(link, targetFile);

            CreateManager().RemoveSymlink(link);

            Assert.False(File.Exists(link));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemoveAllSymlinks_RemovesAllSymlinksAndKeepsRealEntry()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            var targetFile = Path.Combine(dir, "target.mkv");
            File.WriteAllText(targetFile, "x");
            var targetDir = Path.Combine(dir, "targetfolder");
            Directory.CreateDirectory(targetDir);

            File.CreateSymbolicLink(Path.Combine(dir, "Movie.mkv"), targetFile);
            Directory.CreateSymbolicLink(Path.Combine(dir, "Show"), targetDir);
            var realFile = Path.Combine(dir, "notes.txt");
            File.WriteAllText(realFile, "keep");

            CreateManager().RemoveAllSymlinks(dir);

            Assert.False(File.Exists(Path.Combine(dir, "Movie.mkv")));
            Assert.False(Directory.Exists(Path.Combine(dir, "Show")));
            Assert.True(File.Exists(realFile));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemoveAllSymlinks_MissingDirectory_DoesNotThrow()
    {
        CreateManager().RemoveAllSymlinks(Path.Combine(Path.GetTempPath(), "does-not-exist"));
    }

    [Fact]
    public void RemoveSymlink_RemovesDanglingSymlink()
    {
        var dir = Directory.CreateTempSubdirectory("leaving-soon-test-").FullName;
        try
        {
            var targetFile = Path.Combine(dir, "target.mkv");
            File.WriteAllText(targetFile, "x");
            var link = Path.Combine(dir, "Movie.mkv");
            File.CreateSymbolicLink(link, targetFile);
            File.Delete(targetFile); // break the link

            CreateManager().RemoveSymlink(link);

            // File.Exists/Directory.Exists follow the broken target, so they return false
            // even if the link survived; assert on the link itself instead. File.GetAttributes
            // uses lstat and sees the link entry, throwing once the link is actually gone.
            Assert.Throws<FileNotFoundException>(() => File.GetAttributes(link));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
