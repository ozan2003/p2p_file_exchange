using P2PFileTransfer.Core.Models;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Describes peer discovery operations over UDP broadcast.
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
    /// Gets the local peer identifier.
    /// </summary>
    Guid LocalPeerId { get; }

    /// <summary>
    /// Gets a value indicating whether discovery is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts discovery with the provided TCP listener port and display name.
    /// </summary>
    /// <param name="tcpPort">The TCP port used for file transfers.</param>
    /// <param name="displayName">The display name for the local peer.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StartAsync(
        int tcpPort,
        string displayName,
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
    /// Gets the currently discovered peers.
    /// </summary>
    IReadOnlyCollection<PeerInfo> GetPeers();
}
