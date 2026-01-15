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
    /// <param name="direction">The transfer direction.</param>
    /// <param name="metadata">The file metadata.</param>
    /// <param name="remoteEndpoint">The remote endpoint.</param>
    /// <param name="filePath">The local file path used for the transfer.</param>
    public TransferStartedEventArgs(
        Guid transferId,
        TransferDirection direction,
        FileMetadata metadata,
        string remoteEndpoint,
        string filePath
    )
    {
        TransferId = transferId;
        Direction = direction;
        Metadata = metadata;
        RemoteEndpoint = remoteEndpoint;
        FilePath = filePath;
    }

    /// <summary>
    /// Gets the transfer identifier.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// Gets the transfer direction.
    /// </summary>
    public TransferDirection Direction { get; }

    /// <summary>
    /// Gets the file metadata.
    /// </summary>
    public FileMetadata Metadata { get; }

    /// <summary>
    /// Gets the remote endpoint.
    /// </summary>
    public string RemoteEndpoint { get; }

    /// <summary>
    /// Gets the local file path used for the transfer.
    /// </summary>
    public string FilePath { get; }
}
