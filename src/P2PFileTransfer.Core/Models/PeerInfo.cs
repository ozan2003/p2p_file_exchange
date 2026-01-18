using System;

namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Represents a peer discovered on the network.
/// </summary>
public sealed class PeerInfo
{
    /// <summary>
    /// The unique identifier of the peer
    /// </summary>
    public Guid PeerId { get; set; }

    /// <summary>
    /// The display name for the peer.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The IPv4 address of the peer.
    /// </summary>
    public string IPAddress { get; set; } = string.Empty;

    /// <summary>
    /// The TCP port used for file transfers.
    /// </summary>
    public int TcpPort { get; set; }

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
    public string PublicKey { get; set; } = string.Empty;
}
