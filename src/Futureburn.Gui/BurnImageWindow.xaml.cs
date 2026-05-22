using System.IO;
using System.Windows;
using System.Windows.Input;
using Futureburn.Core.Authoring;
using Futureburn.Core.Fs;
using Futureburn.Core.Imapi;
using Futureburn.Core.Spti;

namespace Futureburn.Gui;

public partial class BurnImageWindow : Window
{
    private string? _isoPath;
    private long _isoBytes;
    // If non-null we built the ISO from a folder into a temp file; clean up
    // when we're done with it (either after burn or on window close).
    private string? _tempBuiltIso;
    private IReadOnlyList<OpticalDrive> _drives = Array.Empty<OpticalDrive>();
    private bool _burning;

    public BurnImageWindow()
    {
        InitializeComponent();
        Loaded  += (_, _) => RefreshDrives();
        Closed  += (_, _) => CleanupTempIso();
        UpdateBurnEnabled();
    }

    private void RefreshDrives()
    {
        try
        {
            _drives = DriveEnumerator.Enumerate()
                .Where(d => d.WritableProfiles.Any())  // any writable disc capability
                .ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Drive enumeration failed: {ex.Message}";
            return;
        }

        DriveCombo.ItemsSource = _drives
            .Select(d => $"{d.PrimaryMount ?? "(?)"}    {d.ProductId}")
            .ToList();
        if (_drives.Count > 0) DriveCombo.SelectedIndex = 0;
        UpdateDriveDiscText();
    }

    private async void ChooseIso_Click(object sender, RoutedEventArgs e)
    {
        var path = await FileDialogs.OpenFileAsync(
            "Choose an ISO image to burn",
            "Disc images|*.iso;*.img;*.bin|All files|*.*");
        if (path is null) return;

        CleanupTempIso();   // discard any previous folder-built temp
        _isoPath = path;
        var fi = new FileInfo(_isoPath);
        _isoBytes = fi.Length;
        IsoPathText.Text = _isoPath;
        IsoSizeText.Text = $"{FormatBytes(_isoBytes)} ({_isoBytes / 2048L:N0} sectors)";
        UpdateDiscFitText();
        UpdateDriveDiscText();
        UpdateBurnEnabled();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FileDialogs.OpenFolderAsync(
            "Choose a folder to burn (we'll build the ISO 9660 + Joliet + UDF image)");
        if (folder is null) return;

        var label = Path.GetFileName(folder);
        if (string.IsNullOrEmpty(label)) label = "FUTUREBURN";

        var tempIso = Path.Combine(Path.GetTempPath(), $"futureburn-build-{Guid.NewGuid():N}.iso");

        StatusText.Text = $"Building ISO from {folder} ...";
        Progress.Value = 0;
        Progress.IsIndeterminate = false;
        UpdateBurnEnabled();

        try
        {
            var result = await Task.Run(() =>
            {
                return FsImageBuilder.Build(folder, tempIso, label,
                    onProgress: (copied, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            int pct = total > 0 ? (int)(copied * 100 / total) : 0;
                            Progress.Value = pct;
                        });
                    });
            });

            // Take ownership of the temp file as our current "image source".
            CleanupTempIso();   // (paranoid — should never have a stale one here)
            _isoPath       = tempIso;
            _tempBuiltIso  = tempIso;
            _isoBytes      = result.TotalBytes;

            IsoPathText.Text = $"{folder}  →  {tempIso}";
            IsoSizeText.Text = $"{FormatBytes(_isoBytes)} ({_isoBytes / 2048L:N0} sectors of {result.BlockSize} B)";
            StatusText.Text  = $"ISO built ({FormatBytes(_isoBytes)}). Pick a drive and Burn.";
            Progress.Value = 100;

