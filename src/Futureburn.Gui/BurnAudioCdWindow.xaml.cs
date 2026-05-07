using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Futureburn.Core.Audio;
using Futureburn.Core.Imapi;
using Futureburn.Core.Spti;
using NAudio.Wave;

namespace Futureburn.Gui;

public partial class BurnAudioCdWindow : Window
{
    public sealed class TrackItem
    {
        public required int Index           { get; set; }
        // Title is mutable so users can rename; the GridView binding doesn't update
        // automatically without INotifyPropertyChanged, so we call TrackList.Items.Refresh()
        // after a rename. Keeping this simple over wiring up INPC for one column.
        public required string Title        { get; set; }
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

    // For drag-reorder within the track list.
    private Point _dragStartPoint;
    private TrackItem? _dragItem;

    // Audio preview state (NAudio).
    private IWavePlayer? _player;
    private WaveStream? _playerReader;

    public BurnAudioCdWindow()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        _tracks.CollectionChanged += (_, _) => UpdateTotals();
        Loaded += (_, _) => RefreshDrives();
        Closed += (_, _) => StopPlayback();
        // SpeedCombo affects the est-burn-time calculation in UpdateTotals.
        SpeedCombo.SelectionChanged += (_, _) => UpdateTotals();
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

    private (int added, int skipped) TryAddTracksFromFolder(string folder)
    {
        var supported = AudioDecoder.SupportedExtensions
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();
        int added = 0, skipped = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                                    .Where(f => supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (TryAddTrack(f)) added++;
            else                skipped++;
        }
        return (added, skipped);
    }

    private (int added, int skipped) TryAddTracksFromPlaylist(string playlistPath)
    {
        try
        {
            var pl = PlaylistParser.Load(playlistPath);
            int added = 0, skipped = 0;
            foreach (var entry in pl.Entries)
            {
                if (File.Exists(entry.Path) && TryAddTrack(entry.Path)) added++;
                else                                                     skipped++;
            }
            return (added, skipped);
        }
        catch
        {
            return (0, 1);
        }
    }

