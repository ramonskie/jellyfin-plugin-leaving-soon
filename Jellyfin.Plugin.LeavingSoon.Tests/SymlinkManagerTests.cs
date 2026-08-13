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
}
