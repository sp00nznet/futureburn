using System.Text;
using System.Windows;
using System.Windows.Controls;
using Futureburn.Core.Imapi;

namespace Futureburn.Gui;

public partial class CdInfoWindow : Window
{
    public CdInfoWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDrives();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        DriveList.Items.Clear();
        try
        {
            var drives = DriveEnumerator.Enumerate();
            foreach (var d in drives)
            {
                var label = (d.PrimaryMount ?? "(no letter)") + "    " + d.ProductId;
                DriveList.Items.Add(new DriveItem(label, d));
            }
            StatusText.Text = drives.Count == 0
                ? "No optical drives found."
                : $"{drives.Count} drive{(drives.Count == 1 ? "" : "s")} found.";
            if (DriveList.Items.Count > 0)
                DriveList.SelectedIndex = 0;
            else
                DetailsText.Text = "(No optical drives detected on this system.)";
        }
        catch (Exception ex)
        {
            DetailsText.Text = $"IMAPI2 enumeration failed:\n\n{ex.Message}";
            StatusText.Text = "error — see details";
        }
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is not DriveItem item) { DetailsText.Text = ""; return; }
        DetailsText.Text = FormatDrive(item.Drive);
    }

    private static string FormatDrive(OpticalDrive d)
    {
        var sb = new StringBuilder();
        var letters = d.MountPoints.Count > 0 ? string.Join(", ", d.MountPoints) : "(no drive letter)";

        sb.AppendLine(letters);
        sb.AppendLine($"  {d.VendorId} {d.ProductId}  (firmware {d.Revision})");
        sb.AppendLine();

        var reads = string.Join(", ", d.ReadOnlyProfiles.Select(p => p.Name).Distinct());
        var writes = string.Join(", ", d.WritableProfiles.Select(p => p.Name).Distinct());
        if (!string.IsNullOrEmpty(reads))  sb.AppendLine($"Reads:  {reads}");
        if (!string.IsNullOrEmpty(writes)) sb.AppendLine($"Writes: {writes}");

        var loaded = d.CurrentProfiles.Where(p => p.Code != 0).Select(p => p.Name).ToList();
        sb.AppendLine($"Loaded: {(loaded.Count > 0 ? string.Join(", ", loaded) : "(no disc)")}");
        sb.AppendLine();

        if (loaded.Count == 0)
        {
            sb.AppendLine("Insert a disc and click Refresh.");
            return sb.ToString();
        }

        sb.AppendLine("--- Disc info ---");
        try
        {
            var disc = DiscInspector.InspectDrive(d);
            sb.AppendLine($"Media:  {disc.MediaTypeName}");
            if (!disc.HasFormatDetails)
            {
                sb.AppendLine();
                sb.AppendLine("Format details unavailable.");
                sb.AppendLine("(Could be finalized, read-only, or a non-data format like an audio CD.)");
            }
            else
            {
                sb.AppendLine($"Status: {(disc.IsBlank ? "Blank" : "Has data")}");
                sb.AppendLine($"Total:  {FormatBytes(disc.TotalBytes)}  ({disc.TotalSectors:N0} sectors)");
                sb.AppendLine($"Free:   {FormatBytes(disc.FreeBytes)}  ({disc.FreeSectors:N0} sectors)");
                if (disc.SupportedWriteSpeedsKbps.Count > 0)
                    sb.AppendLine($"Speeds: {string.Join(", ", disc.SupportedWriteSpeedsKbps.Select(s => $"{s:N0} KB/s"))}");
            }
        }
        catch (DiscInspector.NoMediaException ex)
        {
            sb.AppendLine(ex.Message);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Couldn't inspect: {ex.Message}");
        }
        return sb.ToString();
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

    private sealed record DriveItem(string Display, OpticalDrive Drive)
    {
        public override string ToString() => Display;
    }
}
