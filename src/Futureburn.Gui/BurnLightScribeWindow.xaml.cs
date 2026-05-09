using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Futureburn.Core.LightScribe;

namespace Futureburn.Gui;

public partial class BurnLightScribeWindow : Window
{
    private LightScribeRunner? _runner;
    private IReadOnlyList<LightScribeRunner.Drive> _drives = Array.Empty<LightScribeRunner.Drive>();
    private string? _imagePath;
    private bool _burning;

    public BurnLightScribeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InitRunnerAndDrives();
    }

    private void InitRunnerAndDrives()
    {
        try
        {
            _runner = new LightScribeRunner();
        }
        catch (Exception ex)
        {
            // The most common case: LightScribe System Software (LSS) isn't
            // installed at all, or the LSPrintAPI.dll path can't be resolved.
            // Fall back to a clear, actionable status — don't crash.
            StatusText.Text = $"LightScribe runtime not available: {ex.Message}";
            DriveCombo.IsEnabled = false;
            BurnBtn.IsEnabled = false;
            return;
        }

        try
        {
            if (!_runner.AnyDrivePresent())
            {
                StatusText.Text = "LSS is installed but reports no LightScribe drives connected.";
                return;
            }
            _drives = _runner.EnumerateDrives();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Drive enumeration failed: {ex.Message}";
            return;
        }

        DriveCombo.ItemsSource = _drives
            .Select(d => $"{d.DrivePath}   {d.DisplayName}   [{d.Status}]")
            .ToList();
        if (_drives.Count > 0)
        {
            DriveCombo.SelectedIndex = 0;
            DriveStatusText.Text =
                $"{_drives.Count} LightScribe drive{(_drives.Count == 1 ? "" : "s")} detected.";
        }

        UpdateBurnButton();
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Pick a label image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" +
                     "|PNG|*.png|JPEG|*.jpg;*.jpeg|BMP|*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        SetImage(dlg.FileName);
    }

    private void SetImage(string path)
    {
        try
        {
            // Probe + preview using WPF's BitmapImage so we don't lock the
            // file (loading via stream + CacheOption.OnLoad releases the
            // handle immediately — needed if the user picks an image they
            // want to edit elsewhere while the GUI is open).
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();   // makes the BitmapImage thread-safe + immutable
            PreviewImage.Source = bmp;

            _imagePath = path;
            ImagePathText.Text = path;
            ImageDimsText.Text =
                $"{bmp.PixelWidth}×{bmp.PixelHeight} — will be center-fit onto an 800×800 white BMP for the burn.";
            StatusText.Text = "Image loaded. Pick a drive + quality, insert a flipped LightScribe disc, then Burn.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't load image:\n{ex.Message}", "Load failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateBurnButton();
    }

    private void UpdateBurnButton()
    {
        BurnBtn.IsEnabled = !_burning
                            && _runner is not null
                            && _drives.Count > 0
                            && _imagePath is not null
                            && File.Exists(_imagePath);
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_burning || _runner is null || _imagePath is null || _drives.Count == 0) return;

        if (!int.TryParse(CopiesBox.Text, out int copies) || copies < 1)
        {
            MessageBox.Show(this, "Copies must be a whole number ≥ 1.", "Bad value");
            return;
        }

        var drive   = _drives[DriveCombo.SelectedIndex];
        var quality = ((System.Windows.Controls.ComboBoxItem)QualityCombo.SelectedItem)
                       .Content?.ToString() switch
        {
            "draft"  => LightScribeRunner.Quality.Draft,
            "normal" => LightScribeRunner.Quality.Normal,
            _        => LightScribeRunner.Quality.Best,
        };

        var minutes = quality switch
        {
            LightScribeRunner.Quality.Draft  => "~3 min",
            LightScribeRunner.Quality.Normal => "~10 min",
            _                                => "~25 min",
        };

        // Confirm — labels take real time, and a wasted disc is real money.
        var confirm = MessageBox.Show(this,
            $"Burn label to {drive.DrivePath} ({drive.DisplayName})?\n\n" +
            $"Quality: {quality} ({minutes})\nCopies: {copies}\n\n" +
            $"Make sure the disc is a LightScribe-coated disc inserted UPSIDE DOWN " +
            $"(label coating toward the laser).",
            "Confirm label burn",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _burning = true;
        BurnBtn.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        StatusText.Text = "Preparing label image ...";

        // Convert the user's image into a 24-bit BMP in temp the way the SDK
        // requires, then submit + poll on a background thread.
        string preparedBmp = string.Empty;
        try
        {
            await Task.Run(() =>
            {
                preparedBmp = LightScribeRunner.PrepareLabelImage(_imagePath);

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Submitting label print job — confirm in the LightScribe dialog.";
                });

                var result = _runner.PrintAndWait(
                    drive.Index,
                    preparedBmp,
                    quality,
                    copies,
                    showOperatorDialog: false,
                    pollIntervalMs: 1000,
                    onProgress: status =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            int pct = (int)Math.Min(100, status.PercentComplete);
                            Progress.Value = pct;
                            var time = string.IsNullOrEmpty(status.TimeRemainingText)
                                         ? $"{status.SecondsRemaining}s remaining"
                                         : status.TimeRemainingText;
                            StatusText.Text = $"{status.Code} — {pct}% — {time}";
                        });
                    });

                Dispatcher.Invoke(() =>
                {
                    Progress.Value = 100;
                    StatusText.Text = $"Done — {result.Code} ({result.PercentComplete}%).";
                });
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Burn failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Burn failed.";
        }
        finally
        {
            _burning = false;
            BurnBtn.IsEnabled = true;
            if (!string.IsNullOrEmpty(preparedBmp))
            {
                try { File.Delete(preparedBmp); } catch { }
            }
        }
    }
}
