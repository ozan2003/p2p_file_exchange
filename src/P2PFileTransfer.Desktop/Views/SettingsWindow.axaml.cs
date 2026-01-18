using System;
using Avalonia;
using Avalonia.Controls;
using P2PFileTransfer.Desktop;
using P2PFileTransfer.Desktop.ViewModels;

namespace P2PFileTransfer.Desktop.Views;

/// <summary>
/// Window for editing application settings.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsWindow"/> class.
    /// </summary>
    public SettingsWindow()
    {
        this.InitializeComponent();
        SettingsViewModel viewModel = ResolveViewModel();
        this.DataContext = viewModel;
        viewModel.RequestClose += this.OnRequestClose;
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Resolves the settings view model from the application services.
    /// </summary>
    private static SettingsViewModel ResolveViewModel()
    {
        if (Application.Current is not App app)
        {
            throw new InvalidOperationException(
                "Application is not initialized."
            );
        }

        return app.GetRequiredService<SettingsViewModel>();
    }

    /// <summary>
    /// Handles close requests from the view model.
    /// </summary>
    private void OnRequestClose(object? sender, EventArgs e)
    {
        this.Close();
    }

    /// <summary>
    /// Unsubscribes event handlers when the window is closed.
    /// </summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (this.DataContext is SettingsViewModel viewModel)
        {
            viewModel.RequestClose -= this.OnRequestClose;
        }
    }
}
