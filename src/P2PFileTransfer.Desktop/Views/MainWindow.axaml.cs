using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2PFileTransfer.Desktop.ViewModels;

namespace P2PFileTransfer.Desktop.Views;

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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only accept file drops
#pragma warning disable CS0618 // Type or member is obsolete
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
#pragma warning restore CS0618
    }

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

#pragma warning disable CS0618 // Type or member is obsolete
        IEnumerable<IStorageItem>? files = e.Data.GetFiles();
#pragma warning restore CS0618

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
}
