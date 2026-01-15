namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Represents a chunk of a file being transferred.
/// </summary>
public sealed class FileChunk
{
    /// <summary>
    /// Gets or sets the zero-based chunk index.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Gets or sets the chunk payload.
    /// </summary>
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// Gets or sets the SHA256 hash of the chunk data.
    /// </summary>
    public byte[] Hash { get; set; } = [];
}
