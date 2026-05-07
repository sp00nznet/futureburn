using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Futureburn.Core.Audio;
using Futureburn.Core.Imapi;
using Futureburn.Core.Spti;

namespace Futureburn.Gui;

public partial class BurnAudioCdWindow : Window
{
    public sealed class TrackItem
    {
        public required int Index           { get; set; }
        public required string Title        { get; init; }
        public required string FullPath     { get; init; }
        public required TimeSpan Duration   { get; init; }
        public required AudioInfo Info      { get; init; }
        public string DurationDisplay       => Duration.ToString(@"mm\:ss");
        public string FormatDisplay         => Info.IsCdFormat
            ? $"CD-ready ({Info.SampleRate / 1000} kHz)"
            : $"{Info.SampleRate / 1000} kHz / {Info.Channels}ch / will resample";
    }

    private readonly ObservableCollection<TrackItem> _tracks = new();
    private IReadOnlyList<OpticalDrive> _drives = Array.Empty<OpticalDrive>();
    private bool _burning;

    public BurnAudioCdWindow()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        _tracks.CollectionChanged += (_, _) => UpdateTotals();
        Loaded += (_, _) => RefreshDrives();
        UpdateTotals();
    }

    private void RefreshDrives()
    {
        try
        {
            _drives = DriveEnumerator.Enumerate()
                .Where(d => d.WritableProfiles.Any(p => p.Code == 0x0009 || p.Code == 0x000A))
                .ToList();
        }
        catch (Exception ex)
        {
            _drives = Array.Empty<OpticalDrive>();
            StatusText.Text = $"Drive enumeration failed: {ex.Message}";
            return;
        }

        DriveCombo.ItemsSource = _drives
            .Select(d => $"{d.PrimaryMount ?? "(?)"}    {d.ProductId} ({d.Revision})")
            .ToList();
        if (_drives.Count > 0)
        {
            DriveCombo.SelectedIndex = 0;
            StatusText.Text = $"{_drives.Count} CD-capable drive{(_drives.Count == 1 ? "" : "s")} found.";
        }
        else
        {
            StatusText.Text = "No CD-writable drives detected.";
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|All files|*.*",
            Multiselect = true,
            Title = "Add audio tracks",
        };
        if (dlg.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int added = 0, skipped = 0;
            foreach (var path in dlg.FileNames)
            {
                if (TryAddTrack(path)) added++;
                else skipped++;
            }
            StatusText.Text = $"Added {added} track{(added == 1 ? "" : "s")}" +
                              (skipped > 0 ? $" ({skipped} skipped)" : "") + ".";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private bool TryAddTrack(string path)
    {
        try
        {
            var info = AudioDecoder.Probe(path);
            _tracks.Add(new TrackItem
            {
                Index    = _tracks.Count + 1,
                Title    = Path.GetFileNameWithoutExtension(path),
                FullPath = path,
                Duration = info.Duration,
                Info     = info,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var idx = TrackList.SelectedIndex;
        if (idx <= 0) return;
        _tracks.Move(idx, idx - 1);
        TrackList.SelectedIndex = idx - 1;
        Renumber();
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var idx = TrackList.SelectedIndex;
        if (idx < 0 || idx >= _tracks.Count - 1) return;
        _tracks.Move(idx, idx + 1);
        TrackList.SelectedIndex = idx + 1;
        Renumber();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var idx = TrackList.SelectedIndex;
        if (idx < 0) return;
        _tracks.RemoveAt(idx);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < _tracks.Count; i++)
            _tracks[i].Index = i + 1;
        TrackList.Items.Refresh();
    }

    private void UpdateTotals()
    {
        var total = TimeSpan.FromTicks(_tracks.Sum(t => t.Duration.Ticks));
        TotalText.Text = $"{_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")}, " +
                         $"{total:hh\\:mm\\:ss} total " +
                         $"(disc limits: 74 / 80 min)";
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_burning) return;
        if (_tracks.Count == 0)
        {
            MessageBox.Show(this, "Add some tracks first.", "Burn",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DriveCombo.SelectedIndex < 0 || DriveCombo.SelectedIndex >= _drives.Count)
        {
            MessageBox.Show(this, "Pick a drive.", "Burn",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var drive = _drives[DriveCombo.SelectedIndex];
        var engine = (EngineCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "spti";
        var speedStr = (SpeedCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
        int? cdSpeedX = null;
        if (speedStr.EndsWith("x") && int.TryParse(speedStr.AsSpan(0, speedStr.Length - 1), out int x))
            cdSpeedX = x;

        // Confirm.
        var summary = $"This will write {_tracks.Count} tracks ({TotalText.Text.Split(',')[1].Trim()}) " +
                      $"to {drive.PrimaryMount} via IMAPI {engine}.\n\n" +
                      $"Speed: {(cdSpeedX is { } sp ? sp + "x" : "drive default")}\n\n" +
                      $"Continue?";
        if (MessageBox.Show(this, summary, "Confirm burn",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (engine != "spti")
        {
            MessageBox.Show(this,
                "This GUI tile currently only burns via the spti engine. " +
                "Use the CLI for v1 or v2.",
                "Engine not supported in GUI yet",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await DoBurnAsync(drive, cdSpeedX);
    }

    private async Task DoBurnAsync(OpticalDrive drive, int? cdSpeedX)
    {
        _burning = true;
        BurnBtn.IsEnabled = false;
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Planning ...";

        var tempDir = Path.Combine(Path.GetTempPath(), $"futureburn-gui-{Guid.NewGuid():N}");
        var playlist = BuildPlaylistFromTracks();

        try
        {
            // Plan + execute on a worker thread; marshal status updates back via the dispatcher.
            await Task.Run(() =>
            {
                var plan = SptiAudioCdBurner.Plan(drive, playlist, tempDir);

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Burning {plan.Tracks.Count} tracks ...";
                    Progress.IsIndeterminate = false;
                });

                SptiAudioCdBurner.ExecuteBurn(
                    plan,
                    requestedCdSpeedX: cdSpeedX,
                    onTrackStart: (current, total) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"Burning track {current} of {total} ...";
                            Progress.Value = 0;
                        });
                    },
                    onProgress: (current, total, written, totalBytes) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            int pct = totalBytes > 0 ? (int)(written * 100 / totalBytes) : 0;
                            Progress.Value = pct;
                        });
                    });

                var verified = SptiAudioCdBurner.Verify(plan);
                Dispatcher.Invoke(() =>
                {
                    Progress.Value = 100;
                    StatusText.Text = verified.Passed
                        ? $"Done. Disc finalized + verified ({verified.TrackCount} tracks)."
                        : $"Done. {verified.Mismatches.Count} verification mismatch(es) — see CLI cd-info for details.";
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
            BurnBtn.IsEnabled = true;
            Progress.IsIndeterminate = false;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private Playlist BuildPlaylistFromTracks()
    {
        // Synthesize a Playlist record directly from the GUI's track list — no
        // need to write a temp .m3u8 file just to hand to Plan().
        var entries = _tracks.Select(t => new PlaylistEntry(
            Path:         t.FullPath,
            OriginalPath: t.FullPath,
            Title:        t.Title,
            Duration:     t.Duration)).ToList();
        return new Playlist(SourcePath: "(GUI)", IsExtended: true, Entries: entries);
    }
}
