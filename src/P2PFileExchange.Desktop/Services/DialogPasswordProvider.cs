using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using P2PFileExchange.Core.Security;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Provides password input through Avalonia dialogs.
/// </summary>
public sealed class DialogPasswordProvider : IPasswordProvider
{
    private readonly IWindowProvider m_windowProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DialogPasswordProvider"/> class.
    /// </summary>
    /// <param name="windowProvider">The window provider for showing dialogs.</param>
    public DialogPasswordProvider(IWindowProvider windowProvider)
    {
        this.m_windowProvider = windowProvider;
    }

    /// <inheritdoc/>
    public async Task<string?> GetPasswordAsync(
        int attemptsRemaining,
        CancellationToken cancellationToken = default
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Views.PasswordDialog(
                isNewPassword: false,
                attemptsRemaining: attemptsRemaining
            );

            return await dialog.ShowDialog<string?>(
                this.m_windowProvider.MainWindow
                    ?? throw new InvalidOperationException(
                        "Main window is not available."
                    )
            );
        });
    }

    /// <inheritdoc/>
    public async Task<string?> CreatePasswordAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new Views.PasswordDialog(
                isNewPassword: true,
                attemptsRemaining: null
            );

            return await dialog.ShowDialog<string?>(
                this.m_windowProvider.MainWindow
                    ?? throw new InvalidOperationException(
                        "Main window is not available."
                    )
            );
        });
    }

    /// <inheritdoc/>
    public Task NotifyInvalidPasswordAsync(
        int attemptsRemaining,
        CancellationToken cancellationToken = default
    )
    {
        // The dialog itself will show the retry message
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task NotifyPasswordAttemptsExhaustedAsync(
        CancellationToken cancellationToken = default
    )
    {
        // The caller will handle showing the final error
        return Task.CompletedTask;
    }
}
