using System.Net;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Provides configuration for peer discovery.
/// </summary>
public sealed class PeerDiscoveryOptions
{
    /// <summary>
    /// Gets or sets the UDP broadcast port.
    /// </summary>
    public int BroadcastPort { get; set; } = 37020;

    /// <summary>
    /// Gets or sets the UDP broadcast address.
    /// </summary>
    public IPAddress BroadcastAddress { get; set; } = IPAddress.Broadcast;

    /// <summary>
    /// Gets or sets the broadcast interval.
    /// </summary>
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the peer timeout duration.
    /// </summary>
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the cleanup interval for removing stale peers.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(5);
}
