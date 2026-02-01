using System;
using System.Net;

namespace P2PFileExchange.Core.Models;

/// <summary>
/// Represents a peer discovered on the network (runtime state).
/// For trust-related data, see <see cref="TrustInfo"/>.
/// </summary>
public sealed class PeerInfo
{
    /// <summary>
    /// The unique identifier of the peer, derived from the first 16 bytes
    /// of the SHA-256 hash of the peer's Ed25519 identity public key.
    /// This provides a stable identity that persists across sessions.
    /// </summary>
    public Guid PeerId { get; set; }

    /// <summary>
    /// The display name for the peer.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The IPv4 address of the peer.
    /// </summary>
    public IPAddress IPAddress { get; set; } = IPAddress.None;

    /// <summary>
    /// The TCP port used for file transfers.
    /// </summary>
    public ushort TcpPort { get; set; }

    /// <summary>
    /// The last time the peer was seen.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// The Ed25519 identity public key of the peer (base64-encoded, 32 bytes).
    /// Used for verifying discovery broadcast signatures and deriving the PeerId.
    /// </summary>
    public string IdentityPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// The formatted fingerprint of the peer's identity public key for display.
    /// Format: "F3A7 B82C 91D4 E6F5 2C8A 4E91 7B3D 6F2E" (4-char hex groups).
    /// </summary>
    public string IdentityFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Trust metadata from the TOFU database. Null if peer is not yet in database.
    /// Set by PeerTrustService after verifying the peer.
    /// </summary>
    public TrustedPeerInfo? TrustInfo { get; set; }

    /// <summary>
    /// Gets whether this peer is explicitly trusted (TrustLevel.Trusted).
    /// </summary>
    public bool IsTrusted => this.TrustInfo?.TrustLevel == TrustLevel.Trusted;

    /// <summary>
    /// Gets whether this peer is blocked (TrustLevel.Blocked).
    /// </summary>
    public bool IsBlocked => this.TrustInfo?.TrustLevel == TrustLevel.Blocked;

    /// <summary>
    /// Gets whether this peer is unknown (not in database or TrustLevel.Unknown).
    /// </summary>
    public bool IsUnknown =>
        this.TrustInfo is null
        || this.TrustInfo.TrustLevel == TrustLevel.Unknown;
}
