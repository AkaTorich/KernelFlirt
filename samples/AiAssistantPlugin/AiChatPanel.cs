using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using KernelFlirt.SDK;

namespace AiAssistantPlugin;

public class AiChatPanel : Grid
{
    private readonly IDebuggerApi _api;
    private readonly AiSettings _settings;
    private readonly AiProvider _provider;

    // UI elements
    private readonly ComboBox _providerCombo;
    private readonly TextBox _modelBox;
    private readonly RichTextBox _chatBox;
    private readonly TextBox _inputBox;
    private readonly Button _sendBtn;
    private readonly Button _stopBtn;
    private readonly CheckBox _chkRegisters, _chkDisasm, _chkModules, _chkStack, _chkThreads, _chkBreakpoints;

    // Tools
    private readonly DebuggerTools _debuggerTools;

    // Chat state
    private readonly List<ChatMessage> _history = new();
    private bool _isStreaming;
    private Paragraph? _currentAssistantPara;

    public AiChatPanel(IDebuggerApi api)
    {
        _api = api;
        _settings = AiSettings.Load();
        _provider = new AiProvider();
        _debuggerTools = new DebuggerTools(api);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // toolbar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // chat
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // input
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // context toggles

        // === Toolbar ===
        var toolbar = new WrapPanel { Margin = new Thickness(4) };

        toolbar.Children.Add(new Label { Content = "Provider:", VerticalAlignment = VerticalAlignment.Center });
        _providerCombo = new ComboBox { Width = 150, Margin = new Thickness(2) };
        foreach (var p in ProviderPreset.All)
            _providerCombo.Items.Add(p.Name);
        _providerCombo.SelectedItem = _settings.ProviderName;
        _providerCombo.SelectionChanged += OnProviderChanged;
        toolbar.Children.Add(_providerCombo);

        toolbar.Children.Add(new Label { Content = "Model:", VerticalAlignment = VerticalAlignment.Center });
        _modelBox = new TextBox { Width = 140, Text = _settings.Model, Margin = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
        _modelBox.TextChanged += (_, _) => _settings.Model = _modelBox.Text;
        toolbar.Children.Add(_modelBox);

        var settingsBtn = new Button { Content = "\u2699", Width = 30, Margin = new Thickness(4, 2, 2, 2), ToolTip = "Settings" };
        settingsBtn.Click += OnSettingsClick;
        toolbar.Children.Add(settingsBtn);

        var clearBtn = new Button { Content = "\uD83D\uDDD1", Width = 30, Margin = new Thickness(2), ToolTip = "Clear chat" };
        clearBtn.Click += OnClearClick;
        toolbar.Children.Add(clearBtn);

        SetRow(toolbar, 0);
        Children.Add(toolbar);

        // === Chat area ===
        _chatBox = new RichTextBox
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };
        _chatBox.SetResourceReference(Control.BackgroundProperty, "BgPanelBrush");
        _chatBox.SetResourceReference(Control.ForegroundProperty, "FgBrush");
        _chatBox.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _chatBox.Document.Blocks.Clear();
        _chatBox.Document.SetResourceReference(FlowDocument.ForegroundProperty, "FgBrush");
        SetRow(_chatBox, 1);
        Children.Add(_chatBox);

        // === Input bar ===
        var inputPanel = new DockPanel { Margin = new Thickness(4, 4, 4, 2) };

        _stopBtn = new Button { Content = "\u25A0", Width = 30, Margin = new Thickness(2, 0, 0, 0), ToolTip = "Stop", Visibility = Visibility.Collapsed };
        _stopBtn.Click += OnStopClick;
        DockPanel.SetDock(_stopBtn, Dock.Right);
        inputPanel.Children.Add(_stopBtn);

        _sendBtn = new Button { Content = "\u25B6", Width = 30, Margin = new Thickness(2, 0, 0, 0), ToolTip = "Send" };
        _sendBtn.Click += OnSendClick;
        DockPanel.SetDock(_sendBtn, Dock.Right);
        inputPanel.Children.Add(_sendBtn);

        _inputBox = new TextBox
        {
            AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _inputBox.KeyDown += OnInputKeyDown;
        inputPanel.Children.Add(_inputBox);

        SetRow(inputPanel, 2);
        Children.Add(inputPanel);

        // === Context toggles ===
        var togglePanel = new WrapPanel { Margin = new Thickness(4, 0, 4, 4) };

        _chkRegisters = MakeToggle("Registers", _settings.IncludeRegisters, v => _settings.IncludeRegisters = v);
        _chkDisasm = MakeToggle("Disasm", _settings.IncludeDisasm, v => _settings.IncludeDisasm = v);
        _chkStack = MakeToggle("Stack", _settings.IncludeStack, v => _settings.IncludeStack = v);
        _chkModules = MakeToggle("Modules", _settings.IncludeModules, v => _settings.IncludeModules = v);
        _chkThreads = MakeToggle("Threads", _settings.IncludeThreads, v => _settings.IncludeThreads = v);
        _chkBreakpoints = MakeToggle("Breakpoints", _settings.IncludeBreakpoints, v => _settings.IncludeBreakpoints = v);

        togglePanel.Children.Add(_chkRegisters);
        togglePanel.Children.Add(_chkDisasm);
        togglePanel.Children.Add(_chkStack);
        togglePanel.Children.Add(_chkModules);
        togglePanel.Children.Add(_chkThreads);
        togglePanel.Children.Add(_chkBreakpoints);

        SetRow(togglePanel, 3);
        Children.Add(togglePanel);

        AppendSystem("AI Assistant ready. Configure provider and start chatting.");
    }

    private CheckBox MakeToggle(string label, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(6, 2, 6, 2) };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var name = _providerCombo.SelectedItem as string;
        if (name == null) return;

        var preset = ProviderPreset.All.FirstOrDefault(p => p.Name == name);
        if (preset != null)
        {
            _settings.ProviderName = preset.Name;
            _settings.Endpoint = preset.Endpoint;
            _settings.IsAnthropic = preset.IsAnthropic;
            if (!string.IsNullOrEmpty(preset.DefaultModel))
            {
                _settings.Model = preset.DefaultModel;
                _modelBox.Text = preset.DefaultModel;
            }
            // Clamp max tokens to provider limit
            if (_settings.MaxTokens > preset.MaxTokensLimit)
                _settings.MaxTokens = preset.MaxTokensLimit;
        }
        _settings.Save();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AiSettingsDialog(_settings);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true)
        {
            _settings.Save();
            // Sync UI
            _providerCombo.SelectedItem = _settings.ProviderName;
            _modelBox.Text = _settings.Model;
            _chkRegisters.IsChecked = _settings.IncludeRegisters;
            _chkDisasm.IsChecked = _settings.IncludeDisasm;
            _chkStack.IsChecked = _settings.IncludeStack;
            _chkModules.IsChecked = _settings.IncludeModules;
            _chkThreads.IsChecked = _settings.IncludeThreads;
            _chkBreakpoints.IsChecked = _settings.IncludeBreakpoints;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _chatBox.Document.Blocks.Clear();
        _currentAssistantPara = null;
        AppendSystem("Chat cleared.");
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _provider.Cancel();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isStreaming)
        {
            e.Handled = true;
            SendMessage();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SendMessage();

    private async void SendMessage()
    {
        var text = _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _isStreaming) return;

        if (string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            AppendSystem("Error: No API endpoint configured. Click \u2699 to set up.");
            return;
        }

        _inputBox.Text = "";
        SetStreaming(true);

        // Collect debug context
        string context = "";
        try
        {
            context = DebugContextCollector.Collect(_api, _settings);
        }
        catch (Exception ex)
        {
            context = $"[Error collecting context: {ex.Message}]";
        }

        // Show user message
        AppendUser(text);

        // Build messages for API
        var messages = new List<ChatMessage>();

        // System prompt with context
        var systemContent = _settings.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(context))
            systemContent += "\n\n--- Current Debug Context ---\n" + context;
        messages.Add(new ChatMessage { Role = "system", Content = systemContent });

