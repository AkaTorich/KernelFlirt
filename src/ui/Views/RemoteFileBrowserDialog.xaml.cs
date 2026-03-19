using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services;
using Microsoft.Win32;

namespace KernelFlirt.UI.Views;

public partial class RemoteFileBrowserDialog : Window
{
    private readonly DriverComm _driver;
    private string _currentPath = "";
    private RemoteFileEntry[] _currentFiles = [];

    // Navigation history
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private bool _navigating; // suppress history push during back/forward

    // Result
    public string SelectedExePath { get; private set; } = "";
    public bool IsDriverFile { get; private set; }

    public RemoteFileBrowserDialog(DriverComm driver)
    {
        InitializeComponent();
        _driver = driver;
        Loaded += (_, _) => LoadDrives();
    }

    // ── Drive loading ──

    private void LoadDrives()
    {
        try
        {
            var drives = _driver.ListRemoteDrives();
            DriveCombo.ItemsSource = drives;
            if (drives.Count > 0)
            {
                var cDrive = drives.FirstOrDefault(d => d.Letter == 'C');
                DriveCombo.SelectedItem = cDrive ?? drives[0];
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to list drives: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Navigation ──

    private void NavigateTo(string path, bool addToHistory = true)
    {
        try
        {
            var entries = _driver.ListRemoteDirectory(path);

            _currentFiles = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (addToHistory && !_navigating && !string.IsNullOrEmpty(_currentPath))
            {
                _backStack.Push(_currentPath);
                _forwardStack.Clear();
            }

            _currentPath = path;
            PathBox.Text = _currentPath;
            FileGrid.ItemsSource = _currentFiles;
            DebugBtn.IsEnabled = false;
            UpdateNavButtons();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to list directory: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _backStack.Count > 0;
        FwdBtn.IsEnabled = _forwardStack.Count > 0;
    }

    private void UpdateStatus()
    {
        int dirs = _currentFiles.Count(f => f.IsDirectory);
        int files = _currentFiles.Length - dirs;
        var selected = FileGrid.SelectedItems.Count;
        string sel = selected > 0 ? $" | Selected: {selected}" : "";
        StatusText.Text = $"{files} files, {dirs} folders{sel}";
    }

    // ── Navigation event handlers ──

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveCombo.SelectedItem is RemoteDriveInfo drive)
            NavigateTo(drive.Path);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        _forwardStack.Push(_currentPath);
        _navigating = true;
        NavigateTo(_backStack.Pop(), false);
        _navigating = false;
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(_currentPath);
        _navigating = true;
        NavigateTo(_forwardStack.Pop(), false);
        _navigating = false;
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        string parent = Path.GetDirectoryName(_currentPath.TrimEnd('\\')) ?? "";
        if (string.IsNullOrEmpty(parent)) return;
        NavigateTo(parent + "\\");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentPath))
            NavigateTo(_currentPath, false);
    }

