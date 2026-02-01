namespace P2PFileExchange.Core.Models;

/// <summary>
/// Represents the trust level of a peer in the TOFU (Trust-On-First-Use) system.
/// </summary>
public enum TrustLevel
{
    /// <summary>
    /// The peer has been seen but not yet trusted by the user.
    /// First contact awaiting user decision.
    /// </summary>
    Unknown,

    /// <summary>
    /// The peer has been verified and trusted by the user.
    /// File transfers are allowed.
    /// </summary>
    Trusted,

    /// <summary>
    /// The peer has been explicitly blocked by the user.
    /// File transfers are denied.
    /// </summary>
    Blocked,
}
