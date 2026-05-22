using System.Reflection;
using System.Windows;

namespace Futureburn.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Title = $"futureburn v{version}";
        StatusText.Text = $"v{version} — pick a tile";
    }

    private void BurnAudio_Click(object sender, RoutedEventArgs e)
    {
        var w = new BurnAudioCdWindow { Owner = this };
        w.Show();
    }

    private void BurnVideo_Click(object sender, RoutedEventArgs e)
    {
        var w = new BurnImageWindow { Owner = this };
        w.Show();
    }

    private void CdInfo_Click(object sender, RoutedEventArgs e)
    {
        var w = new CdInfoWindow { Owner = this };
        w.Show();
    }

    private void BurnLabel_Click(object sender, RoutedEventArgs e)
    {
        var w = new BurnLightScribeWindow { Owner = this };
        w.Show();
    }

    private void OpenPlaceholder(string title, string message)
    {
        var w = new PlaceholderWindow(title, message) { Owner = this };
        w.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        MessageBox.Show(
            $"futureburn v{version}\n\n" +
            "A friendly modern CD/DVD burner for Windows 11.\n" +
            "A passion project. Burns at your own risk.\n\n" +
            "https://github.com/sp00nznet/futureburn",
            "About futureburn",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
