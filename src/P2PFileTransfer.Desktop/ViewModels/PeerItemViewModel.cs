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
    /// The formatted endpoint.
    /// </summary>
    public string Endpoint => $"{this.IPAddress}:{this.TcpPort}";

    /// <summary>
    /// Updates the view model from the peer information.
    /// </summary>
    /// <param name="peer">The peer information.</param>
    public void UpdateFrom(PeerInfo peer)
    {
        this.DisplayName = peer.DisplayName;
        this.IPAddress = peer.IPAddress;
        this.TcpPort = peer.TcpPort;
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
}
