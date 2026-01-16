namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Indicates the mode of a file transfer.
/// </summary>
public enum TransferMode
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
