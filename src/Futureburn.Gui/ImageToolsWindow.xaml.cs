using System.IO;
using System.Windows;
using Futureburn.Core.Imapi;

namespace Futureburn.Gui;

// The optical image toolkit tile: rip / convert / mount / unmount / erase.
// Long operations run on a background Task; Core's onProgress/onLog callbacks
// are marshalled back to the UI thread via the Dispatcher.
public partial class ImageToolsWindow : Window
{
    public ImageToolsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDrives();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        DriveCombo.Items.Clear();
        try
        {
            foreach (var d in DriveEnumerator.Enumerate())
                DriveCombo.Items.Add(new DriveItem((d.PrimaryMount ?? "(no letter)") + "   " + d.ProductId, d));
            if (DriveCombo.Items.Count > 0) DriveCombo.SelectedIndex = 0;
            StatusText.Text = $"{DriveCombo.Items.Count} optical drive(s).";
        }
        catch (Exception ex) { StatusText.Text = "drive enumeration failed: " + ex.Message; }
    }

    private OpticalDrive? SelectedDrive() => (DriveCombo.SelectedItem as DriveItem)?.Drive;

    // --- Rip disc → ISO ------------------------------------------------------
    private async void Rip_Click(object sender, RoutedEventArgs e)
    {
        var drive = SelectedDrive();
        if (drive is null) { Warn("Pick a drive with a data disc first."); return; }

        Futureburn.Core.Image.DiscRipper.RipPlan plan;
        try { plan = Futureburn.Core.Image.DiscRipper.Plan(drive); }
        catch (Exception ex) { Warn("Can't rip: " + ex.Message); return; }

        var outIso = await FileDialogs.SaveFileAsync(
            "Rip disc to ISO", "ISO image (*.iso)|*.iso", "disc.iso", ".iso");
        if (outIso is null) return;

        await RunAsync($"rip {drive.PrimaryMount} → {outIso}", () =>
        {
            var r = Futureburn.Core.Image.DiscRipper.Rip(plan, outIso, OnProgress, OnLog);
            OnLog($"RIP COMPLETE — {r.BytesWritten:N0} bytes" +
                  (r.BadSectors > 0 ? $"  ({r.BadSectors:N0} unreadable sectors zero-filled)" : ""));
        });
    }

    // --- Convert image → ISO -------------------------------------------------
    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var input = await FileDialogs.OpenFileAsync(
            "Choose an image to convert",
            "Disc images (*.cue;*.bin;*.mds;*.mdf;*.nrg;*.iso)|*.cue;*.bin;*.mds;*.mdf;*.nrg;*.iso|All files (*.*)|*.*");
        if (input is null) return;
        var output = await FileDialogs.SaveFileAsync(
            "Convert to ISO", "ISO image (*.iso)|*.iso",
            Path.GetFileNameWithoutExtension(input) + ".iso", ".iso");
        if (output is null) return;

        await RunAsync($"convert {Path.GetFileName(input)} → {Path.GetFileName(output)}", () =>
        {
            var r = Futureburn.Core.Image.ImageConverter.ToIso(input, output, OnProgress, OnLog);
            OnLog($"CONVERTED {r.SourceFormat} → {r.IsoBytes:N0} bytes ISO ({r.Sectors:N0} sectors)");
        });
    }

    // --- Mount image (ISO native; others convert-then-mount) -----------------
    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        var image = await FileDialogs.OpenFileAsync(
            "Choose an image to mount",
            "Disc images (*.iso;*.img;*.vhd;*.cue;*.bin;*.mds;*.mdf;*.nrg)|*.iso;*.img;*.vhd;*.cue;*.bin;*.mds;*.mdf;*.nrg|All files (*.*)|*.*");
        if (image is null) return;

        await RunAsync($"mount {Path.GetFileName(image)}", () =>
        {
            string toMount = image;
            if (!Futureburn.Core.Tools.DiskImageMounter.IsNativelyMountable(image))
            {
                var tempIso = Path.Combine(Path.GetTempPath(), $"futureburn-mount-{Guid.NewGuid():N}.iso");
                OnLog($"{Path.GetExtension(image)} isn't natively mountable — converting to a temp ISO ...");
                Futureburn.Core.Image.ImageConverter.ToIso(image, tempIso, OnProgress, OnLog);
                toMount = tempIso;
            }
            var letter = Futureburn.Core.Tools.DiskImageMounter.Mount(toMount);
            OnLog($"MOUNTED at {letter}:\\   (unmount with the Unmount button, entering {letter})");
        });
    }

    // --- Unmount by drive letter or image path -------------------------------
    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextInputDialog("Unmount", "Drive letter (e.g. H) or the image path that was mounted:", "") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        var arg = dlg.Value.Trim();

        await RunAsync($"unmount {arg}", () =>
        {
            bool isLetter = arg.Length <= 3 && char.IsLetter(arg[0]) && (arg.Length == 1 || arg[1] == ':');
            if (isLetter) Futureburn.Core.Tools.DiskImageMounter.UnmountByLetter(arg[0]);
            else          Futureburn.Core.Tools.DiskImageMounter.UnmountByImage(arg);
            OnLog($"UNMOUNTED {arg}");
        });
    }

    // --- Erase a rewritable --------------------------------------------------
    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        var drive = SelectedDrive();
        if (drive is null) { Warn("Pick a drive with a rewritable disc first."); return; }
        var name = Mmc.LookupProfile(drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0).Name;

        var ans = MessageBox.Show(this,
            $"Permanently ERASE everything on {drive.PrimaryMount} ({name})?",
            "Erase disc", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes) return;

        await RunAsync($"erase {drive.PrimaryMount}", () =>
        {
            var r = Futureburn.Core.Spti.DiscEraser.Erase(drive, full: false, OnLog);
            OnLog($"ERASED — {r.MediaName} via {r.Method}. Disc is blank.");
        });
    }

    // --- infra ---------------------------------------------------------------
    private void OnLog(string msg) => Dispatcher.BeginInvoke(() =>
    {
        Log.AppendText(msg + "\n");
        Log.ScrollToEnd();
    });

    private void OnProgress(long done, long total) => Dispatcher.BeginInvoke(() =>
    {
        Progress.Value = total > 0 ? done * 100.0 / total : 0;
    });

    private void Warn(string m) =>
        MessageBox.Show(this, m, "Image Tools", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task RunAsync(string what, Action work)
    {
        SetBusy(true);
        OnLog($"\n$ {what}");
        StatusText.Text = what;
        try
        {
            await Task.Run(work);
            StatusText.Text = "done.";
        }
        catch (Exception ex)
        {
            OnLog("ERROR: " + ex.Message);
            StatusText.Text = "error — see log.";
            MessageBox.Show(this, ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            Progress.Value = 0;
        }
    }

    private void SetBusy(bool busy)
    {
        ButtonPanel.IsEnabled = !busy;
        DriveCombo.IsEnabled  = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private sealed record DriveItem(string Display, OpticalDrive Drive)
    {
        public override string ToString() => Display;
    }
}
