using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services;

namespace KernelFlirt.UI.Views;

public partial class RemoteFileBrowserDialog : Window
{
    private readonly DriverComm _driver;
    private string _currentPath = "";
    private RemoteFileEntry[] _currentFiles = [];

    public string SelectedExePath { get; private set; } = "";

    public RemoteFileBrowserDialog(DriverComm driver)
    {
        InitializeComponent();
        _driver = driver;
        Loaded += (_, _) => LoadDrives();
    }

    private void LoadDrives()
    {
        try
        {
            var drives = _driver.ListRemoteDrives();
            DriveCombo.ItemsSource = drives;
            if (drives.Count > 0)
            {
                // Prefer C: drive
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

    private void NavigateTo(string path)
    {
        try
        {
            var entries = _driver.ListRemoteDirectory(path);

            // Sort: directories first (alphabetical), then files (alphabetical)
            _currentFiles = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _currentPath = path;
            PathBox.Text = _currentPath;
            FileGrid.ItemsSource = _currentFiles;
            OpenBtn.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to list directory: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveCombo.SelectedItem is RemoteDriveInfo drive)
        {
            NavigateTo(drive.Path);
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        // Go to parent directory
        string parent = System.IO.Path.GetDirectoryName(_currentPath.TrimEnd('\\')) ?? "";
        if (string.IsNullOrEmpty(parent))
        {
            // Already at root, stay here
            return;
        }
        NavigateTo(parent + "\\");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentPath))
            NavigateTo(_currentPath);
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is RemoteFileEntry entry)
        {
            if (entry.IsDirectory)
            {
                string newPath = _currentPath.TrimEnd('\\') + "\\" + entry.Name + "\\";
                NavigateTo(newPath);
            }
            else if (entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                SelectAndClose(entry);
            }
        }
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileGrid.SelectedItem is RemoteFileEntry entry)
        {
            OpenBtn.IsEnabled = !entry.IsDirectory &&
                entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            OpenBtn.IsEnabled = false;
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is RemoteFileEntry entry && !entry.IsDirectory)
        {
            SelectAndClose(entry);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SelectAndClose(RemoteFileEntry entry)
    {
        SelectedExePath = _currentPath.TrimEnd('\\') + "\\" + entry.Name;
        DialogResult = true;
        Close();
    }
}
