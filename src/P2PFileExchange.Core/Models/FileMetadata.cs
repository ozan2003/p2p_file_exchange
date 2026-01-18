namespace P2PFileExchange.Core.Models;

/// <summary>
/// Describes a file being transferred.
/// </summary>
public sealed class FileMetadata
{
    /// <summary>
    /// The name of the file.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The file size in bytes.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// The total number of chunks.
    /// </summary>
    public int TotalChunksNumber { get; set; }

    /// <summary>
    /// The chunk size in bytes.
    /// </summary>
    public int ChunkSize { get; set; }
}
