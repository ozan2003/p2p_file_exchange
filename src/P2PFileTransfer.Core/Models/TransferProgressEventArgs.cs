namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Provides data for transfer progress updates.
/// </summary>
public sealed class TransferProgressEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferProgressEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="progressPercent">The progress percent.</param>
    public TransferProgressEventArgs(Guid transferId, int progressPercent)
    {
        TransferId = transferId;
        ProgressPercent = progressPercent;
    }

    /// <summary>
    /// Gets the transfer identifier.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// Gets the progress percent.
    /// </summary>
    public int ProgressPercent { get; }
}
