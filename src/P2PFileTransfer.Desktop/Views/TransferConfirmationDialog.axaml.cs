using Avalonia.Controls;
using Avalonia.Interactivity;

namespace P2PFileTransfer.Desktop.Views;

/// <summary>
/// A dialog window for confirming incoming file transfer requests.
/// </summary>
public partial class TransferConfirmationDialog : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransferConfirmationDialog"/> class.
    /// </summary>
    public TransferConfirmationDialog()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Gets or sets a value indicating whether the transfer was accepted.
    /// </summary>
    public bool IsAccepted { get; private set; }

    /// <summary>
    /// Sets the dialog content based on the transfer request.
    /// </summary>
    /// <param name="senderName">The display name of the sender.</param>
    /// <param name="fileName">The name of the file being sent.</param>
    /// <param name="fileSize">The size of the file in bytes.</param>
    public void SetContent(string senderName, string fileName, long fileSize)
    {
        this.SenderText.Text = $"{senderName} wants to send you a file:";
        this.FileInfoText.Text = $"{fileName} ({FormatFileSize(fileSize)})";
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        this.IsAccepted = true;
        this.Close();
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        this.IsAccepted = false;
        this.Close();
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.##} {suffixes[suffixIndex]}";
    }
}
