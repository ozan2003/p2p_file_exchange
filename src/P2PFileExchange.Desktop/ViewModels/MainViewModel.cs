using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using P2PFileExchange.Core.Models;
using P2PFileExchange.Core.Models.TransferEvents;
using P2PFileExchange.Core.Services.Discovery;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Services.Transfer;
using P2PFileExchange.Core.Utilities;
using P2PFileExchange.Desktop.Services;
using P2PFileExchange.Desktop.Settings;
using ReactiveUI;

namespace P2PFileExchange.Desktop.ViewModels;

/// <summary>
/// Main view model for the desktop application.
/// </summary>
public sealed class MainViewModel : ReactiveObject, IDisposable
{
    private const int MaxDisplayNameLength = 64;
    private const string DefaultCertificatePassword = "p2p-file-transfer";

    private readonly IPeerDiscoveryService m_peerDiscoveryService;
    private readonly IFileTransferService m_fileTransferService;
    private readonly IFileDialogService m_fileDialogService;
    private readonly AppSettings m_settings;
    private readonly Dictionary<Guid, PeerItemViewModel> m_peerLookup = [];
    private readonly Dictionary<Guid, TransferItemViewModel> m_transferLookup =
    [];

    private X509Certificate2? m_localCertificate;
    private string m_localFingerprint = string.Empty;
    private ECDsa? m_signingKey;

    private string m_displayName;
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
        IFileDialogService fileDialogService,
        AppSettings settings
    )
    {
        this.m_peerDiscoveryService = peerDiscoveryService;
        this.m_fileTransferService = fileTransferService;
        this.m_fileDialogService = fileDialogService;
        this.m_settings = settings;

        this.m_displayName = string.Empty;

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

        peerDiscoveryService.PeerUpdated += this.OnPeerUpdated;
        peerDiscoveryService.PeerRemoved += this.OnPeerRemoved;
        peerDiscoveryService.StatusChanged += this.OnStatusChanged;

        fileTransferService.TransferRequestReceived +=
            this.OnTransferRequestReceived;
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
    /// The display name entered by the user (may be empty).
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
            this.RaisePropertyChanged(nameof(this.EffectiveDisplayName));
            this.m_peerDiscoveryService.UpdateDisplayName(
                this.EffectiveDisplayName
            );
        }
    }

    /// <summary>
    /// The effective display name (user's input or default if empty).
    /// </summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(this.m_displayName)
            ? DefaultDisplayName
            : this.m_displayName;

    /// <summary>
    /// The default display name used as watermark and fallback.
    /// </summary>
    public static string DefaultDisplayName => GetDefaultDisplayName();

    /// <summary>
    /// A value indicating whether discovery is running.
    /// </summary>
    public bool IsDiscovering
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// A value indicating whether an operation is in progress.
    /// </summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The selected peer.
    /// </summary>
    public PeerItemViewModel? SelectedPeer
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The status message shown in the status bar.
    /// </summary>
    public string StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Ready.";

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
            // Initialize the TLS certificate for file transfers.
            SecuritySettings securitySettings = this.m_settings.Security;
            this.m_localCertificate = CertificateManager.GetOrCreateCertificate(
                securitySettings.CertificatePath,
                DefaultCertificatePassword,
                securitySettings.CertificateValidityYears
            );
            this.m_localFingerprint =
                CertificateManager.GetCertificateFingerprint(
                    this.m_localCertificate
                );

            // Load or create the ECDSA signing key for discovery authentication.
            this.m_signingKey = SigningKeyManager.GetOrCreateKeyPair(
                securitySettings.SigningKeyPath
            );

            string downloadDirectory = this.m_settings.DownloadDirectory;
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                downloadDirectory =
                    FilePathUtilities.GetDefaultDownloadDirectory();
            }
            await ((FileTransferService)this.m_fileTransferService)
                .StartListenerAsync(
                    0, // dynamic port
                    downloadDirectory,
                    this.m_localCertificate,
                    this.m_peerDiscoveryService.GetPeerFingerprintByIPAddress,
                    this.m_peerDiscoveryService.GetPeerDisplayNameByIPAddress,
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

        this.m_fileTransferService.TransferRequestReceived -=
            this.OnTransferRequestReceived;
        this.m_fileTransferService.TransferStarted -= this.OnTransferStarted;
        this.m_fileTransferService.TransferProgressChanged -=
            this.OnTransferProgressChanged;
        this.m_fileTransferService.TransferCompleted -=
            this.OnTransferCompleted;
        this.m_fileTransferService.TransferFailed -= this.OnTransferFailed;

        this.StartDiscoveryCommand.Dispose();
        this.StopDiscoveryCommand.Dispose();
        this.SendFileCommand.Dispose();

        this.m_localCertificate?.Dispose();
        this.m_signingKey?.Dispose();
    }

    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
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

        if (this.m_signingKey == null)
        {
            this.SetStatusMessage(
                "Signing key not initialized. Restart the app."
            );
            return;
        }

        this.SetBusy(true);
        try
        {
            await this
                .m_peerDiscoveryService.StartAsync(
                    this.m_fileTransferService.ListenerPort,
                    this.EffectiveDisplayName,
                    this.m_localFingerprint,
                    this.m_signingKey,
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

    /// <summary>
    /// Removes a transfer from the list by its ID.
    /// </summary>
    /// <param name="transferId">The transfer ID to remove.</param>
    public void RemoveTransfer(Guid transferId)
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

    private void OnTransferRequestReceived(
        object? sender,
        TransferRequestEventArgs args
    )
    {
        // Show confirmation dialog on UI thread.
        Dispatcher.UIThread.Post(async () =>
        {
            string senderName =
                args.SenderDisplayName ?? args.RemoteEndpoint.ToString();
            bool accepted = await this
                .m_fileDialogService.ShowTransferConfirmationAsync(
                    senderName,
                    args.Metadata.FileName,
                    args.Metadata.FileSize
                )
                .ConfigureAwait(false);

            TransferResponse response = accepted
                ? TransferResponse.Accepted
                : TransferResponse.Rejected;

            this.m_fileTransferService.RespondToTransferRequest(
                args.RequestId,
                response
            );

            if (!accepted)
            {
                this.SetStatusMessage(
                    $"Rejected transfer from {senderName}: {args.Metadata.FileName}."
                );
            }
        });
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