        // History
        messages.AddRange(_history);

        // Current user message
        messages.Add(new ChatMessage { Role = "user", Content = text });
        _history.Add(new ChatMessage { Role = "user", Content = text });

        // Get tools
        var tools = _debuggerTools != null ? DebuggerTools.GetToolDefinitions() : null;

        // Tool use loop — AI can call tools multiple times before giving final text answer
        const int maxToolRounds = 10;
        for (int round = 0; round < maxToolRounds; round++)
        {
            _currentAssistantPara = AppendAssistantStart();
            var fullResponse = new System.Text.StringBuilder();
            string? error = null;

            var toolCalls = await _provider.StreamChatAsync(
                _settings,
                messages,
                tools,
                onToken: token =>
                {
                    fullResponse.Append(token);
                    Dispatcher.Invoke(() =>
                    {
                        _currentAssistantPara?.Inlines.Add(new Run(token));
                        _chatBox.ScrollToEnd();
                    });
                },
                onError: err => error = err);

            if (error != null)
            {
                Dispatcher.Invoke(() =>
                {
                    _currentAssistantPara?.Inlines.Add(new Run($"\n[Error: {error}]") { FontWeight = FontWeights.Bold });
                    _chatBox.ScrollToEnd();
                });
                break;
            }

            // Save assistant response text to history
            if (fullResponse.Length > 0)
            {
                _history.Add(new ChatMessage { Role = "assistant", Content = fullResponse.ToString() });
                messages.Add(new ChatMessage { Role = "assistant", Content = fullResponse.ToString() });
            }

            // If no tool calls — done
            if (toolCalls == null || toolCalls.Count == 0 || _debuggerTools == null)
                break;

            // Execute tool calls and add results
            var assistantMsg = new ChatMessage
            {
                Role = "assistant",
                Content = fullResponse.ToString(),
                ToolCalls = toolCalls
            };

            // Replace the text-only assistant message with one that includes tool calls
            if (fullResponse.Length > 0)
            {
                _history.RemoveAt(_history.Count - 1);
                messages.RemoveAt(messages.Count - 1);
            }
            _history.Add(assistantMsg);
            messages.Add(assistantMsg);

            foreach (var tc in toolCalls)
            {
                // Show tool call in chat
                Dispatcher.Invoke(() =>
                {
                    AppendToolCall(tc.Name, tc.Arguments);
                });

                // Execute tool on background thread to avoid UI deadlock
                // (Continue/StepOver etc. dispatch to UI thread via InvokeAsync,
                //  wait_for_break polls IsBreakState — both need UI thread free)
                var result = await Task.Run(() => _debuggerTools.Execute(tc.Name, tc.Arguments));

                // Show result in chat
                Dispatcher.Invoke(() =>
                {
                    AppendToolResult(tc.Name, result);
                });

                // Add tool result to messages (truncate for history to save context)
                var historyResult = result.Length > 1500 ? result[..1500] + "\n... (truncated)" : result;
                var toolMsg = new ChatMessage
                {
                    Role = "tool",
                    Content = historyResult,
                    ToolCallId = tc.Id
                };
                _history.Add(toolMsg);
                messages.Add(new ChatMessage { Role = "tool", Content = result, ToolCallId = tc.Id });
            }

            // Trim history if too large (keep last N messages + system prompt is re-added each time)
            TrimHistory();

            // Continue loop — send tool results back to AI for next response
        }

