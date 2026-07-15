using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using P2PFileExchange.Desktop.ViewModels;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Main application window.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        this.AddHandler(DragDrop.DropEvent, this.OnDrop);
        this.AddHandler(DragDrop.DragOverEvent, this.OnDragOver);
    }

    /// <summary>
    /// Handles drag-over to indicate supported drop content.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// Clears focus when clicking on empty space (not inside a TextBox).
    /// </summary>
    private void OnBackgroundPointerPressed(
        object? sender,
        PointerPressedEventArgs e
    )
    {
        if (
            e.Source is Visual visual
            && visual.FindAncestorOfType<TextBox>() != null
        )
        {
            return;
        }

        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    /// <summary>
    /// Sends files dropped onto the window.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (this.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedPeer == null)
        {
            return;
        }

        IEnumerable<IStorageItem>? files = e.DataTransfer.TryGetFiles();

        if (files == null)
        {
            return;
        }

        foreach (IStorageItem item in files)
        {
            if (item is IStorageFile file)
            {
                string? path = file.Path.LocalPath;
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        await viewModel
                            .SendFileByPathAsync(path)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send file: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Opens the settings window.
    /// </summary>
    private void OnOpenSettingsClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        SettingsWindow settingsWindow = new();
        _ = settingsWindow.ShowDialog(this);
    }

    /// <summary>
    /// Removes a transfer entry from the UI.
    /// </summary>
    private void OnRemoveTransferClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (sender is Button button && button.Tag is Guid transferId)
        {
            if (this.DataContext is MainViewModel viewModel)
            {
                viewModel.RemoveTransfer(transferId);
            }
        }
    }
}
