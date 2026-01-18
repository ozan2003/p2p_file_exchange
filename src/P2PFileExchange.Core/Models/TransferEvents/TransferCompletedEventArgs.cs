using System;

namespace P2PFileExchange.Core.Models.TransferEvents;

/// <summary>
/// Provides data for transfer completion.
/// </summary>
public sealed class TransferCompletedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferCompletedEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="mode">The transfer mode.</param>
    /// <param name="filePath">The local file path used for the transfer.</param>
    public TransferCompletedEventArgs(
        Guid transferId,
        TransferMode mode,
        string filePath
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
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
    /// The local file path used for the transfer.
    /// </summary>
    public string FilePath { get; }
}
