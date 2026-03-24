using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KernelFlirt.SDK;

namespace McpServerPlugin;

/// <summary>
/// WPF panel shown in the KernelFlirt "MCP Server" tab.
/// Lets the user configure the port, start/stop the server and watch live activity.
/// </summary>
public class McpSettingsPanel : Grid
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly IDebuggerApi   _api;
    private McpHttpServer?          _server;
    private int                     _port;

    // ── UI controls ───────────────────────────────────────────────────────────

    private readonly Ellipse    _statusDot;
    private readonly TextBlock  _statusLabel;
    private readonly TextBlock  _urlLabel;
    private readonly TextBox    _portBox;
    private readonly Button     _startBtn;
    private readonly Button     _stopBtn;
    private readonly TextBox    _logBox;

    // ── Settings file ─────────────────────────────────────────────────────────

    private static readonly string SettingsPath =
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(
                typeof(McpSettingsPanel).Assembly.Location)!,
            "mcp_settings.json");

    // ─────────────────────────────────────────────────────────────────────────

    public McpSettingsPanel(IDebuggerApi api)
    {
        _api  = api;
        _port = LoadPort();

        // ── Root layout: header strip + log ──────────────────────────────────
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // config row
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // mcp.json hint
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // separator
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // log

        Margin = new Thickness(8);

        // ── Row 0: status ─────────────────────────────────────────────────────
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 6)
        };

        _statusDot = new Ellipse
        {
            Width   = 12,
            Height  = 12,
            Fill    = Brushes.Gray,
            Margin  = new Thickness(0, 3, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusLabel = new TextBlock
        {
            Text               = "Stopped",
            FontWeight         = FontWeights.Bold,
            VerticalAlignment  = VerticalAlignment.Center,
            Margin             = new Thickness(0, 0, 12, 0)
        };

        _urlLabel = new TextBlock
        {
            Text              = BuildUrl(_port),
            Foreground        = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily        = new FontFamily("Consolas"),
            FontSize          = 11
        };

        headerPanel.Children.Add(_statusDot);
        headerPanel.Children.Add(_statusLabel);
        headerPanel.Children.Add(_urlLabel);
        SetRow(headerPanel, 0);
        Children.Add(headerPanel);

        // ── Row 1: port + buttons ─────────────────────────────────────────────
        var configPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 6)
        };

        configPanel.Children.Add(new TextBlock
        {
            Text              = "Port:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0)
        });

        _portBox = new TextBox
        {
            Text              = _port.ToString(),
            Width             = 60,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
            Padding           = new Thickness(3, 2, 3, 2)
        };

        _startBtn = MakeButton("▶  Start", "#2E7D32", OnStart);
        _stopBtn  = MakeButton("■  Stop",  "#B71C1C", OnStop);
        _stopBtn.IsEnabled = false;

        configPanel.Children.Add(_portBox);
        configPanel.Children.Add(_startBtn);
        configPanel.Children.Add(_stopBtn);
        SetRow(configPanel, 1);
        Children.Add(configPanel);

        // ── Row 2: .mcp.json hint ─────────────────────────────────────────────
        var hintBox = new TextBox
        {
            IsReadOnly        = true,
            Text              = $"\"kf-debugger\": {{ \"url\": \"{BuildUrl(_port)}\" }}",
            FontFamily        = new FontFamily("Consolas"),
            FontSize          = 11,
            Foreground        = Brushes.DarkCyan,
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Margin            = new Thickness(0, 0, 0, 6),
            IsTabStop         = false,
            ToolTip           = "Paste this into your .mcp.json → mcpServers section"
        };
        SetRow(hintBox, 2);
        Children.Add(hintBox);

        // ── Row 3: separator ─────────────────────────────────────────────────
        var sep = new Separator { Margin = new Thickness(0, 0, 0, 4) };
        SetRow(sep, 3);
        Children.Add(sep);

        // ── Row 4: activity log ───────────────────────────────────────────────
        var logHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var logTitle  = new TextBlock { Text = "Activity log", FontWeight = FontWeights.SemiBold };
        var clearBtn  = new Button
        {
            Content             = "Clear",
            Padding             = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        clearBtn.Click += (_, _) => _logBox!.Clear();
        DockPanel.SetDock(clearBtn, Dock.Right);
        logHeader.Children.Add(clearBtn);
        logHeader.Children.Add(logTitle);

        _logBox = new TextBox
        {
            IsReadOnly      = true,
            AcceptsReturn   = true,
            FontFamily      = new FontFamily("Consolas"),
            FontSize        = 11,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding         = new Thickness(4)
        };

        var logStack = new DockPanel();
        DockPanel.SetDock(logHeader, Dock.Top);
        logStack.Children.Add(logHeader);
        logStack.Children.Add(_logBox);
        SetRow(logStack, 4);
        Children.Add(logStack);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Start the server immediately (called from plugin Initialize).</summary>
    public void AutoStart()
    {
        StartServer(_port);
    }

    public void Shutdown()
    {
        _server?.Stop();
        _server = null;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_portBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            AppendLog("Invalid port — must be 1024..65535");
            return;
        }
        _server?.Stop();
        _server = null;
        _port = port;
        SavePort(port);
        StartServer(port);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _server?.Stop();
        _server = null;
        SetStatus(running: false);
        AppendLog("Server stopped");
    }

    // ── Server lifecycle ──────────────────────────────────────────────────────

    private void StartServer(int port)
    {
        try
        {
            _server = new McpHttpServer(_api, port);
            _server.OnActivity += msg => Dispatcher.InvokeAsync(() => AppendLog(msg));
            _server.Start();
            SetStatus(running: true);
            UpdateUrl(port);
            AppendLog($"Server started — listening on {BuildUrl(port)}");
        }
        catch (Exception ex)
        {
            SetStatus(running: false);
            AppendLog($"Failed to start: {ex.Message}");
            _api.Log.Error($"[MCP] Failed to start server: {ex.Message}");
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetStatus(bool running)
    {
        _statusDot.Fill   = running ? Brushes.LimeGreen : Brushes.Gray;
        _statusLabel.Text = running ? "Running" : "Stopped";
        _startBtn.IsEnabled = !running;
        _stopBtn.IsEnabled  =  running;
    }

    private void UpdateUrl(int port)
    {
        var url = BuildUrl(port);
        _urlLabel.Text = url;
        // Update hint box
        foreach (var child in Children)
        {
            if (child is TextBox tb && tb.IsReadOnly && tb.Text.Contains("kf-debugger"))
            {
                tb.Text = $"\"kf-debugger\": {{ \"url\": \"{url}\" }}";
                break;
            }
        }
    }

    private void AppendLog(string message)
    {
        var ts   = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{ts}] {message}\n";
        _logBox.AppendText(line);
        _logBox.ScrollToEnd();

        // Keep log bounded to last 500 lines
        const int maxLines = 500;
        var text = _logBox.Text;
        var lines = text.Split('\n');
        if (lines.Length > maxLines + 50)
        {
            _logBox.Text = string.Join('\n', lines[^maxLines..]);
            _logBox.ScrollToEnd();
        }
    }

    private static Button MakeButton(string content, string hexColor, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content           = content,
            Padding           = new Thickness(10, 3, 10, 3),
            Margin            = new Thickness(0, 0, 6, 0),
            Foreground        = Brushes.White,
            Background        = (SolidColorBrush)new BrushConverter().ConvertFrom(hexColor)!,
            BorderThickness   = new Thickness(0)
        };
        btn.Click += click;
        return btn;
    }

    private static string BuildUrl(int port) => $"http://localhost:{port}/sse";

    // ── Settings persistence ─────────────────────────────────────────────────

    private static int LoadPort()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("port", out var p))
                    return p.GetInt32();
            }
        }
        catch { }
        return 13371;
    }

    private static void SavePort(int port)
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { port })); }
        catch { }
    }
}