        _currentAssistantPara = null;
        SetStreaming(false);
        Dispatcher.Invoke(() => _chatBox.ScrollToEnd());
    }

    /// <summary>
    /// Trim history to stay within reasonable token limits.
    /// Keeps the most recent messages, removes oldest tool results first.
    /// </summary>
    private void TrimHistory()
    {
        // Estimate total characters in history
        const int maxHistoryChars = 30000; // ~7500 tokens

        int totalChars = _history.Sum(m => m.Content.Length);
        if (totalChars <= maxHistoryChars) return;

        // Remove oldest messages (skip removing the very last few which are current context)
        while (_history.Count > 4 && totalChars > maxHistoryChars)
        {
            totalChars -= _history[0].Content.Length;
            _history.RemoveAt(0);
        }
    }

    private void SetStreaming(bool streaming)
    {
        _isStreaming = streaming;
        _sendBtn.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
        _stopBtn.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        _inputBox.IsEnabled = !streaming;
    }

    // === Chat rendering helpers ===

    private void AppendSystem(string text)
    {
        var para = new Paragraph(new Run(text) { FontStyle = FontStyles.Italic })
        {
            Margin = new Thickness(0, 4, 0, 4)
        };
        _chatBox.Document.Blocks.Add(para);
        _chatBox.ScrollToEnd();
    }

    private void AppendUser(string text)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 2)
        };
        para.Inlines.Add(new Run("You: ") { FontWeight = FontWeights.Bold });
        para.Inlines.Add(new Run(text));
        _chatBox.Document.Blocks.Add(para);
        _chatBox.ScrollToEnd();
    }

    private Paragraph AppendAssistantStart()
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 6)
        };
        para.Inlines.Add(new Run("AI: ") { FontWeight = FontWeights.Bold });
        _chatBox.Document.Blocks.Add(para);
        _chatBox.ScrollToEnd();
        return para;
    }

    private void AppendToolCall(string name, string args)
    {
        // Minimal indicator — just tool name, no details
        var para = new Paragraph
        {
            Margin = new Thickness(10, 1, 0, 1)
        };
        para.Inlines.Add(new Run($"  \u25B8 {name}") { FontStyle = FontStyles.Italic });
        _chatBox.Document.Blocks.Add(para);
        _chatBox.ScrollToEnd();
    }

    private void AppendToolResult(string name, string result)
    {
        // Don't show raw tool results — the AI will summarize what matters
    }

    public void Shutdown()
    {
        _settings.Save();
        _provider.Dispose();
    }
}
