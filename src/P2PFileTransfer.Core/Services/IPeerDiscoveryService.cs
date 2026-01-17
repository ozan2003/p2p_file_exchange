using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using P2PFileTransfer.Core.Models;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Describes peer discovery operations over UDP broadcast on the same LAN.
/// </summary>
public interface IPeerDiscoveryService : IAsyncDisposable
{
    /// <summary>
    /// Occurs when a peer is added or updated.
    /// </summary>
    event EventHandler<PeerInfo>? PeerUpdated;

    /// <summary>
    /// Occurs when a peer is removed due to timeout.
    /// </summary>
    event EventHandler<Guid>? PeerRemoved;

    /// <summary>
    /// Occurs when the discovery service emits a status message.
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// The local peer identifier.
    /// </summary>
    Guid LocalPeerId { get; }

    /// <summary>
    /// A value indicating whether discovery is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The UDP port used for discovery broadcasts.
    /// </summary>
    int BroadcastPort { get; }

    /// <summary>
    /// Starts discovery with the provided TCP listener port, display name, and certificate fingerprint for broadcasting.
    /// </summary>
    /// <param name="tcpPort">The TCP port used for file transfers.</param>
    /// <param name="displayName">The display name for the local peer.</param>
    /// <param name="certificateFingerprint">The SHA-256 fingerprint of the local TLS certificate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StartAsync(
        int tcpPort,
        string displayName,
        string certificateFingerprint,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stops discovery and clears resources.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Updates the local display name used for broadcasts.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    void UpdateDisplayName(string displayName);

    /// <summary>
    /// The currently discovered peers.
    /// </summary>
    IReadOnlyCollection<PeerInfo> GetPeers();

    /// <summary>
    /// Looks up the expected certificate fingerprint for a peer by IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address of the peer.</param>
    /// <returns>The certificate fingerprint, or null if not found.</returns>
    string? GetPeerFingerprintByIPAddress(string ipAddress);
}
