namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Provides data for transfer failure.
/// </summary>
public sealed class TransferFailedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferFailedEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="direction">The transfer direction.</param>
    /// <param name="errorMessage">The error message.</param>
    public TransferFailedEventArgs(
        Guid transferId,
        TransferDirection direction,
        string errorMessage
    )
    {
        TransferId = transferId;
        Direction = direction;
        ErrorMessage = errorMessage;
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
    /// Gets the error message.
    /// </summary>
    public string ErrorMessage { get; }
}
