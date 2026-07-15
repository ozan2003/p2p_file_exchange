using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Security;

namespace P2PFileExchange.Core.Discovery;

/// <summary>
/// Describes peer discovery operations over UDP broadcast on the same LAN.
///
/// <list type="bullet">
/// <item>Manages the discovery lifecycle (start/stop) and exposes running state.</item>
/// <item>Emits peer update/removal events and status messages for UI consumption.</item>
/// <item>Provides access to the local peer ID, broadcast port, and discovered peers.</item>
/// <item>All peers are identified by their Ed25519 public key.</item>
/// </list>
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
    ushort BroadcastPort { get; }

    /// <summary>
    /// Starts discovery with the provided TCP listener port, display name, and identity key manager.
    /// </summary>
    /// <param name="tcpPort">The TCP port used for file transfers.</param>
    /// <param name="displayName">The display name for the local peer.</param>
    /// <param name="identityKeyManager">The Ed25519 identity key manager for signing discovery broadcasts.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StartAsync(
        ushort tcpPort,
        ReadOnlyMemory<char> displayName,
        IdentityKeyManager identityKeyManager,
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
    void UpdateDisplayName(ReadOnlySpan<char> displayName);

    /// <summary>
    /// The currently discovered peers.
    /// </summary>
    IReadOnlyCollection<PeerInfo> GetPeers();

    /// <summary>
    /// Looks up a peer by their IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address of the peer.</param>
    /// <returns>The peer info, or null if not found.</returns>
    PeerInfo? GetPeerByIPAddress(IPAddress ipAddress);

    /// <summary>
    /// Looks up the display name for a peer by IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address of the peer.</param>
    /// <returns>The display name, or null if not found.</returns>
    string? GetPeerDisplayNameByIPAddress(IPAddress ipAddress);
}
