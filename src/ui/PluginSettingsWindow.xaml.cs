using System.ComponentModel;
using System.IO;
using System.Windows;
using KernelFlirt.UI.Services;

namespace KernelFlirt.UI;

public class PluginViewModel : INotifyPropertyChanged
{
    private bool _enabled;
    public LoadedPlugin LoadedPlugin { get; }
    public string DisplayName => $"{LoadedPlugin.Plugin.Name} v{LoadedPlugin.Plugin.Version}";
    public string Description => LoadedPlugin.Plugin.Description;
    public string FileName => Path.GetFileName(LoadedPlugin.DllPath);

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
    }

    public PluginViewModel(LoadedPlugin plugin)
    {
        LoadedPlugin = plugin;
        _enabled = plugin.Enabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class PluginSettingsWindow : Window
{
    private readonly PluginManager _pluginManager;

    public PluginSettingsWindow(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        InitializeComponent();

        var items = pluginManager.Plugins
            .Select(p => new PluginViewModel(p))
            .ToList();
        PluginList.ItemsSource = items;
    }

    private void OnPluginToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not PluginViewModel vm) return;
        _pluginManager.SetPluginEnabled(vm.LoadedPlugin, cb.IsChecked == true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
