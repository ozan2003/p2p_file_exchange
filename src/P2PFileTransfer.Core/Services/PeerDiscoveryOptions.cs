using System;
using System.Net;

namespace P2PFileTransfer.Core.Services;

/// <summary>
/// Provides configuration for peer discovery.
/// </summary>
public sealed class PeerDiscoveryOptions
{
    /// <summary>
    /// The default broadcast port.
    /// </summary>
    private const int DefaultBroadcastPort = 37020;

    /// <summary>
    /// The UDP broadcast port.
    /// </summary>
    public int BroadcastPort { get; set; } = DefaultBroadcastPort;

    /// <summary>
    /// The UDP broadcast address.
    /// </summary>
    public IPAddress BroadcastAddress { get; set; } = IPAddress.Broadcast;

    /// <summary>
    /// The broadcast interval.
    /// </summary>
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The peer timeout duration.
    /// </summary>
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The cleanup interval for removing stale peers.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(5);
}