    private void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string path = PathBox.Text.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                if (!path.EndsWith("\\")) path += "\\";
                NavigateTo(path);
            }
            e.Handled = true;
        }
    }

    // ── File list events ──

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedEntries();
        bool hasDebuggable = selected.Any(f => f.IsDebuggable);
        DebugBtn.IsEnabled = hasDebuggable;
        CtxOpenDebug.IsEnabled = selected.Count == 1 && selected[0].IsDebuggable;
        CtxDownload.IsEnabled = selected.Any(f => !f.IsDirectory);
        CtxRename.IsEnabled = selected.Count == 1;
        CtxDelete.IsEnabled = selected.Count > 0;
        CtxCopyPath.IsEnabled = selected.Count > 0;
        UpdateStatus();
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is not RemoteFileEntry entry) return;

        if (entry.IsDirectory)
        {
            NavigateTo(_currentPath.TrimEnd('\\') + "\\" + entry.Name + "\\");
        }
        else if (entry.IsDebuggable)
        {
            SelectAndClose(entry);
        }
        else
        {
            // Download non-debuggable files
            DownloadFiles([entry]);
        }
    }

    // ── Open & Debug ──

    private void OnOpenDebugClick(object sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntries().FirstOrDefault(f => f.IsDebuggable);
        if (entry != null)
            SelectAndClose(entry);
    }

    private void SelectAndClose(RemoteFileEntry entry)
    {
        SelectedExePath = _currentPath.TrimEnd('\\') + "\\" + entry.Name;
        IsDriverFile = entry.IsSys;
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ── Download ──

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedEntries().Where(f => !f.IsDirectory).ToList();
        if (files.Count == 0) return;
        DownloadFiles(files);
    }

    private async void DownloadFiles(List<RemoteFileEntry> files)
    {
        if (files.Count == 1)
        {
            var file = files[0];
            var dlg = new SaveFileDialog
            {
                FileName = file.Name,
                Filter = "All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            await DownloadSingleFile(
                _currentPath.TrimEnd('\\') + "\\" + file.Name,
                dlg.FileName, file.FileSize);
        }
        else
        {
            // Multi-file: pick a folder via FolderBrowserDialog
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = $"Select folder to save {files.Count} files"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            foreach (var file in files)
            {
                string remotePath = _currentPath.TrimEnd('\\') + "\\" + file.Name;
                string localPath = Path.Combine(dlg.SelectedPath, file.Name);
                await DownloadSingleFile(remotePath, localPath, file.FileSize);
            }
        }
        NavigateTo(_currentPath, false);
    }

    private async Task DownloadSingleFile(string remotePath, string localPath, ulong totalSize)
    {
        IsEnabled = false;
        string origTitle = Title;
        var cts = new CancellationTokenSource();
        try
        {
            string fileName = Path.GetFileName(remotePath);
            StatusText.Text = $"Downloading {fileName}...";

            bool ok = await Task.Run(() => _driver.DownloadRemoteFile(remotePath, localPath,
                (done, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (totalSize > 0)
                        {
                            double pct = (double)done / totalSize * 100;
                            Title = $"[{pct:F0}%] Downloading {fileName}";
                            StatusText.Text = $"Downloading {fileName}: {RemoteFileEntry.FormatSizeStatic((ulong)done)} / {RemoteFileEntry.FormatSizeStatic(totalSize)}";
                        }
                        else
                        {
                            StatusText.Text = $"Downloading {fileName}: {RemoteFileEntry.FormatSizeStatic((ulong)done)}";
                        }
                    });
                }, cts.Token));

            StatusText.Text = ok ? $"Downloaded: {Path.GetFileName(localPath)}" : "Download failed";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Download failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Title = origTitle;
            IsEnabled = true;
        }
    }

    // ── Upload ──

    private void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select file(s) to upload",
            Multiselect = true,
            Filter = "All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        UploadFiles(dlg.FileNames);
    }

    private async void UploadFiles(string[] localPaths)
    {
        IsEnabled = false;
        string origTitle = Title;
        try
        {
            foreach (var localPath in localPaths)
            {
                string fileName = Path.GetFileName(localPath);
                string remotePath = _currentPath.TrimEnd('\\') + "\\" + fileName;
                var fi = new FileInfo(localPath);
                long totalSize = fi.Length;

                StatusText.Text = $"Uploading {fileName}...";

                bool ok = await Task.Run(() => _driver.UploadLocalFile(localPath, remotePath,
                    (done, total) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            double pct = total > 0 ? (double)done / total * 100 : 0;
                            Title = $"[{pct:F0}%] Uploading {fileName}";
                            StatusText.Text = $"Uploading {fileName}: {RemoteFileEntry.FormatSizeStatic((ulong)done)} / {RemoteFileEntry.FormatSizeStatic((ulong)total)}";
                        });
                    }, CancellationToken.None));

                if (!ok)
                {
                    MessageBox.Show($"Upload failed: {fileName}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                }
            }
        }
        finally
        {
            Title = origTitle;
            IsEnabled = true;
            NavigateTo(_currentPath, false);
        }
    }

    // ── Drag & Drop upload ──

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            UploadFiles(files);
    }

    // ── New Folder ──

    private void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        string? name = PromptInput("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        string path = _currentPath.TrimEnd('\\') + "\\" + name;
        bool ok = _driver.CreateRemoteDirectory(path);
        if (!ok)
            MessageBox.Show("Failed to create directory", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            NavigateTo(_currentPath, false);
    }

    // ── Rename ──

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count != 1) return;

        var entry = selected[0];
        string? newName = PromptInput("Rename", "Enter new name:", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;

        string oldPath = _currentPath.TrimEnd('\\') + "\\" + entry.Name;
        string newPath = _currentPath.TrimEnd('\\') + "\\" + newName;
        bool ok = _driver.RenameRemotePath(oldPath, newPath);
        if (!ok)
            MessageBox.Show("Rename failed", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            NavigateTo(_currentPath, false);
    }

    // ── Delete ──

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return;

        string msg = selected.Count == 1
            ? $"Delete \"{selected[0].Name}\"?"
            : $"Delete {selected.Count} items?";

        if (MessageBox.Show(msg, "Confirm Delete", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int failed = 0;
        foreach (var entry in selected)
        {
            string path = _currentPath.TrimEnd('\\') + "\\" + entry.Name;
            if (!_driver.DeleteRemotePath(path))
                failed++;
        }

        if (failed > 0)
            MessageBox.Show($"{failed} item(s) could not be deleted", "Warning",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        NavigateTo(_currentPath, false);
    }

    // ── Copy Path ──

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return;

        var paths = selected.Select(f => _currentPath.TrimEnd('\\') + "\\" + f.Name);
        Clipboard.SetText(string.Join("\n", paths));
    }

    // ── Keyboard shortcuts ──

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't handle if PathBox is focused
        if (PathBox.IsFocused) return;

        switch (e.Key)
        {
            case Key.F5:
                OnRefreshClick(sender, e);
                e.Handled = true;
                break;
            case Key.F2:
                OnRenameClick(sender, e);
                e.Handled = true;
                break;
            case Key.Delete:
                OnDeleteClick(sender, e);
                e.Handled = true;
                break;
            case Key.Back:
                OnUpClick(sender, e);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
                OnBackClick(sender, e);
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
                OnForwardClick(sender, e);
                e.Handled = true;
                break;
            case Key.Enter:
                if (FileGrid.SelectedItem is RemoteFileEntry entry)
                {
                    if (entry.IsDirectory)
                        NavigateTo(_currentPath.TrimEnd('\\') + "\\" + entry.Name + "\\");
                    else if (entry.IsDebuggable)
                        SelectAndClose(entry);
                }
                e.Handled = true;
                break;
        }
    }

    // ── Helpers ──

    private List<RemoteFileEntry> GetSelectedEntries()
    {
        return FileGrid.SelectedItems.Cast<RemoteFileEntry>().ToList();
    }

    private static string? PromptInput(string title, string prompt, string defaultValue = "")
    {
        // Simple input dialog using a Window
        var win = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = defaultValue };
        tb.SelectAll();
        sp.Children.Add(tb);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okBtn = new Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", Width = 75, IsCancel = true };
        okBtn.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        sp.Children.Add(btnPanel);

        win.Content = sp;
        tb.Focus();

        return win.ShowDialog() == true ? tb.Text : null;
    }
}
