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

    private void BurnVideo_Click(object sender, RoutedEventArgs e) =>
        OpenPlaceholder(
            "Burn Blu-ray / DVD",
            "Drop in your MKV (or whatever) and get a disc that plays itself. No menus, no chapters, no DRM dance — just the movie. " +
            "Coming after we lock down audio CD burning. Same drop-and-burn UX as the Burn Audio CD tile, just bigger media.");

    private void CdInfo_Click(object sender, RoutedEventArgs e)
    {
        var w = new CdInfoWindow { Owner = this };
        w.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        OpenPlaceholder(
            "Settings",
            "Settings show up around v0.6. For now, the program decides everything for you.");

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
