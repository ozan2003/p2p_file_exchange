using System;
using P2PFileTransfer.Core.Services;

namespace P2PFileTransfer.Core.Services.Transfer;

/// <summary>
/// Provides configuration for file transfers.
/// </summary>
public sealed class FileTransferOptions
{
    /// <summary>
    /// The default buffer size in bytes.
    /// </summary>
    public const int DefaultBufferSize = 80 * 1024; // 80 KiB

    /// <summary>
    /// The default TLS handshake timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTlsHandshakeTimeout =
        TimeSpan.FromSeconds(10);

    /// <summary>
    /// The default transfer request timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTransferRequestTimeout =
        TimeSpan.FromMinutes(2);

    /// <summary>
    /// The default chunk size in bytes.
    /// </summary>
    public const int DefaultChunkSize = FileChunker.DefaultChunkSize;

    /// <summary>
    /// The per-chunk size in bytes.
    /// </summary>
    public int ChunkSize { get; set; } = DefaultChunkSize;

    /// <summary>
    /// The buffer size used when reading and writing files.
    /// </summary>
    public int BufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// The maximum duration allowed for TLS handshakes.
    /// </summary>
    public TimeSpan TlsHandshakeTimeout { get; set; } =
        DefaultTlsHandshakeTimeout;

    /// <summary>
    /// The maximum time to wait for a transfer request response.
    /// </summary>
    public TimeSpan TransferRequestTimeout { get; set; } =
        DefaultTransferRequestTimeout;
}
