using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography.X509Certificates;
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
public sealed class MainViewModel : ReactiveObject, IDisposable
{
    private const int MaxDisplayNameLength = 64;
    private const string DefaultCertificatePassword = "p2p-file-transfer";
    private static readonly TimeSpan s_transferRemovalDelay =
        TimeSpan.FromSeconds(5);

    private readonly IPeerDiscoveryService m_peerDiscoveryService;
    private readonly IFileTransferService m_fileTransferService;
    private readonly IFileDialogService m_fileDialogService;
    private readonly CertificateManager m_certificateManager;
    private readonly Dictionary<Guid, PeerItemViewModel> m_peerLookup = [];
    private readonly Dictionary<Guid, TransferItemViewModel> m_transferLookup =
    [];

    private X509Certificate2? m_localCertificate;
    private string m_localFingerprint = string.Empty;

    private string m_displayName;
    private string m_statusMessage = "Ready.";
    private bool m_isDiscovering;
    private bool m_isBusy;
    private PeerItemViewModel? m_selectedPeer;
    private bool m_isDisposed;

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
        this.m_certificateManager = new CertificateManager();

        this.m_displayName = GetDefaultDisplayName();

        this.Peers = [];
        this.Transfers = [];

        IObservable<bool> canStartDiscovery = this.WhenAnyValue(
                vm => vm.IsDiscovering,
                vm => vm.IsBusy
            )
            .Select(tuple => !tuple.Item1 && !tuple.Item2);

        IObservable<bool> canStopDiscovery = this.WhenAnyValue(
                vm => vm.IsDiscovering,
                vm => vm.IsBusy
            )
            .Select(tuple => tuple.Item1 && !tuple.Item2);

        IObservable<bool> canSendFile = this.WhenAnyValue(
                vm => vm.SelectedPeer,
                vm => vm.IsBusy
            )
            .Select(tuple => tuple.Item1 != null && !tuple.Item2);

        this.StartDiscoveryCommand = ReactiveCommand.CreateFromTask(
            this.StartDiscoveryAsync,
            canStartDiscovery
        );

        this.StopDiscoveryCommand = ReactiveCommand.CreateFromTask(
            this.StopDiscoveryAsync,
            canStopDiscovery
        );

        this.SendFileCommand = ReactiveCommand.CreateFromTask(
            this.SendFileAsync,
            canSendFile
        );

        this.ClearCompletedTransfersCommand = ReactiveCommand.Create(
            this.ClearCompletedTransfers
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
            string sanitized = SanitizeDisplayName(value);
            if (this.m_displayName == sanitized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref this.m_displayName, sanitized);
            this.m_peerDiscoveryService.UpdateDisplayName(sanitized);
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
    /// A value indicating whether an operation is in progress.
    /// </summary>
    public bool IsBusy
    {
        get => this.m_isBusy;
        private set => this.RaiseAndSetIfChanged(ref this.m_isBusy, value);
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
    /// The command to clear completed transfers.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ClearCompletedTransfersCommand { get; }

    /// <summary>
    /// Initializes the background services.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Load or create the local TLS certificate.
            this.m_localCertificate =
                this.m_certificateManager.GetOrCreateDefaultCertificate(
                    DefaultCertificatePassword
                );
            this.m_localFingerprint =
                this.m_certificateManager.GetCertificateFingerprint(
                    this.m_localCertificate
                );

            string downloadDirectory =
                FilePathUtilities.GetDefaultDownloadDirectory();
            await this
                .m_fileTransferService.StartListenerAsync(
                    0, // dynamic port
                    downloadDirectory,
                    this.m_localCertificate,
                    this.m_peerDiscoveryService.GetPeerFingerprintByIPAddress,
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.m_isDisposed)
        {
            return;
        }

        this.m_isDisposed = true;

        this.m_peerDiscoveryService.PeerUpdated -= this.OnPeerUpdated;
        this.m_peerDiscoveryService.PeerRemoved -= this.OnPeerRemoved;
        this.m_peerDiscoveryService.StatusChanged -= this.OnStatusChanged;

        this.m_fileTransferService.TransferStarted -= this.OnTransferStarted;
        this.m_fileTransferService.TransferProgressChanged -=
            this.OnTransferProgressChanged;
        this.m_fileTransferService.TransferCompleted -=
            this.OnTransferCompleted;
        this.m_fileTransferService.TransferFailed -= this.OnTransferFailed;

        this.StartDiscoveryCommand.Dispose();
        this.StopDiscoveryCommand.Dispose();
        this.SendFileCommand.Dispose();
        this.ClearCompletedTransfersCommand.Dispose();

        this.m_localCertificate?.Dispose();
    }

    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return GetDefaultDisplayName();
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxDisplayNameLength)
        {
            trimmed = trimmed[..MaxDisplayNameLength];
        }

        return trimmed;
    }

    private async Task StartDiscoveryAsync()
    {
        if (this.m_fileTransferService.ListenerPort <= 0)
        {
            this.SetStatusMessage("Listener not ready. Restart the app.");
            return;
        }

        this.SetBusy(true);
        try
        {
            await this
                .m_peerDiscoveryService.StartAsync(
                    this.m_fileTransferService.ListenerPort,
                    this.DisplayName,
                    this.m_localFingerprint,
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            this.SetDiscoveryState(
                true,
                $"Peer discovery on UDP {this.m_peerDiscoveryService.BroadcastPort} started."
            );
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private async Task StopDiscoveryAsync()
    {
        this.SetBusy(true);
        try
        {
            await this.m_peerDiscoveryService.StopAsync().ConfigureAwait(false);
            this.SetDiscoveryState(false, "Discovery stopped.");
        }
        finally
        {
            this.SetBusy(false);
        }
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

    private void ClearCompletedTransfers()
    {
        List<TransferItemViewModel> toRemove = [];
        foreach (TransferItemViewModel transfer in this.Transfers)
        {
            if (transfer.IsFinished)
            {
                toRemove.Add(transfer);
            }
        }

        foreach (TransferItemViewModel transfer in toRemove)
        {
            this.m_transferLookup.Remove(transfer.TransferId);
            this.Transfers.Remove(transfer);
        }
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

    /// <summary>
    /// Sends a file to the selected peer via drag &amp; drop.
    /// </summary>
    /// <param name="filePath">The path to the file to send.</param>
    public async Task SendFileByPathAsync(string filePath)
    {
        if (this.SelectedPeer == null)
        {
            this.SetStatusMessage("Select a peer first to send files.");
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            this.SetStatusMessage("Invalid file path.");
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
                args.Metadata.FileSize,
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
                this.ScheduleTransferRemoval(args.TransferId);
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
                this.ScheduleTransferRemoval(args.TransferId);
            }

            this.StatusMessage = args.ErrorMessage;
        });
    }

    private void ScheduleTransferRemoval(Guid transferId)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(s_transferRemovalDelay).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (
                    this.m_transferLookup.TryGetValue(
                        transferId,
                        out TransferItemViewModel? transfer
                    )
                )
                {
                    this.m_transferLookup.Remove(transferId);
                    this.Transfers.Remove(transfer);
                }
            });
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

    private void SetBusy(bool isBusy)
    {
        Dispatcher.UIThread.Post(() => this.IsBusy = isBusy);
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
