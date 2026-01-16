using System;

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
        this.TransferId = transferId;
        this.ProgressPercent = progressPercent;
    }

    /// <summary>
    /// The transfer identifier.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// The transfer progress percentage (0–100).
    /// </summary>
    public int ProgressPercent { get; }
}
