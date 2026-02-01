using System;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Coordinates peer trust workflows, including new peer verification,
/// key mismatch handling, and trust management.
/// </summary>
public interface IPeerTrustService : IAsyncDisposable
{
    /// <summary>
    /// Raised when a new peer requires trust verification from the user.
    /// </summary>
    event EventHandler<NewPeerTrustEventArgs>? NewPeerDetected;

    /// <summary>
    /// Raised when a key mismatch is detected for a previously trusted peer.
    /// </summary>
    event EventHandler<KeyMismatchEventArgs>? KeyMismatchDetected;

    /// <summary>
    /// Gets a value indicating whether the service has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the trust database and audit log.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a discovered peer, checking trust status and triggering appropriate events.
    /// </summary>
    /// <param name="peer">The discovered peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification result indicating what action should be taken.</returns>
    Task<PeerVerificationResult> VerifyPeerAsync(
        PeerInfo peer,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records the user's decision to trust a new peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="displayName">The peer's display name.</param>
    /// <param name="publicKey">The peer's Ed25519 public key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TrustNewPeerAsync(
        Guid peerId,
        string displayName,
        byte[] publicKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records the user's decision to block a peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="displayName">The peer's display name.</param>
    /// <param name="publicKey">The peer's Ed25519 public key.</param>
    /// <param name="reason">The reason for blocking (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task BlockPeerAsync(
        Guid peerId,
        string displayName,
        byte[] publicKey,
        string? reason = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records the user's approval of a key change.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="displayName">The peer's display name.</param>
    /// <param name="newPublicKey">The peer's new Ed25519 public key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ApproveKeyChangeAsync(
        Guid peerId,
        string displayName,
        byte[] newPublicKey,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Unblocks a previously blocked peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnblockPeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Updates the last seen time and increments the transfer count for a peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="success">Whether the transfer was successful.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordTransferAsync(
        Guid peerId,
        bool success,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets detailed information about a trusted peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer information, or null if not found.</returns>
    Task<TrustedPeerInfo?> GetPeerInfoAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Updates notes for a peer.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="notes">The notes to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdatePeerNotesAsync(
        Guid peerId,
        string? notes,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Removes a peer from the trust database.
    /// </summary>
    /// <param name="peerId">The peer's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemovePeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Event args for new peer detection.
/// </summary>
public sealed class NewPeerTrustEventArgs : EventArgs
{
    /// <summary>
    /// Gets the peer's unique identifier.
    /// </summary>
    public required Guid PeerId { get; init; }

    /// <summary>
    /// Gets the peer's display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the peer's public key fingerprint (formatted).
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Gets the peer's Ed25519 public key.
    /// </summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// Gets the peer's IP address as string.
    /// </summary>
    public string? IPAddress { get; init; }
}

/// <summary>
/// Event args for key mismatch detection.
/// </summary>
public sealed class KeyMismatchEventArgs : EventArgs
{
    /// <summary>
    /// Gets the peer's unique identifier.
    /// </summary>
    public required Guid PeerId { get; init; }

    /// <summary>
    /// Gets the peer's display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the previously trusted fingerprint.
    /// </summary>
    public required string OldFingerprint { get; init; }

    /// <summary>
    /// Gets the new fingerprint received.
    /// </summary>
    public required string NewFingerprint { get; init; }

    /// <summary>
    /// Gets the peer's new Ed25519 public key.
    /// </summary>
    public required byte[] NewPublicKey { get; init; }

    /// <summary>
    /// Gets the peer's IP address as string.
    /// </summary>
    public string? IPAddress { get; init; }
}

/// <summary>
/// Result of peer verification.
/// </summary>
public enum PeerVerificationResult
{
    /// <summary>
    /// Peer is trusted and verified.
    /// </summary>
    Trusted,

    /// <summary>
    /// Peer is unknown, requires user decision.
    /// </summary>
    Unknown,

    /// <summary>
    /// Peer is blocked.
    /// </summary>
    Blocked,

    /// <summary>
    /// Key mismatch detected, requires user review.
    /// </summary>
    KeyMismatch,
}
