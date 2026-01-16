using System;
using P2PFileTransfer.Core.Models;
using ReactiveUI;

namespace P2PFileTransfer.Desktop.ViewModels;

/// <summary>
/// Represents a peer in the UI.
/// </summary>
public sealed class PeerItemViewModel : ReactiveObject
{
    private string m_displayName;
    private string m_ipAddress;
    private int m_tcpPort;
    private DateTimeOffset m_lastSeen;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerItemViewModel"/> class.
    /// </summary>
    /// <param name="peer">The peer information.</param>
    public PeerItemViewModel(PeerInfo peer)
    {
        this.PeerId = peer.PeerId;
        this.m_displayName = peer.DisplayName;
        this.m_ipAddress = peer.IPAddress;
        this.m_tcpPort = peer.TcpPort;
        this.m_lastSeen = peer.LastSeen;
    }

    /// <summary>
    /// The peer identifier.
    /// </summary>
    public Guid PeerId { get; }

    /// <summary>
    /// The display name.
    /// </summary>
    public string DisplayName
    {
        get => this.m_displayName;
        set => this.RaiseAndSetIfChanged(ref this.m_displayName, value);
    }

    /// <summary>
    /// The IPv4 address.
    /// </summary>
    public string IPAddress
    {
        get => this.m_ipAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref this.m_ipAddress, value);
            this.RaisePropertyChanged(nameof(this.Endpoint));
        }
    }

    /// <summary>
    /// The TCP port.
    /// </summary>
    public int TcpPort
    {
        get => this.m_tcpPort;
        set
        {
            this.RaiseAndSetIfChanged(ref this.m_tcpPort, value);
            this.RaisePropertyChanged(nameof(this.Endpoint));
        }
    }

    /// <summary>
    /// The last time the peer was seen.
    /// </summary>
    public DateTimeOffset LastSeen
    {
        get => this.m_lastSeen;
        set
        {
            this.RaiseAndSetIfChanged(ref this.m_lastSeen, value);
            this.RaisePropertyChanged(nameof(this.LastSeenText));
        }
    }

    /// <summary>
    /// The formatted endpoint.
    /// </summary>
    public string Endpoint => $"{this.IPAddress}:{this.TcpPort}";

    /// <summary>
    /// A human-readable "last seen" text.
    /// </summary>
    public string LastSeenText => FormatLastSeen(this.LastSeen);

    /// <summary>
    /// Updates the view model from the peer information.
    /// </summary>
    /// <param name="peer">The peer information.</param>
    public void UpdateFrom(PeerInfo peer)
    {
        this.DisplayName = peer.DisplayName;
        this.IPAddress = peer.IPAddress;
        this.TcpPort = peer.TcpPort;
        this.LastSeen = peer.LastSeen;
    }

    /// <summary>
    /// Converts to the peer model.
    /// </summary>
    public PeerInfo ToPeerInfo()
    {
        return new()
        {
            PeerId = this.PeerId,
            DisplayName = this.DisplayName,
            IPAddress = this.IPAddress,
            TcpPort = this.TcpPort,
            LastSeen = DateTimeOffset.UtcNow,
        };
    }

    private static string FormatLastSeen(DateTimeOffset lastSeen)
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - lastSeen;

        if (elapsed.TotalSeconds < 5)
        {
            return "just now";
        }

        if (elapsed.TotalSeconds < 60)
        {
            return $"{(int)elapsed.TotalSeconds}s ago";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        return $"{(int)elapsed.TotalHours}h ago";
    }
}
