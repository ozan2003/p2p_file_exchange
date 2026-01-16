namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Represents a chunk of a file being transferred.
/// </summary>
public sealed class FileChunk
{
    /// <summary>
    /// The zero-based chunk index.
    /// </summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// The data being chunked.
    /// </summary>
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// The SHA256 hash of the chunk data.
    /// </summary>
    public byte[] Hash { get; set; } = [];
}
