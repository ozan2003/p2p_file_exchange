using System;
using System.Net;

namespace P2PFileExchange.Core.Models;

/// <summary>
/// Represents a peer discovered on the network.
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
    /// The SHA-256 fingerprint of the peer's TLS certificate (hex string).
    /// Used for certificate pinning during TLS handshake validation.
    /// </summary>
    public string CertificateFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The verified ECDSA public key of the peer (base64-encoded).
    /// Used for verifying discovery broadcast signatures.
    /// </summary>
    [Obsolete("Use IdentityPublicKey for Ed25519-based identity verification.")]
    public string PublicKey { get; set; } = string.Empty;

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
    /// Gets whether this peer has been verified via TOFU (Trust-On-First-Use).
    /// A verified peer's identity public key has been seen before and matches.
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Gets or sets when this peer's identity was first trusted (TOFU timestamp).
    /// Null if the peer has not been trusted yet.
    /// </summary>
    public DateTimeOffset? FirstTrusted { get; set; }
}
