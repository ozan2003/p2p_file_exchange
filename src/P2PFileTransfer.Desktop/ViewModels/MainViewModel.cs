using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using P2PFileTransfer.Core.Models;
using P2PFileTransfer.Core.Services;
using P2PFileTransfer.Core.Utilities;
using P2PFileTransfer.Desktop.Services;
using ReactiveUI;

namespace P2PFileTransfer.Desktop.ViewModels;

/// <summary>
/// Main view model for the desktop application.
/// </summary>
public sealed class MainViewModel : ReactiveObject
{
    private readonly IPeerDiscoveryService m_peerDiscoveryService;
    private readonly IFileTransferService m_fileTransferService;
    private readonly IFileDialogService m_fileDialogService;
    private readonly Dictionary<Guid, PeerItemViewModel> m_peerLookup = [];
    private readonly Dictionary<Guid, TransferItemViewModel> m_transferLookup =
    [];

    private string m_displayName;
    private string m_statusMessage = "Ready.";
    private bool m_isDiscovering;
    private PeerItemViewModel? m_selectedPeer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class.
    /// </summary>
    /// <param name="peerDiscoveryService">The peer discovery service.</param>
    /// <param name="fileTransferService">The file transfer service.</param>
    /// <param name="fileDialogService">The file dialog service.</param>
    public MainViewModel(
        IPeerDiscoveryService peerDiscoveryService,
        IFileTransferService fileTransferService,
        IFileDialogService fileDialogService
    )
    {
        this.m_peerDiscoveryService = peerDiscoveryService;
        this.m_fileTransferService = fileTransferService;
        this.m_fileDialogService = fileDialogService;

        this.m_displayName = GetDefaultDisplayName();

        this.Peers = [];
        this.Transfers = [];

        this.StartDiscoveryCommand = ReactiveCommand.CreateFromTask(
            this.StartDiscoveryAsync,
            this.WhenAnyValue(vm => vm.IsDiscovering)
                .Select(isRunning => !isRunning)
        );

        this.StopDiscoveryCommand = ReactiveCommand.CreateFromTask(
            this.StopDiscoveryAsync,
            this.WhenAnyValue(vm => vm.IsDiscovering)
        );

        this.SendFileCommand = ReactiveCommand.CreateFromTask(
            this.SendFileAsync,
            this.WhenAnyValue(vm => vm.SelectedPeer)
                .Select(peer => peer != null)
        );

        peerDiscoveryService.PeerUpdated += this.OnPeerUpdated;
        peerDiscoveryService.PeerRemoved += this.OnPeerRemoved;
        peerDiscoveryService.StatusChanged += this.OnStatusChanged;

        fileTransferService.TransferStarted += this.OnTransferStarted;
        fileTransferService.TransferProgressChanged +=
            this.OnTransferProgressChanged;
        fileTransferService.TransferCompleted += this.OnTransferCompleted;
        fileTransferService.TransferFailed += this.OnTransferFailed;

        this.StartDiscoveryCommand.ThrownExceptions.Subscribe(ex =>
            this.SetStatusMessage($"Discovery failed: {ex.Message}")
        );
        this.StopDiscoveryCommand.ThrownExceptions.Subscribe(ex =>
            this.SetStatusMessage($"Discovery stop failed: {ex.Message}")
        );
        this.SendFileCommand.ThrownExceptions.Subscribe(ex =>
            this.SetStatusMessage($"Send failed: {ex.Message}")
        );
    }

    /// <summary>
    /// The display name shown to other peers.
    /// </summary>
    public string DisplayName
    {
        get => this.m_displayName;
        set
        {
            this.RaiseAndSetIfChanged(ref this.m_displayName, value);
            this.m_peerDiscoveryService.UpdateDisplayName(value);
        }
    }

    /// <summary>
    /// A value indicating whether discovery is running.
    /// </summary>
    public bool IsDiscovering
    {
        get => this.m_isDiscovering;
        private set =>
            this.RaiseAndSetIfChanged(ref this.m_isDiscovering, value);
    }

    /// <summary>
    /// The selected peer.
    /// </summary>
    public PeerItemViewModel? SelectedPeer
    {
        get => this.m_selectedPeer;
        set => this.RaiseAndSetIfChanged(ref this.m_selectedPeer, value);
    }

    /// <summary>
    /// The status message shown in the status bar.
    /// </summary>
    public string StatusMessage
    {
        get => this.m_statusMessage;
        private set =>
            this.RaiseAndSetIfChanged(ref this.m_statusMessage, value);
    }

    /// <summary>
    /// The discovered peers.
    /// </summary>
    public ObservableCollection<PeerItemViewModel> Peers { get; }

    /// <summary>
    /// The active transfers.
    /// </summary>
    public ObservableCollection<TransferItemViewModel> Transfers { get; }

    /// <summary>
    /// The command to start discovery.
    /// </summary>
    public ReactiveCommand<Unit, Unit> StartDiscoveryCommand { get; }

