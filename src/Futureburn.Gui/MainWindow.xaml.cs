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

    private void BurnAudio_Click(object sender, RoutedEventArgs e) =>
        OpenPlaceholder(
            "Burn Audio CD",
            "Audio CD burning lands in v0.1. We'll do drag-and-drop tracks, reordering, and the burn.");

    private void BurnVideo_Click(object sender, RoutedEventArgs e) =>
        OpenPlaceholder(
            "Burn Video DVD",
            "Video DVD burning is around v0.5. Drop in an MKV, get a watchable disc — no menus, no chapters, just the movie.");

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
