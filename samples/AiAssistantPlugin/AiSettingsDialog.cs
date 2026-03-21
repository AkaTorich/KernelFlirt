using System.Windows;
using System.Windows.Controls;

namespace AiAssistantPlugin;

public class AiSettingsDialog : Window
{
    private readonly AiSettings _settings;
    private readonly ComboBox _providerCombo;
    private readonly TextBox _endpointBox;
    private readonly PasswordBox _apiKeyBox;
    private readonly TextBox _modelBox;
    private readonly TextBox _systemPromptBox;
    private readonly Slider _maxTokensSlider;
    private readonly Slider _temperatureSlider;
    private readonly Label _maxTokensLabel;
    private readonly Label _temperatureLabel;

    public AiSettingsDialog(AiSettings settings)
    {
        _settings = settings;

        Title = "AI Assistant Settings";
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        // Inherit theme from Application resources
        if (Application.Current?.Resources != null)
        {
            Resources.MergedDictionaries.Add(Application.Current.Resources);
        }
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "FgBrush");

        var stack = new StackPanel { Margin = new Thickness(12) };

        // Provider
        stack.Children.Add(MakeLabel("Provider:"));
        _providerCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var p in ProviderPreset.All)
            _providerCombo.Items.Add(p.Name);
        _providerCombo.SelectedItem = settings.ProviderName;
        _providerCombo.SelectionChanged += OnProviderChanged;
        stack.Children.Add(_providerCombo);

        // Endpoint
        stack.Children.Add(MakeLabel("API Endpoint:"));
        _endpointBox = new TextBox { Text = settings.Endpoint, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_endpointBox);

        // API Key
        stack.Children.Add(MakeLabel("API Key (leave empty for local providers):"));
        _apiKeyBox = new PasswordBox { Password = settings.ApiKey, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_apiKeyBox);

        // Model
        stack.Children.Add(MakeLabel("Model:"));
        _modelBox = new TextBox { Text = settings.Model, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_modelBox);

        // Max tokens
        var tokensPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        tokensPanel.Children.Add(MakeLabel("Max Tokens:"));
        _maxTokensLabel = new Label { Content = settings.MaxTokens.ToString(), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(_maxTokensLabel, Dock.Right);
        tokensPanel.Children.Add(_maxTokensLabel);
        stack.Children.Add(tokensPanel);

        _maxTokensSlider = new Slider { Minimum = 1024, Maximum = 65536, Value = settings.MaxTokens, TickFrequency = 1024, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 8) };
        _maxTokensSlider.ValueChanged += (_, _) => _maxTokensLabel.Content = ((int)_maxTokensSlider.Value).ToString();
        stack.Children.Add(_maxTokensSlider);

        // Temperature
        var tempPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        tempPanel.Children.Add(MakeLabel("Temperature:"));
        _temperatureLabel = new Label { Content = settings.Temperature.ToString("F1"), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(_temperatureLabel, Dock.Right);
        tempPanel.Children.Add(_temperatureLabel);
        stack.Children.Add(tempPanel);

        _temperatureSlider = new Slider { Minimum = 0, Maximum = 1, Value = settings.Temperature, TickFrequency = 0.1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 8) };
        _temperatureSlider.ValueChanged += (_, _) => _temperatureLabel.Content = _temperatureSlider.Value.ToString("F1");
        stack.Children.Add(_temperatureSlider);

        // System prompt
        stack.Children.Add(MakeLabel("System Prompt:"));
        _systemPromptBox = new TextBox
        {
            Text = settings.SystemPrompt,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 100,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(_systemPromptBox);

        var resetPromptBtn = new Button { Content = "Reset to Default", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 12) };
        resetPromptBtn.Click += (_, _) => _systemPromptBox.Text = AiSettings.DefaultSystemPrompt;
        stack.Children.Add(resetPromptBtn);

        // OK / Cancel
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        okBtn.Click += OnOk;
        var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // Bind input controls to theme
        foreach (var ctrl in new FrameworkElement[] { _endpointBox, _modelBox, _systemPromptBox })
        {
            ctrl.SetResourceReference(Control.BackgroundProperty, "BgPanelBrush");
            ctrl.SetResourceReference(Control.ForegroundProperty, "FgBrush");
            ctrl.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        }
        _apiKeyBox.SetResourceReference(Control.BackgroundProperty, "BgPanelBrush");
        _apiKeyBox.SetResourceReference(Control.ForegroundProperty, "FgBrush");
        _apiKeyBox.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = _providerCombo.SelectedItem as string;
        var preset = ProviderPreset.All.FirstOrDefault(p => p.Name == name);
        if (preset != null)
        {
            _endpointBox.Text = preset.Endpoint;
            if (!string.IsNullOrEmpty(preset.DefaultModel))
                _modelBox.Text = preset.DefaultModel;

            // Adjust max tokens slider to provider limit
            _maxTokensSlider.Maximum = preset.MaxTokensLimit;
            if (_maxTokensSlider.Value > preset.MaxTokensLimit)
                _maxTokensSlider.Value = preset.MaxTokensLimit;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _settings.ProviderName = _providerCombo.SelectedItem as string ?? "Custom";
        _settings.Endpoint = _endpointBox.Text.Trim();
        _settings.ApiKey = _apiKeyBox.Password;
        _settings.Model = _modelBox.Text.Trim();
        _settings.MaxTokens = (int)_maxTokensSlider.Value;
        _settings.Temperature = Math.Round(_temperatureSlider.Value, 1);
        _settings.SystemPrompt = _systemPromptBox.Text;

        var preset = ProviderPreset.All.FirstOrDefault(p => p.Name == _settings.ProviderName);
        _settings.IsAnthropic = preset?.IsAnthropic ?? false;

        DialogResult = true;
    }

    private static Label MakeLabel(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 2) };
}
