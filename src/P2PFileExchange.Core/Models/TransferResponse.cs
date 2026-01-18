namespace P2PFileExchange.Core.Models;

/// <summary>
/// Represents the receiver's response to a file transfer request.
/// </summary>
public enum TransferResponse : byte
{
    /// <summary>
    /// The transfer request was accepted.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// The transfer request was rejected.
    /// </summary>
    Rejected = 2,
}