    // ---- Drag-and-drop ----------------------------------------------------

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int added = 0, skipped = 0;
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var (a, s) = TryAddTracksFromFolder(path);
                    added += a; skipped += s;
                }
                else if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is ".m3u" or ".m3u8")
                    {
                        var (a, s) = TryAddTracksFromPlaylist(path);
                        added += a; skipped += s;
                    }
                    else if (AudioDecoder.IsSupported(path))
                    {
                        if (TryAddTrack(path)) added++; else skipped++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            StatusText.Text = added == 0
                ? $"Nothing added (dropped items had no recognizable audio)."
                : $"Added {added} track{(added == 1 ? "" : "s")}" +
                  (skipped > 0 ? $" ({skipped} skipped)" : "") + ".";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // ---- Add folder + load/save playlist ---------------------------------

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder of audio files",
        };
        if (dlg.ShowDialog(this) != true) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (added, skipped) = TryAddTracksFromFolder(dlg.FolderName);
            StatusText.Text = $"Added {added} track{(added == 1 ? "" : "s")} from folder" +
                              (skipped > 0 ? $" ({skipped} skipped)" : "") + ".";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "M3U / M3U8 playlists|*.m3u;*.m3u8|All files|*.*",
            Title = "Load a playlist",
        };
        if (dlg.ShowDialog(this) != true) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (added, skipped) = TryAddTracksFromPlaylist(dlg.FileName);
            StatusText.Text = added == 0
                ? "Couldn't load any tracks from that playlist."
                : $"Loaded {added} track{(added == 1 ? "" : "s")} from {Path.GetFileName(dlg.FileName)}" +
                  (skipped > 0 ? $" ({skipped} skipped)" : "") + ".";
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void SaveM3U_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0)
        {
            MessageBox.Show(this, "Add some tracks before saving.", "Save M3U8",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Extended M3U8|*.m3u8|All files|*.*",
            DefaultExt = ".m3u8",
            FileName = "playlist.m3u8",
            Title = "Save current track list as M3U8",
        };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var t in _tracks)
        {
            int seconds = (int)Math.Round(t.Duration.TotalSeconds);
            sb.AppendLine($"#EXTINF:{seconds},{t.Title}");
            sb.AppendLine(t.FullPath);
        }
        File.WriteAllText(dlg.FileName, sb.ToString());
        StatusText.Text = $"Saved {_tracks.Count} tracks to {dlg.FileName}.";
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

    // ---- Rename ----------------------------------------------------------

    private void Rename_Click(object sender, RoutedEventArgs e) => RenameSelected();

    private void TrackList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { RenameSelected(); e.Handled = true; }
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is TrackItem) { RenameSelected(); e.Handled = true; }
    }

    private void RenameSelected()
    {
        if (TrackList.SelectedItem is not TrackItem t) return;
        var dlg = new TextInputDialog("Rename track", "Title:", t.Title) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        t.Title = dlg.Value;
        TrackList.Items.Refresh();
    }

    // ---- Audio preview ---------------------------------------------------

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (TrackList.SelectedItem is not TrackItem t) return;
        StopPlayback();
        try
        {
            // MediaFoundationReader handles all formats Windows knows about
            // (MP3, M4A, AAC, WMA, FLAC, plus WAV).
            _playerReader = new MediaFoundationReader(t.FullPath);
            _player = new WaveOutEvent();
            _player.PlaybackStopped += (_, _) => Dispatcher.Invoke(StopPlayback);
            _player.Init(_playerReader);
            _player.Play();
            PlayBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            StatusText.Text = $"Playing: {t.Title}";
        }
        catch (Exception ex)
        {
            StopPlayback();
            MessageBox.Show(this, ex.Message, "Playback failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        try { _player?.Stop(); } catch { }
        _player?.Dispose();
        _playerReader?.Dispose();
        _player       = null;
        _playerReader = null;
        PlayBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
    }

    // ---- Drag-reorder within the track list ------------------------------

    private void TrackList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        // Find the row under the cursor (if any) so we can drag it later.
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not ListViewItem) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        _dragItem = (hit as ListViewItem)?.DataContext as TrackItem;
    }

    private void TrackList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pt = e.GetPosition(null);
        // Wait until the cursor has moved past the system drag threshold.
        if (Math.Abs(pt.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
         && Math.Abs(pt.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject("FuturreburnTrack", _dragItem);
        DragDrop.DoDragDrop(TrackList, data, DragDropEffects.Move);
        _dragItem = null;
    }

    private void TrackList_DragOver(object sender, DragEventArgs e)
    {
        // Only accept our own internal track drags as Move; anything else
        // (file drops onto the list) is bubbled up to the Window's Drop handler.
        e.Effects = e.Data.GetDataPresent("FuturreburnTrack")
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void TrackList_DropOnList(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FuturreburnTrack")) return;
        var src = (TrackItem)e.Data.GetData("FuturreburnTrack");
        int oldIdx = _tracks.IndexOf(src);
        if (oldIdx < 0) return;

        // Find the row we dropped onto.
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not ListViewItem) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        var targetItem = (hit as ListViewItem)?.DataContext as TrackItem;

        int newIdx = targetItem is null ? _tracks.Count - 1 : _tracks.IndexOf(targetItem);
        if (newIdx < 0 || newIdx == oldIdx) return;
        _tracks.Move(oldIdx, newIdx);
        Renumber();
        TrackList.SelectedIndex = newIdx;
        e.Handled = true;
    }

    private void UpdateTotals()
    {
        var total = TimeSpan.FromTicks(_tracks.Sum(t => t.Duration.Ticks));
        TotalText.Text = $"{_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")}, " +
                         $"{total:hh\\:mm\\:ss} total";

        // Capacity fit + estimated burn time at the currently-selected speed.
        string fits;
        if (_tracks.Count == 0)
            fits = "(disc limits: 74 / 80 min)";
        else if (total.TotalMinutes <= 74)
            fits = "fits on a 74-min CD-R";
        else if (total.TotalMinutes <= 80)
            fits = "needs an 80-min CD-R (won't fit on 74-min)";
        else
            fits = $"⚠ exceeds standard CD-R capacity by {total.TotalMinutes - 80:0.0} min";

        long totalSectors = _tracks.Sum(t => Core.Audio.CdFormat.SectorsForDuration(t.Duration));
        string speedLabel = (SpeedCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
        string burn = "";
        if (speedLabel.EndsWith("x") && int.TryParse(speedLabel.AsSpan(0, speedLabel.Length - 1), out int x) && x > 0)
        {
            // Audio CD writes at x * 75 sectors/sec.
            double burnSec = totalSectors / (double)(x * 75);
            // Add ~30 sec for finalization overhead.
            burnSec += 30;
            burn = $"  •  est. burn at {x}x: ~{TimeSpan.FromSeconds(burnSec):mm\\:ss}";
        }

        FitsText.Text = fits + burn;
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