    /// <summary>
    /// The command to stop discovery.
    /// </summary>
    public ReactiveCommand<Unit, Unit> StopDiscoveryCommand { get; }

    /// <summary>
    /// The command to send a file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SendFileCommand { get; }

    /// <summary>
    /// Initializes the background services.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            string downloadDirectory =
                FilePathUtilities.GetDefaultDownloadDirectory();
            await this
                .m_fileTransferService.StartListenerAsync(
                    0, // dynamic port
                    downloadDirectory,
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            this.SetStatusMessage(
                $"File transfer listener ready on port {this.m_fileTransferService.ListenerPort}."
            );
        }
        catch (Exception ex)
        {
            this.SetStatusMessage($"Listener failed: {ex.Message}");
        }
    }

    private async Task StartDiscoveryAsync()
    {
        if (this.m_fileTransferService.ListenerPort <= 0)
        {
            this.SetStatusMessage("Listener not ready. Restart the app.");
            return;
        }

        await this
            .m_peerDiscoveryService.StartAsync(
                this.m_fileTransferService.ListenerPort,
                this.DisplayName,
                CancellationToken.None
            )
            .ConfigureAwait(false);

        this.SetDiscoveryState(
            true,
            $"Peer discovery on UDP {this.m_peerDiscoveryService.BroadcastPort} started."
        );
    }

    private async Task StopDiscoveryAsync()
    {
        await this.m_peerDiscoveryService.StopAsync().ConfigureAwait(false);
        this.SetDiscoveryState(false, "Discovery stopped.");
    }

    private async Task SendFileAsync()
    {
        if (this.SelectedPeer == null)
        {
            this.SetStatusMessage("Select a peer to send a file.");
            return;
        }

        string? filePath = await this
            .m_fileDialogService.PickFileAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        this.SetStatusMessage(
            $"Sending {Path.GetFileName(filePath)} to {this.SelectedPeer.Endpoint}."
        );
        await this
            .m_fileTransferService.SendFileAsync(
                filePath,
                this.SelectedPeer.ToPeerInfo(),
                null,
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    private void OnPeerUpdated(object? sender, PeerInfo peer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (
                this.m_peerLookup.TryGetValue(
                    peer.PeerId,
                    out PeerItemViewModel? existing
                )
            )
            {
                existing.UpdateFrom(peer);
                return;
            }

            PeerItemViewModel viewModel = new(peer);
            this.m_peerLookup[peer.PeerId] = viewModel;
            this.Peers.Add(viewModel);
        });
    }

    private void OnPeerRemoved(object? sender, Guid peerId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (
                !this.m_peerLookup.TryGetValue(
                    peerId,
                    out PeerItemViewModel? existing
                )
            )
            {
                return;
            }

            this.m_peerLookup.Remove(peerId);
            this.Peers.Remove(existing);

            if (this.SelectedPeer?.PeerId == peerId)
            {
                this.SelectedPeer = null;
            }
        });
    }

    private void OnStatusChanged(object? sender, string message)
    {
        this.SetStatusMessage(message);
    }

    private void OnTransferStarted(
        object? sender,
        TransferStartedEventArgs args
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.m_transferLookup.ContainsKey(args.TransferId))
            {
                return;
            }

            TransferItemViewModel viewModel = new(
                args.TransferId,
                args.Mode,
                args.Metadata.FileName,
                args.RemoteEndpoint
            );

            this.m_transferLookup[args.TransferId] = viewModel;
            this.Transfers.Add(viewModel);
        });
    }

    private void OnTransferProgressChanged(
        object? sender,
        TransferProgressEventArgs args
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (
                this.m_transferLookup.TryGetValue(
                    args.TransferId,
                    out TransferItemViewModel? transfer
                )
            )
            {
                transfer.UpdateProgress(args.ProgressPercent);
            }
        });
    }

    private void OnTransferCompleted(
        object? sender,
        TransferCompletedEventArgs args
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (
                this.m_transferLookup.TryGetValue(
                    args.TransferId,
                    out TransferItemViewModel? transfer
                )
            )
            {
                transfer.MarkCompleted();
            }

            this.StatusMessage =
                $"Transfer completed: {Path.GetFileName(args.FilePath)}.";
        });
    }

    private void OnTransferFailed(object? sender, TransferFailedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (
                this.m_transferLookup.TryGetValue(
                    args.TransferId,
                    out TransferItemViewModel? transfer
                )
            )
            {
                transfer.MarkFailed(args.ErrorMessage);
            }

            this.StatusMessage = args.ErrorMessage;
        });
    }

    private static string GetDefaultDisplayName()
    {
        string userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            return userName;
        }

        string machineName = Environment.MachineName;
        return string.IsNullOrWhiteSpace(machineName) ? "Peer" : machineName;
    }

    private void SetStatusMessage(string message)
    {
        Dispatcher.UIThread.Post(() => this.StatusMessage = message);
    }

    private void SetDiscoveryState(bool isRunning, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.IsDiscovering = isRunning;
            this.StatusMessage = message;
        });
    }
}
