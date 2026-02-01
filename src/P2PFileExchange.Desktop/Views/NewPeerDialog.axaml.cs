using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Result from the NewPeerDialog.
/// </summary>
public enum NewPeerDialogResult
{
    /// <summary>
    /// User trusted the peer.
    /// </summary>
    Trust,

    /// <summary>
    /// User rejected but did not block the peer.
    /// </summary>
    Reject,

    /// <summary>
    /// User blocked the peer.
    /// </summary>
    Block,
}

/// <summary>
/// Dialog shown when a new peer is detected for the first time (TOFU).
/// Displays the peer's public key fingerprint for verification.
/// </summary>
public partial class NewPeerDialog : Window
{
    private TaskCompletionSource<NewPeerDialogResult>? m_resultTcs;

    /// <summary>
    /// Gets the peer ID associated with this dialog.
    /// </summary>
    public Guid PeerId { get; private set; }

    /// <summary>
    /// Gets the peer's display name.
    /// </summary>
    public string PeerName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the peer's public key fingerprint.
    /// </summary>
    public string Fingerprint { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the peer's Ed25519 public key.
    /// </summary>
    public byte[] PublicKey { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="NewPeerDialog"/> class.
    /// </summary>
    public NewPeerDialog()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Sets the dialog content based on the peer information.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="fingerprint">The peer's public key fingerprint (formatted).</param>
    /// <param name="publicKey">The peer's Ed25519 public key.</param>
    public void SetContent(
        Guid peerId,
        string peerName,
        string fingerprint,
        byte[] publicKey
    )
    {
        this.PeerId = peerId;
        this.PeerName = peerName;
        this.Fingerprint = fingerprint;
        this.PublicKey = publicKey;

        this.PeerNameText.Text = $"A new peer \"{peerName}\" wants to connect.";
        this.FingerprintText.Text = fingerprint;
    }

    /// <summary>
    /// Shows the dialog as a standalone window and waits for user response.
    /// </summary>
    /// <param name="owner">Optional owner window for initial positioning.</param>
    /// <returns>The user's decision regarding the peer.</returns>
    public Task<NewPeerDialogResult> ShowAndWaitAsync(Window? owner = null)
    {
        this.m_resultTcs = new TaskCompletionSource<NewPeerDialogResult>();

        if (owner != null)
        {
            this.Show(owner);
        }
        else
        {
            this.Show();
        }

        this.Activate();
        return this.m_resultTcs.Task;
    }

    private void OnTrustClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(NewPeerDialogResult.Trust);
        this.Close();
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(NewPeerDialogResult.Reject);
        this.Close();
    }

    private void OnBlockClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(NewPeerDialogResult.Block);
        this.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // If window is closed without clicking a button, treat as rejection.
        this.m_resultTcs?.TrySetResult(NewPeerDialogResult.Reject);
    }
}
