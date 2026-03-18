using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace KernelFlirt.UI;

public partial class ColorPickerDialog : Window
{
    public string SelectedHex { get; private set; }

    private static int[] _customColors = new int[16];

    public ColorPickerDialog(string initialHex)
    {
        InitializeComponent();
        SelectedHex = initialHex;
        TxtHex.Text = initialHex;
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        var hex = TxtHex.Text.Trim();
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            Preview.Background = new SolidColorBrush(color);
        }
        catch
        {
            Preview.Background = Brushes.Transparent;
        }
    }

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        // Parse current color
        int r = 128, g = 128, b = 128;
        try
        {
            var current = (Color)ColorConverter.ConvertFromString(TxtHex.Text.Trim());
            r = current.R; g = current.G; b = current.B;
        }
        catch { /* ignore */ }

        // Call native ChooseColor dialog
        var hwnd = new WindowInteropHelper(this).Handle;
        var result = ShowNativeColorPicker(hwnd, r, g, b);
        if (result.HasValue)
        {
            var (nr, ng, nb) = result.Value;
            TxtHex.Text = $"#{nr:X2}{ng:X2}{nb:X2}";
        }
    }

    private static (int R, int G, int B)? ShowNativeColorPicker(IntPtr owner, int r, int g, int b)
    {
        var cc = new CHOOSECOLOR();
        cc.lStructSize = Marshal.SizeOf<CHOOSECOLOR>();
        cc.hwndOwner = owner;
        cc.rgbResult = r | (g << 8) | (b << 16); // COLORREF = 0x00BBGGRR
        cc.Flags = CC_FULLOPEN | CC_RGBINIT | CC_ANYCOLOR;

        // Pin custom colors array
        var handle = GCHandle.Alloc(_customColors, GCHandleType.Pinned);
        try
        {
            cc.lpCustColors = handle.AddrOfPinnedObject();
            if (ChooseColorW(ref cc))
            {
                int cr = cc.rgbResult;
                return (cr & 0xFF, (cr >> 8) & 0xFF, (cr >> 16) & 0xFF);
            }
        }
        finally
        {
            handle.Free();
        }
        return null;
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
            TxtHex.Text = hex;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var hex = TxtHex.Text.Trim();
        try
        {
            ColorConverter.ConvertFromString(hex);
            SelectedHex = hex;
            DialogResult = true;
        }
        catch
        {
            MessageBox.Show("Invalid color format. Use #RRGGBB.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Native interop
    private const int CC_RGBINIT = 0x01;
    private const int CC_FULLOPEN = 0x02;
    private const int CC_ANYCOLOR = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ChooseColorW(ref CHOOSECOLOR cc);
}
