using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Providers;
using Jellyfin.Plugin.LeavingSoon.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LeavingSoon.Tests;

/// <summary>
/// Tests for <see cref="ItemImageSync"/> - the pass that points each leaving-soon copy's
/// Primary image at its original item's Primary image file so the symlink library shows
/// the same cover (including Maintainerr overlays) as the original.
/// </summary>
public class ItemImageSyncTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IFileSystem> _fileSystemMock = new();

    public ItemImageSyncTests()
    {
        // SetImagePath reads the file's last-write time through BaseItem.FileSystem.
        BaseItem.FileSystem = _fileSystemMock.Object;
        _fileSystemMock.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<FileSystemMetadata>())).Returns((FileSystemMetadata m) => m.LastWriteTimeUtc);
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_OriginalHasLocalImage_RepointsCopyPrimaryToOriginalPath()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            // A folder-symlinked movie: the copy is indexed at <symlinkDir>/<folder>/<file>.
            Directory.CreateDirectory(Path.Combine(symlinkDir, "Movie (2020)"));
            var originalMovieFile = Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv");
            File.WriteAllText(originalMovieFile, "x");

            var original = new TestItem
            {
                Id = Guid.NewGuid(),
                Path = originalMovieFile,
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/original/poster.jpg" }
                ],
            };
            var copy = new TestItem
            {
                Id = Guid.NewGuid(),
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/copy/poster.jpg" }
                ],
            };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);
            _libraryManagerMock.Setup(m => m.FindByPath(Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv"), false)).Returns(copy);
            _fileSystemMock.Setup(f => f.GetFileInfo("/var/lib/jellyfin/metadata/original/poster.jpg"))
                .Returns(new FileSystemMetadata { FullName = "/var/lib/jellyfin/metadata/original/poster.jpg", LastWriteTimeUtc = DateTime.UtcNow, Exists = true });

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "movie",
                SourcePath = originalMovieFile,
            };

            await CreateSut().ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            Assert.Equal("/var/lib/jellyfin/metadata/original/poster.jpg", copy.GetImageInfo(ImageType.Primary, 0)?.Path);
            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(
                    It.Is<IReadOnlyList<BaseItem>>(l => l.Contains(copy)),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.ImageUpdate,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_Show_RepointsSeriesCopyPrimary()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            // Shows are symlinked as their series folder and indexed at the folder path.
            Directory.CreateDirectory(Path.Combine(symlinkDir, "ShowName"));

            var original = new TestItem
            {
                Id = Guid.NewGuid(),
                Path = "/media/ShowName",
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/original/series-poster.jpg" }
                ],
            };
            var copy = new TestItem
            {
                Id = Guid.NewGuid(),
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/copy/series-poster.jpg" }
                ],
            };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);
            _libraryManagerMock.Setup(m => m.FindByPath(Path.Combine(symlinkDir, "ShowName"), true)).Returns(copy);
            _fileSystemMock.Setup(f => f.GetFileInfo("/var/lib/jellyfin/metadata/original/series-poster.jpg"))
                .Returns(new FileSystemMetadata { FullName = "/var/lib/jellyfin/metadata/original/series-poster.jpg", LastWriteTimeUtc = DateTime.UtcNow, Exists = true });

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "show",
                SourcePath = "/media/ShowName",
            };

            await CreateSut().ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            Assert.Equal("/var/lib/jellyfin/metadata/original/series-poster.jpg", copy.GetImageInfo(ImageType.Primary, 0)?.Path);
            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(
                    It.Is<IReadOnlyList<BaseItem>>(l => l.Contains(copy)),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.ImageUpdate,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_OriginalHasNoImage_NothingSet()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            var original = new TestItem { Id = Guid.NewGuid(), ImageInfos = [] };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "movie",
                SourcePath = "/media/Movie (2020).mkv",
            };

            await CreateSut().ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_CopyNotFound_SkipsWithoutThrowing()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            var original = new TestItem
            {
                Id = Guid.NewGuid(),
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/original/poster.jpg" }
                ],
            };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);
            _libraryManagerMock.Setup(m => m.FindByPath(It.IsAny<string>(), It.IsAny<bool?>())).Returns((BaseItem?)null);

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "movie",
                SourcePath = "/media/Movie (2020).mkv",
            };

            await CreateSut().ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_UnchangedCopy_IsNotReSaved()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(symlinkDir, "Movie (2020)"));
            var originalMovieFile = Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv");
            File.WriteAllText(originalMovieFile, "x");

            var original = new TestItem
            {
                Id = Guid.NewGuid(),
                Path = originalMovieFile,
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/original/poster.jpg" }
                ],
            };
            var copy = new TestItem
            {
                Id = Guid.NewGuid(),
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/copy/poster.jpg" }
                ],
            };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);
            _libraryManagerMock.Setup(m => m.FindByPath(Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv"), false)).Returns(copy);
            var overlayTime = DateTime.UtcNow;
            _fileSystemMock.Setup(f => f.GetFileInfo("/var/lib/jellyfin/metadata/original/poster.jpg"))
                .Returns(new FileSystemMetadata { FullName = "/var/lib/jellyfin/metadata/original/poster.jpg", LastWriteTimeUtc = overlayTime, Exists = true });

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "movie",
                SourcePath = originalMovieFile,
            };

            var sut = CreateSut();
            await sut.ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);
            await sut.ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            // Steady state is a true no-op: the copy already points at the original's file
            // and the file was not re-rendered, so only the first run persisted anything.
            Assert.Equal("/var/lib/jellyfin/metadata/original/poster.jpg", copy.GetImageInfo(ImageType.Primary, 0)?.Path);
            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(
                    It.Is<IReadOnlyList<BaseItem>>(l => l.Contains(copy)),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.ImageUpdate,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    [Fact]
    public async Task ShareOriginalItemImagesAsync_ReRenderedOverlay_RefreshesDateModified()
    {
        var symlinkDir = Directory.CreateTempSubdirectory("leaving-soon-img-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(symlinkDir, "Movie (2020)"));
            var originalMovieFile = Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv");
            File.WriteAllText(originalMovieFile, "x");

            var original = new TestItem
            {
                Id = Guid.NewGuid(),
                Path = originalMovieFile,
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/original/poster.jpg" }
                ],
            };
            var copy = new TestItem
            {
                Id = Guid.NewGuid(),
                ImageInfos =
                [
                    new ItemImageInfo { Type = ImageType.Primary, Path = "/var/lib/jellyfin/metadata/copy/poster.jpg" }
                ],
            };

            _libraryManagerMock.Setup(m => m.GetItemById(original.Id)).Returns(original);
            _libraryManagerMock.Setup(m => m.FindByPath(Path.Combine(symlinkDir, "Movie (2020)", "Movie (2020).mkv"), false)).Returns(copy);

            var firstRender = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var reRender = firstRender.AddMinutes(5);
            _fileSystemMock.Setup(f => f.GetFileInfo("/var/lib/jellyfin/metadata/original/poster.jpg"))
                .Returns(new FileSystemMetadata { FullName = "/var/lib/jellyfin/metadata/original/poster.jpg", LastWriteTimeUtc = firstRender, Exists = true });

            var item = new LeavingSoonItem
            {
                MediaServerId = original.Id.ToString(),
                Type = "movie",
                SourcePath = originalMovieFile,
            };

            var sut = CreateSut();
            await sut.ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            // Maintainerr re-renders the overlay in place: same path, new file mtime.
            _fileSystemMock.Setup(f => f.GetFileInfo("/var/lib/jellyfin/metadata/original/poster.jpg"))
                .Returns(new FileSystemMetadata { FullName = "/var/lib/jellyfin/metadata/original/poster.jpg", LastWriteTimeUtc = reRender, Exists = true });
            await sut.ShareOriginalItemImagesAsync([item], symlinkDir, CancellationToken.None);

            // The stored DateModified is refreshed so the image cache invalidates; the copy
            // keeps pointing at the same (rewritten) file.
            Assert.Equal("/var/lib/jellyfin/metadata/original/poster.jpg", copy.GetImageInfo(ImageType.Primary, 0)?.Path);
            Assert.Equal(reRender, copy.GetImageInfo(ImageType.Primary, 0)?.DateModified);
            _libraryManagerMock.Verify(
                m => m.UpdateItemsAsync(
                    It.Is<IReadOnlyList<BaseItem>>(l => l.Contains(copy)),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.ImageUpdate,
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }
        finally
        {
            Directory.Delete(symlinkDir, true);
        }
    }

    private ItemImageSync CreateSut() =>
        new(_libraryManagerMock.Object, _fileSystemMock.Object, NullLogger<ItemImageSync>.Instance);

    private sealed class TestItem : BaseItem
    {
    }
}
