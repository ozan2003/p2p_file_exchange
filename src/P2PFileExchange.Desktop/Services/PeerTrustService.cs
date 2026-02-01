using System;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Services.Security;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Coordinates peer trust workflows using the Core's PeerTrustManager and SecurityAuditLog.
/// </summary>
public sealed class PeerTrustService : IPeerTrustService
{
    private readonly PeerTrustManager m_trustManager;
    private readonly SecurityAuditLog m_auditLog;
    private bool m_disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrustService"/> class.
    /// </summary>
    /// <param name="trustManager">The peer trust manager.</param>
    /// <param name="auditLog">The security audit log.</param>
    public PeerTrustService(
        PeerTrustManager trustManager,
        SecurityAuditLog auditLog
    )
    {
        this.m_trustManager =
            trustManager
            ?? throw new ArgumentNullException(nameof(trustManager));
        this.m_auditLog =
            auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    /// <summary>
    /// Creates a new instance with default database paths.
    /// </summary>
    public PeerTrustService()
        : this(new PeerTrustManager(), new SecurityAuditLog()) { }

    /// <inheritdoc />
    public event EventHandler<NewPeerTrustEventArgs>? NewPeerDetected;

    /// <inheritdoc />
    public event EventHandler<KeyMismatchEventArgs>? KeyMismatchDetected;

    /// <inheritdoc />
    public bool IsInitialized =>
        this.m_trustManager.IsInitialized && this.m_auditLog.IsInitialized;

    /// <inheritdoc />
    public async Task InitializeAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        await this
            .m_trustManager.InitializeDatabaseAsync(cancellationToken)
            .ConfigureAwait(false);
        await this
            .m_auditLog.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PeerVerificationResult> VerifyPeerAsync(
        PeerInfo peer,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(peer.IdentityPublicKey))
        {
            // Peer doesn't have identity key - treat as unknown
            return PeerVerificationResult.Unknown;
        }

        byte[] publicKey = Convert.FromBase64String(peer.IdentityPublicKey);

        // Check if peer exists in trust database
        TrustLevel? trustLevel = await this
            .m_trustManager.GetTrustLevelAsync(peer.PeerId, cancellationToken)
            .ConfigureAwait(false);

        if (trustLevel is null)
        {
            // New peer - unknown
            string fingerprint = IdentityKeyManager.ComputeFingerprint(
                publicKey
            );

            this.NewPeerDetected?.Invoke(
                this,
                new NewPeerTrustEventArgs
                {
                    PeerId = peer.PeerId,
                    DisplayName = peer.DisplayName,
                    Fingerprint = fingerprint,
                    PublicKey = publicKey,
                    IPAddress = peer.IPAddress?.ToString(),
                }
            );

            return PeerVerificationResult.Unknown;
        }

        if (trustLevel == TrustLevel.Blocked)
        {
            return PeerVerificationResult.Blocked;
        }

        // Peer exists - verify public key matches
        (string OldFingerprint, string NewFingerprint)? mismatch = await this
            .m_trustManager.DetectKeyMismatchAsync(
                peer.PeerId,
                publicKey,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (mismatch is not null)
        {
            // Key mismatch detected!
            await this
                .m_auditLog.LogKeyMismatchDetectedAsync(
                    peer.PeerId,
                    peer.DisplayName,
                    mismatch.Value.OldFingerprint,
                    mismatch.Value.NewFingerprint,
                    peer.IPAddress?.ToString(),
                    cancellationToken
                )
                .ConfigureAwait(false);

            this.KeyMismatchDetected?.Invoke(
                this,
                new KeyMismatchEventArgs
                {
                    PeerId = peer.PeerId,
                    DisplayName = peer.DisplayName,
                    OldFingerprint = mismatch.Value.OldFingerprint,
                    NewFingerprint = mismatch.Value.NewFingerprint,
                    NewPublicKey = publicKey,
                    IPAddress = peer.IPAddress?.ToString(),
                }
            );

            return PeerVerificationResult.KeyMismatch;
        }

        // Trusted and verified
        await this
            .m_trustManager.UpdateLastSeenAsync(peer.PeerId, cancellationToken)
            .ConfigureAwait(false);

        return PeerVerificationResult.Trusted;
    }

    /// <inheritdoc />
    public async Task TrustNewPeerAsync(
        Guid peerId,
        string displayName,
        byte[] publicKey,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        await this
            .m_trustManager.TrustPeerAsync(
                peerId,
                displayName.AsMemory(),
                publicKey,
                cancellationToken
            )
            .ConfigureAwait(false);

        string fingerprint = IdentityKeyManager.ComputeFingerprint(publicKey);

        await this
            .m_auditLog.LogNewPeerTrustedAsync(
                peerId,
                displayName,
                fingerprint,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BlockPeerAsync(
        Guid peerId,
        string displayName,
        byte[] publicKey,
        string? reason = null,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        // Check if peer exists; if not, add them first
        bool exists = await this
            .m_trustManager.PeerExistsAsync(peerId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await this
                .m_trustManager.TrustPeerAsync(
                    peerId,
                    displayName.AsMemory(),
                    publicKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await this
            .m_trustManager.SetTrustLevelAsync(
                peerId,
                TrustLevel.Blocked,
                cancellationToken
            )
            .ConfigureAwait(false);

        await this
            .m_auditLog.LogPeerBlockedAsync(
                peerId,
                displayName,
                reason,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApproveKeyChangeAsync(
        Guid peerId,
        string displayName,
        byte[] newPublicKey,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        await this
            .m_trustManager.ApproveKeyChangeAsync(
                peerId,
                newPublicKey,
                cancellationToken
            )
            .ConfigureAwait(false);

        string newFingerprint = IdentityKeyManager.ComputeFingerprint(
            newPublicKey
        );

        await this
            .m_auditLog.LogKeyChangeApprovedAsync(
                peerId,
                displayName,
                newFingerprint,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnblockPeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        TrustedPeerInfo? peer = await this
            .m_trustManager.GetPeerAsync(peerId, cancellationToken)
            .ConfigureAwait(false);

        if (peer is null)
        {
            return;
        }

        await this
            .m_trustManager.SetTrustLevelAsync(
                peerId,
                TrustLevel.Trusted,
                cancellationToken
            )
            .ConfigureAwait(false);

        await this
            .m_auditLog.LogPeerUnblockedAsync(
                peerId,
                peer.CachedDisplayName ?? "Unknown",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordTransferAsync(
        Guid peerId,
        bool success,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        await this
            .m_trustManager.IncrementTransferCountAsync(
                peerId,
                success,
                cancellationToken
            )
            .ConfigureAwait(false);

        await this
            .m_trustManager.UpdateLastSeenAsync(peerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TrustedPeerInfo?> GetPeerInfoAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        return await this
            .m_trustManager.GetPeerAsync(peerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdatePeerNotesAsync(
        Guid peerId,
        string? notes,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        await this
            .m_trustManager.UpdateNotesAsync(peerId, notes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemovePeerAsync(
        Guid peerId,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        TrustedPeerInfo? peer = await this
            .m_trustManager.GetPeerAsync(peerId, cancellationToken)
            .ConfigureAwait(false);

        if (peer is null)
        {
            return;
        }

        _ = await this
            .m_trustManager.RemovePeerAsync(peerId, cancellationToken)
            .ConfigureAwait(false);

        await this
            .m_auditLog.LogPeerRemovedAsync(
                peerId,
                peer.CachedDisplayName ?? "Unknown",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_disposed, this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.m_disposed)
        {
            return;
        }

        this.m_disposed = true;

        await this.m_trustManager.DisposeAsync().ConfigureAwait(false);
        await this.m_auditLog.DisposeAsync().ConfigureAwait(false);
    }
}
