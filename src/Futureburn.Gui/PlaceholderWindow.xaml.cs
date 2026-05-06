using System.Windows;

namespace Futureburn.Gui;

public partial class PlaceholderWindow : Window
{
    public PlaceholderWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
