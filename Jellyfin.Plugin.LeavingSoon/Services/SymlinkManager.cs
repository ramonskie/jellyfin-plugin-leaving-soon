using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Services;

/// <summary>
/// Service for managing symlinks within Jellyfin's filesystem context.
/// </summary>
public class SymlinkManager
{
    private readonly ILogger<SymlinkManager> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SymlinkManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    public SymlinkManager(ILogger<SymlinkManager> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Ensures a directory exists, creating it if necessary.
    /// </summary>
    /// <param name="directoryPath">The directory path to ensure exists.</param>
    /// <returns>True if the directory was created, false if it already existed.</returns>
    public bool EnsureDirectoryExists(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be empty", nameof(directoryPath));
        }

        ValidatePath(directoryPath, nameof(directoryPath));

        if (Directory.Exists(directoryPath))
        {
            _logger.LogDebug("Directory already exists: {Directory}", directoryPath);
            return false;
        }

        _logger.LogInformation("Creating directory: {Directory}", directoryPath);
        Directory.CreateDirectory(directoryPath);
        return true;
    }

    /// <summary>
    /// Creates a symlink to a media item.
    /// </summary>
    /// <param name="sourcePath">The source media file path.</param>
    /// <param name="targetDirectory">The target directory for the symlink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the created symlink.</returns>
    public Task<string> CreateSymlinkAsync(string sourcePath, string targetDirectory, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Reserved for future use
        ValidatePath(sourcePath, nameof(sourcePath));
        ValidatePath(targetDirectory, nameof(targetDirectory));

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        }

        // Ensure target directory exists (fallback behavior)
        EnsureDirectoryExists(targetDirectory);

        var fileName = Path.GetFileName(sourcePath);
        var symlinkPath = Path.Combine(targetDirectory, fileName);

        // If symlink already exists, remove it
        if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
        {
            _logger.LogInformation("Removing existing symlink: {Path}", symlinkPath);
            if (Directory.Exists(symlinkPath) && !File.Exists(symlinkPath))
            {
                Directory.Delete(symlinkPath);
            }
            else
            {
                File.Delete(symlinkPath);
            }
        }

        _logger.LogInformation("Creating symlink: {Source} -> {Target}", sourcePath, symlinkPath);

        // Create symlink (Unix-specific, Windows requires different approach)
        File.CreateSymbolicLink(symlinkPath, sourcePath);
        _logger.LogInformation("Successfully created symlink: {SymlinkPath} pointing to {SourcePath}", symlinkPath, sourcePath);
        return Task.FromResult(symlinkPath);
    }

    /// <summary>
    /// Removes a symlink.
    /// </summary>
    /// <param name="symlinkPath">The symlink path to remove.</param>
    /// <exception cref="InvalidOperationException">Thrown when the path exists but is not a symlink.</exception>
    public void RemoveSymlink(string symlinkPath)
    {
        ValidatePath(symlinkPath, nameof(symlinkPath));

        var fileInfo = new FileInfo(symlinkPath);
        var dirInfo = new DirectoryInfo(symlinkPath);

        // File.Exists / Directory.Exists follow the link target, so a dangling symlink
        // (target already removed) may report as non-existent on some platforms. Detect
        // it by its reparse-point attribute instead and remove the link itself.
        var isPresent = File.Exists(symlinkPath) || Directory.Exists(symlinkPath);
        var isReparsePoint = TryGetReparsePoint(fileInfo, dirInfo);
        if (!isReparsePoint)
        {
            if (!isPresent)
            {
                _logger.LogWarning("Path does not exist: {Path}", symlinkPath);
                return;
            }

            _logger.LogError("Refusing to delete {Path}: not a symlink", symlinkPath);
            throw new InvalidOperationException($"Path is not a symlink: {symlinkPath}");
        }

        _logger.LogInformation("Removing symlink: {Path}", symlinkPath);
        if (Directory.Exists(symlinkPath) && !File.Exists(symlinkPath))
        {
            Directory.Delete(symlinkPath);
        }
        else
        {
            // Works for file symlinks, directory symlinks, and dangling links alike.
            File.Delete(symlinkPath);
        }
    }

    /// <summary>
    /// Removes a directory if it exists.
    /// </summary>
    /// <param name="directoryPath">The directory path to remove.</param>
    /// <param name="force">If true, removes the directory even if not empty.</param>
    public void RemoveDirectory(string directoryPath, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be empty", nameof(directoryPath));
        }

        ValidatePath(directoryPath, nameof(directoryPath));

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory does not exist: {Directory}", directoryPath);
            return;
        }

        var hasContent = Directory.GetFiles(directoryPath).Length > 0
            || Directory.GetDirectories(directoryPath).Length > 0;

        if (hasContent && !force)
        {
            throw new InvalidOperationException(
                $"Directory is not empty: {directoryPath}. Use force=true to remove anyway.");
        }

        Directory.Delete(directoryPath, recursive: force);
    }

    /// <summary>
    /// Removes every symlink inside a directory. Used by uninstall cleanup to drop all
    /// links the plugin created. Unrelated entries (real files/folders) are left alone.
    /// </summary>
    /// <param name="directory">The directory whose symlinks should be removed.</param>
    public void RemoveAllSymlinks(string directory)
    {
        foreach (var link in ListSymlinks(directory))
        {
            try
            {
                RemoveSymlink(link.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove symlink {Path}", link.Path);
            }
        }
    }

    /// <summary>
    /// Lists all symlinks in a directory.
    /// </summary>
    /// <param name="directory">The directory to list symlinks from.</param>
    /// <returns>An array of symlink information.</returns>
    public SymlinkInfo[] ListSymlinks(string directory)
    {
        ValidatePath(directory, nameof(directory));

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Directory does not exist: {Directory}", directory);
            return [];
        }

        var symlinks = new List<SymlinkInfo>();
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var fileInfo = new FileInfo(entry);
            var dirInfo = new DirectoryInfo(entry);
            if (!TryGetReparsePoint(fileInfo, dirInfo))
            {
                continue;
            }

            try
            {
                symlinks.Add(new SymlinkInfo
                {
                    Path = entry,
                    Target = fileInfo.LinkTarget ?? dirInfo.LinkTarget ?? "unknown",
                    Name = Path.GetFileName(entry),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read symlink target for: {File}", entry);
            }
        }

        return symlinks.ToArray();
    }

    /// <summary>
    /// Validates that a path does not contain traversal sequences.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <param name="paramName">The parameter name for error messages.</param>
    private static void ValidatePath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty", paramName);
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path traversal detected in {paramName}: {path}");
        }
    }

    private static bool TryGetReparsePoint(FileInfo fileInfo, DirectoryInfo dirInfo)
    {
        try
        {
            return fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            // Some platforms throw when stat'ing a broken link or a path that vanished
            // mid-check; treat that as "not a symlink we can reason about".
            return false;
        }
    }
}

/// <summary>
/// Information about a symlink.
/// </summary>
public class SymlinkInfo
{
    /// <summary>
    /// Gets or sets the full path to the symlink.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target path the symlink points to.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filename of the symlink.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
