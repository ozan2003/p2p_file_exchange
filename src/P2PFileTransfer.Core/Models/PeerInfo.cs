namespace P2PFileTransfer.Core.Models;

/// <summary>
/// Represents a peer discovered on the network.
/// </summary>
public sealed class PeerInfo
{
    /// <summary>
    /// Gets or sets the unique peer identifier.
    /// </summary>
    public Guid PeerId { get; set; }

    /// <summary>
    /// Gets or sets the display name for the peer.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IPv4 address of the peer.
    /// </summary>
    public string IPAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TCP port used for file transfers.
    /// </summary>
    public int TcpPort { get; set; }

    /// <summary>
    /// Gets or sets the last time the peer was seen.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }
}
