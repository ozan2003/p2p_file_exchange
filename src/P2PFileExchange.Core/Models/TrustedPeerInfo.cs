using System;

namespace P2PFileExchange.Core.Models;

/// <summary>
/// Represents trust metadata for a peer, stored in the TOFU database.
/// This contains only database-specific trust data; runtime network info is in <see cref="PeerInfo"/>.
/// </summary>
public sealed record TrustedPeerInfo
{
    /// <summary>
    /// The unique peer identifier derived from the Ed25519 public key.
    /// </summary>
    public required Guid PeerId { get; init; }

    /// <summary>
    /// The Ed25519 public key of the peer (32 bytes).
    /// Used for cryptographic identity verification.
    /// </summary>
    public required byte[] Ed25519PublicKey { get; init; }

    /// <summary>
    /// The SHA-256 fingerprint of the public key, formatted as hex with 4-char groups.
    /// Example: "F3A7 B82C 91D4 E6F5 2C8A 4E91 7B3D 6F2E ..."
    /// </summary>
    public required string PublicKeyFingerprint { get; init; }

    /// <summary>
    /// The short fingerprint (first 8 hex characters) for compact display.
    /// Example: "F3A7B82C"
    /// </summary>
    public string ShortFingerprint =>
        this.PublicKeyFingerprint.Length >= 9
            ? this.PublicKeyFingerprint[..9].Replace(" ", "")
            : this.PublicKeyFingerprint.Replace(" ", "");

    /// <summary>
    /// The trust level assigned to this peer.
    /// </summary>
    public required TrustLevel TrustLevel { get; init; }

    /// <summary>
    /// The timestamp when this peer was first trusted.
    /// </summary>
    public required DateTimeOffset FirstTrusted { get; init; }

    /// <summary>
    /// The timestamp when this peer was last seen online.
    /// </summary>
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>
    /// The number of successful file transfers with this peer.
    /// </summary>
    public int TransferCount { get; init; }

    /// <summary>
    /// The number of failed file transfers with this peer.
    /// </summary>
    public int FailedTransferCount { get; init; }

    /// <summary>
    /// Optional notes about this peer (e.g., "Work laptop", "Friend's phone").
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// The last known display name (cached from discovery, may be stale).
    /// Use <see cref="PeerInfo.DisplayName"/> for current name when peer is online.
    /// </summary>
    public string? CachedDisplayName { get; init; }
}
