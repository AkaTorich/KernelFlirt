using System.Windows;

namespace KernelFlirt.UI;

public partial class InputDialog : Window
{
    public string InputText => TxtInput.Text;

    public InputDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        TxtPrompt.Text = prompt;
        Loaded += (_, _) => TxtInput.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
