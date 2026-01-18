using System;

namespace P2PFileTransfer.Core.Models.TransferEvents;

/// <summary>
/// Provides data for transfer progress updates.
/// </summary>
public sealed class TransferProgressEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferProgressEventArgs"/> class.
    /// </summary>
    /// <param name="transferId">The transfer identifier.</param>
    /// <param name="mode">The transfer mode.</param>
    /// <param name="progressPercent">The progress percent.</param>
    public TransferProgressEventArgs(
        Guid transferId,
        TransferMode mode,
        int progressPercent
    )
    {
        this.TransferId = transferId;
        this.Mode = mode;
        this.ProgressPercent = progressPercent;
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
    /// The transfer progress percentage (0–100).
    /// </summary>
    public int ProgressPercent { get; }
}
