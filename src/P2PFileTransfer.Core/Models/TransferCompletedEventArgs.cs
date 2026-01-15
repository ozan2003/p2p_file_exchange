namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Provides data for transfer completion.
/// </summary>
public sealed class TransferCompletedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferCompletedEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="direction">The transfer direction.</param>
    /// <param name="filePath">The local file path used for the transfer.</param>
    public TransferCompletedEventArgs(
        Guid transferId,
        TransferDirection direction,
        string filePath
    )
    {
        TransferId = transferId;
        Direction = direction;
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
    /// Gets the local file path used for the transfer.
    /// </summary>
    public string FilePath { get; }
}
