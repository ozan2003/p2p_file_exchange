using System;
using System.Net;
using P2PFileExchange.Core.Models;
using ReactiveUI;

namespace P2PFileExchange.Desktop.ViewModels;

/// <summary>
/// Represents a peer in the UI.
/// </summary>
public sealed class PeerItemViewModel : ReactiveObject
{
    /// <summary>Threshold in seconds for "just now" display.</summary>
    private const int JustNowThresholdSeconds = 5;

    /// <summary>Threshold in seconds before switching to minutes display.</summary>
    private const int SecondsThresholdSeconds = 60;

    /// <summary>Threshold in minutes before switching to hours display.</summary>
    private const int MinutesThresholdMinutes = 60;

    private string m_displayName;
    private IPAddress m_ipAddress;
    private ushort m_tcpPort;
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
    public IPAddress IPAddress
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
    public ushort TcpPort
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
    /// The endpoint.
    /// </summary>
    public IPEndPoint Endpoint => new(this.IPAddress, this.TcpPort);

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
            IPAddress = this.m_ipAddress,
            TcpPort = this.TcpPort,
            LastSeen = DateTimeOffset.UtcNow,
        };
    }

    private static string FormatLastSeen(DateTimeOffset lastSeen)
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - lastSeen;

        if (elapsed.TotalSeconds < JustNowThresholdSeconds)
        {
            return "just now";
        }

        if (elapsed.TotalSeconds < SecondsThresholdSeconds)
        {
            return $"{(int)elapsed.TotalSeconds}s ago";
        }

        if (elapsed.TotalMinutes < MinutesThresholdMinutes)
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        return $"{(int)elapsed.TotalHours}h ago";
    }
}
