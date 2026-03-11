using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services;

namespace KernelFlirt.UI.Views;

public partial class ProcessPickerDialog : Window
{
    private ProcessInfo[] _allProcesses = [];
    private readonly DriverComm? _debugger;

    public uint SelectedPid { get; private set; }

    public ProcessPickerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadProcesses();
        FilterBox.Focus();
    }

    public ProcessPickerDialog(DriverComm debugger) : this()
    {
        _debugger = debugger;
    }

    private void LoadProcesses()
    {
        try
        {
            if (_debugger != null)
            {
                _allProcesses = _debugger.EnumProcesses()
                    .OrderBy(p => p.Name)
                    .ToArray();
            }
            else
            {
                _allProcesses = Process.GetProcesses()
                    .Select(p =>
                    {
                        try
                        {
                            return new ProcessInfo
                            {
                                ProcessId = (uint)p.Id,
                                SessionId = (uint)p.SessionId,
                                Name = p.ProcessName
                            };
                        }
                        finally { p.Dispose(); }
                    })
                    .OrderBy(p => p.Name)
                    .ToArray();
            }
        }
        catch
        {
            _allProcesses = [];
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(filter))
        {
            ProcessGrid.ItemsSource = _allProcesses;
        }
        else
        {
            ProcessGrid.ItemsSource = _allProcesses
                .Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || p.ProcessId.ToString().Contains(filter))
                .ToArray();
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        SelectAndClose();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnProcessDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectAndClose();
    }

    private void SelectAndClose()
    {
        if (ProcessGrid.SelectedItem is ProcessInfo proc)
        {
            SelectedPid = proc.ProcessId;
            DialogResult = true;
            Close();
        }
    }
}
