using System;
using System.IO;

namespace P2PFileExchange.Core.Utilities;

/// <summary>
/// Provides helpers for file paths and storage locations.
/// </summary>
public static class FilePathUtilities
{
    /// <summary>
    /// The default download directory for inbound transfers.
    /// </summary>
    public static string GetDefaultDownloadDirectory()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile
        );
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments
            );
        }

        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Environment.CurrentDirectory;
        }

        return Path.Combine(
            userProfile,
            "Downloads",
            AppConstants.AppDataDirectoryName
        );
    }

    /// <summary>
    /// Returns a unique file path by appending an index when necessary.
    /// </summary>
    /// <param name="path">The preferred path.</param>
    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        int index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(
                directory,
                $"{fileName}_({index}){extension}"
            );
            ++index;
        } while (File.Exists(candidate));

        return candidate;
    }
}
