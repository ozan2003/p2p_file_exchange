namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Describes a file being transferred.
/// </summary>
public sealed class FileMetadata
{
    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// Gets or sets the total number of chunks.
    /// </summary>
    public int TotalChunks { get; set; }

    /// <summary>
    /// Gets or sets the chunk size in bytes.
    /// </summary>
    public int ChunkSize { get; set; }
}
