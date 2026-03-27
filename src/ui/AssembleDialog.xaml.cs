using System.Windows;
using System.Windows.Controls;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.Services;

namespace KernelFlirt.UI;

public partial class AssembleDialog : Window
{
    private readonly ulong _address;
    private readonly int _originalSize;
    private readonly bool _is32Bit;
    private byte[]? _assembledBytes;

    /// <summary>Final bytes to write (with NOP padding if enabled).</summary>
    public byte[]? ResultBytes { get; private set; }

    public AssembleDialog(Instruction instruction, bool is32Bit)
    {
        InitializeComponent();

        _address = instruction.Address;
        _originalSize = instruction.Size;
        _is32Bit = is32Bit;

        TxtAddress.Text = is32Bit ? $"{_address:X8}" : $"{_address:X16}";
        TxtOriginalBytes.Text = instruction.BytesHex;
        TxtOriginal.Text = instruction.FullText;

        Loaded += (_, _) =>
        {
            TxtInput.Focus();
            TxtInput.Text = instruction.FullText;
            TxtInput.SelectAll();
        };
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (TxtInput == null || TxtPreviewBytes == null || TxtPaddedBytes == null
            || TxtStatus == null || BtnAssemble == null || ChkNopPad == null)
            return;
        string input = TxtInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(input))
        {
            TxtPreviewBytes.Text = "";
            TxtPaddedBytes.Text = "";
            TxtStatus.Text = "";
            BtnAssemble.IsEnabled = false;
            _assembledBytes = null;
            return;
        }

        var (bytes, error) = X86Assembler.Assemble(input, _address, _is32Bit);
        if (bytes == null || error != null)
        {
            TxtPreviewBytes.Text = "";
            TxtPaddedBytes.Text = "";
            TxtStatus.Text = error ?? "Assembly failed";
            BtnAssemble.IsEnabled = false;
            _assembledBytes = null;
            return;
        }

        _assembledBytes = bytes;
        TxtPreviewBytes.Text = BitConverter.ToString(bytes).Replace("-", " ");

        if (bytes.Length > _originalSize)
        {
            TxtStatus.Text = $"⚠ New instruction is {bytes.Length - _originalSize} byte(s) longer than original ({bytes.Length} > {_originalSize})";
            // Still allow — user might know what they're doing
            TxtPaddedBytes.Text = TxtPreviewBytes.Text;
            BtnAssemble.IsEnabled = true;
        }
        else if (bytes.Length < _originalSize && ChkNopPad.IsChecked == true)
        {
            // Pad with NOPs
            var padded = new byte[_originalSize];
            Array.Copy(bytes, padded, bytes.Length);
            for (int i = bytes.Length; i < _originalSize; i++)
                padded[i] = 0x90; // NOP
            TxtPaddedBytes.Text = BitConverter.ToString(padded).Replace("-", " ");
            TxtStatus.Text = $"Padded with {_originalSize - bytes.Length} NOP(s)";
            BtnAssemble.IsEnabled = true;
        }
        else
        {
            TxtPaddedBytes.Text = TxtPreviewBytes.Text;
            TxtStatus.Text = bytes.Length == _originalSize ? "Exact size match" : "";
            BtnAssemble.IsEnabled = true;
        }
    }

    private void OnAssemble(object sender, RoutedEventArgs e)
    {
        if (_assembledBytes == null) return;

        if (_assembledBytes.Length <= _originalSize && ChkNopPad.IsChecked == true)
        {
            var padded = new byte[_originalSize];
            Array.Copy(_assembledBytes, padded, _assembledBytes.Length);
            for (int i = _assembledBytes.Length; i < _originalSize; i++)
                padded[i] = 0x90;
            ResultBytes = padded;
        }
        else
        {
            ResultBytes = _assembledBytes;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
