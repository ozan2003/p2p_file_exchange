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
    public const int DefaultBroadcastPort = 37020;

    /// <summary>
    /// The default broadcast interval.
    /// </summary>
    public static readonly TimeSpan DefaultBroadcastInterval =
        TimeSpan.FromSeconds(5);

    /// <summary>
    /// The default peer timeout.
    /// </summary>
    public static readonly TimeSpan DefaultPeerTimeout = TimeSpan.FromSeconds(
        15
    );

    /// <summary>
    /// The default cleanup interval.
    /// </summary>
    public static readonly TimeSpan DefaultCleanupInterval =
        TimeSpan.FromSeconds(5);

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
    public TimeSpan BroadcastInterval { get; set; } = DefaultBroadcastInterval;

    /// <summary>
    /// The peer timeout duration.
    /// </summary>
    public TimeSpan PeerTimeout { get; set; } = DefaultPeerTimeout;

    /// <summary>
    /// The cleanup interval for removing stale peers.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = DefaultCleanupInterval;
}
