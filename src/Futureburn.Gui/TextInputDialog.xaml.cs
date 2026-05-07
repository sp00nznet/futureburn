using System.Windows;
using System.Windows.Input;

namespace Futureburn.Gui;

public partial class TextInputDialog : Window
{
    public string Value { get; private set; } = "";

    public TextInputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title          = title;
        PromptText.Text = prompt;
        ValueBox.Text   = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  Ok_Click(sender, e);
        if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
