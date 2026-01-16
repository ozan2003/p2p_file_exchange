using System;

namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Provides data for the transfer started event.
/// </summary>
public sealed class TransferStartedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferStartedEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="mode">The transfer mode.</param>
    /// <param name="metadata">The file metadata.</param>
    /// <param name="remoteEndpoint">The remote endpoint.</param>
    /// <param name="filePath">The local file path used for the transfer.</param>
    public TransferStartedEventArgs(
        Guid transferId,
        TransferMode mode,
        FileMetadata metadata,
        string remoteEndpoint,
        string filePath
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
        this.Metadata = metadata;
        this.RemoteEndpoint = remoteEndpoint;
        this.FilePath = filePath;
    }

    /// <summary>
    /// The transfer identifier.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// The transfer mode.
    /// </summary>
    public TransferMode Mode { get; }

    /// <summary>
    /// The file metadata.
    /// </summary>
    public FileMetadata Metadata { get; }

    /// <summary>
    /// The remote endpoint.
    /// </summary>
    public string RemoteEndpoint { get; }

    /// <summary>
    /// The local file path used for the transfer.
    /// </summary>
    public string FilePath { get; }
}
