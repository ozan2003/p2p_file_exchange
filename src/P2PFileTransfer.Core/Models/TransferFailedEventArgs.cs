using System;

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
    /// <param name="mode">The transfer mode.</param>
    /// <param name="errorMessage">The error message.</param>
    public TransferFailedEventArgs(
        Guid transferId,
        TransferMode mode,
        string errorMessage
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
        this.ErrorMessage = errorMessage;
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
    /// The error message.
    /// </summary>
    public string ErrorMessage { get; }
}