            UpdateDiscFitText();
            UpdateDriveDiscText();
            UpdateBurnEnabled();
        }
        catch (Exception ex)
        {
            try { File.Delete(tempIso); } catch { }
            MessageBox.Show(this, ex.Message, "ISO build failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "ISO build failed.";
            Progress.Value = 0;
        }
    }

    private async void ChooseVideo_Click(object sender, RoutedEventArgs e)
    {
        var video = await FileDialogs.OpenFileAsync(
            "Choose a video to author + burn as a DVD-Video",
            "Video files|*.mkv;*.mp4;*.avi;*.m4v;*.mov;*.webm;*.wmv;*.ts;*.m2ts|All files|*.*");
        if (video is null) return;

        bool withMenu      = MenuCheck.IsChecked == true;
        var label          = Path.GetFileNameWithoutExtension(video);
        var authoredFolder = Path.Combine(Path.GetTempPath(), $"futureburn-dvdv-{Guid.NewGuid():N}");
        var tempIso        = Path.Combine(Path.GetTempPath(), $"futureburn-build-{Guid.NewGuid():N}.iso");

        // Reuse the burn-busy gate so Burn stays disabled while we transcode.
        _burning = true;
        UpdateBurnEnabled();
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Authoring DVD-Video — transcoding, this can take a while ...";

        try
        {
            var built = await Task.Run(() =>
            {
                // 1. Author: transcode + subtitles + IFOs → a DVD-Video folder.
                MkvDvdPipeline.Author(
                    new MkvDvdPipeline.Options(video, authoredFolder,
                        IsPal: false, Label: label, Menu: withMenu),
                    onLog: line => Dispatcher.Invoke(() =>
                        StatusText.Text = line.Length > 100 ? line.Substring(0, 100) : line),
                    onProgress: frac => Dispatcher.Invoke(() =>
                    {
                        Progress.IsIndeterminate = false;
                        Progress.Value = frac * 100;
                    }));

                // 2. Build the burnable UDF image from that folder.
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Building the DVD UDF image ...";
                    Progress.IsIndeterminate = false;
                    Progress.Value = 0;
                });
                return FsImageBuilder.Build(authoredFolder, tempIso, label,
                    onProgress: (copied, total) => Dispatcher.Invoke(() =>
                        Progress.Value = total > 0 ? copied * 100.0 / total : 0));
            });

            // The authored folder was only scaffolding for the ISO — drop it.
            try { Directory.Delete(authoredFolder, recursive: true); } catch { }

            CleanupTempIso();
            _isoPath      = tempIso;
            _tempBuiltIso = tempIso;
            _isoBytes     = built.TotalBytes;

            IsoPathText.Text = $"{video}  →  DVD-Video";
            IsoSizeText.Text = $"{FormatBytes(_isoBytes)} ({_isoBytes / 2048L:N0} sectors)";
            StatusText.Text  = $"DVD-Video ready ({FormatBytes(_isoBytes)}). Pick a drive and Burn.";
            Progress.Value   = 100;

            UpdateDiscFitText();
            UpdateDriveDiscText();
        }
        catch (Exception ex)
        {
            try { Directory.Delete(authoredFolder, recursive: true); } catch { }
            try { File.Delete(tempIso); } catch { }
            MessageBox.Show(this, ex.Message, "DVD-Video authoring failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "DVD-Video authoring failed.";
            Progress.Value = 0;
        }
        finally
        {
            _burning = false;
            Progress.IsIndeterminate = false;
            UpdateBurnEnabled();
        }
    }

    private void CleanupTempIso()
    {
        if (_tempBuiltIso is not null)
        {
            try { File.Delete(_tempBuiltIso); } catch { }
            _tempBuiltIso = null;
        }
    }

    private void UpdateDiscFitText()
    {
        if (_isoBytes == 0)
        {
            DiscFitText.Text = "";
            return;
        }
        // Standard disc capacities (rough):
        //   CD-R 80 min ≈ 700 MB / 737 MB
        //   DVD-R    ≈ 4.7 GB / 4.37 GiB
        //   DVD-R DL ≈ 8.5 GB / 7.96 GiB
        //   BD-R     ≈ 25 GB / 23.3 GiB
        const long cd = 737L * 1024 * 1024;
        const long dvd = 4_700_372_992L;
        const long dvdDl = 8_543_666_176L;
        const long bd = 25_025_314_816L;
        var fits = new List<string>();
        if (_isoBytes <= cd)    fits.Add("CD-R (700 MB)");
        if (_isoBytes <= dvd)   fits.Add("DVD-R (4.7 GB)");
        if (_isoBytes <= dvdDl) fits.Add("DVD-R DL (8.5 GB)");
        if (_isoBytes <= bd)    fits.Add("BD-R (25 GB)");
        DiscFitText.Text = fits.Count > 0
            ? "Fits on: " + string.Join(", ", fits)
            : "WARNING: image is larger than any standard single-layer disc.";
    }

    private void UpdateDriveDiscText()
    {
        if (DriveCombo.SelectedIndex < 0 || DriveCombo.SelectedIndex >= _drives.Count)
        {
            DriveDiscText.Text = "";
            return;
        }
        var d = _drives[DriveCombo.SelectedIndex];
        var profile = d.CurrentProfiles.FirstOrDefault(p => p.Code != 0);
        DriveDiscText.Text = profile is null
            ? "No disc loaded in selected drive."
            : $"Loaded: {profile.Name}";
    }

    private void UpdateBurnEnabled()
    {
        BurnBtn.IsEnabled = !_burning
            && _isoPath is not null
            && _drives.Count > 0
            && DriveCombo.SelectedIndex >= 0;
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_burning || _isoPath is null) return;
        if (DriveCombo.SelectedIndex < 0 || DriveCombo.SelectedIndex >= _drives.Count)
        {
            MessageBox.Show(this, "Pick a drive first.", "Burn",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var drive = _drives[DriveCombo.SelectedIndex];
        var speedStr = (SpeedCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "default";
        int? cdSpeedX = null;
        if (speedStr.EndsWith("x") && int.TryParse(speedStr.AsSpan(0, speedStr.Length - 1), out int x))
            cdSpeedX = x;

        var summary = $"This will write {FormatBytes(_isoBytes)} to {drive.PrimaryMount}.\n\n" +
                      $"ISO:    {_isoPath}\n" +
                      $"Speed:  {(cdSpeedX is { } sp ? sp + "x" : "drive default")}\n\n" +
                      $"Continue?";
        if (MessageBox.Show(this, summary, "Confirm burn",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await DoBurnAsync(drive, _isoPath, cdSpeedX);
    }

    private async Task DoBurnAsync(OpticalDrive drive, string isoPath, int? cdSpeedX)
    {
        _burning = true;
        UpdateBurnEnabled();
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Planning ...";

        try
        {
            await Task.Run(() =>
            {
                var plan = SptiDataBurner.Plan(drive, isoPath);
                Dispatcher.Invoke(() =>
                {
                    Progress.IsIndeterminate = false;
                    StatusText.Text = $"Burning {FormatBytes(plan.ImageBytes)} to {drive.PrimaryMount} ...";
                });

                SptiDataBurner.ExecuteBurn(
                    plan,
                    requestedSpeedX: cdSpeedX,
                    onProgress: (written, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            int pct = total > 0 ? (int)(written * 100 / total) : 0;
                            Progress.Value = pct;
                        });
                    });

                Dispatcher.Invoke(() =>
                {
                    Progress.Value = 100;
                    StatusText.Text = $"Done. {FormatBytes(plan.ImageBytes)} written, disc finalized.";
                });
            });
        }
        catch (AudioCdBurner.BurnException ex)
        {
            MessageBox.Show(this, ex.Message, "Burn failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Burn failed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Burn failed (unexpected).";
        }
        finally
        {
            _burning = false;
            Progress.IsIndeterminate = false;
            UpdateBurnEnabled();
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            < KB => $"{bytes} B",
            < MB => $"{bytes / (double)KB:0.##} KB",
            < GB => $"{bytes / (double)MB:0.##} MB",
            _    => $"{bytes / (double)GB:0.##} GB",
        };
    }
}
