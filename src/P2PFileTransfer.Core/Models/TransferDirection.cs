namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Indicates the direction of a file transfer.
/// </summary>
public enum TransferDirection
{
    /// <summary>
    /// Data is being sent to a peer.
    /// </summary>
    Send,

    /// <summary>
    /// Data is being received from a peer.
    /// </summary>
    Receive,
}
