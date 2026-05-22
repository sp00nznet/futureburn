using System.Text;
using System.Windows;
using Futureburn.Core.Imapi;
using Futureburn.Core.Spti;

namespace Futureburn.Gui;

// At-a-glance "what's loaded" dashboard: every optical drive, the disc in it,
// its state, and a suggested next action. The CD Info window does the deep
// single-disc inspection; this is the quick overview of all drives at once.

public partial class DriveStatusWindow : Window
{
    public DriveStatusWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        try
        {
            var drives = DriveEnumerator.Enumerate();
            if (drives.Count == 0)
            {
                ReadoutText.Text = "No optical drives detected on this system.";
                StatusText.Text  = "no drives found";
                return;
            }

            var sb = new StringBuilder();
            foreach (var d in drives)
            {
                sb.AppendLine(SummarizeDrive(d));
                sb.AppendLine();
            }
            ReadoutText.Text = sb.ToString().TrimEnd();
            StatusText.Text  = $"{drives.Count} drive{(drives.Count == 1 ? "" : "s")} — " +
                               "Refresh after inserting or ejecting a disc.";
        }
        catch (Exception ex)
        {
            ReadoutText.Text = $"Drive enumeration failed:\n\n{ex.Message}";
            StatusText.Text  = "error — see above";
        }
    }

    private static string SummarizeDrive(OpticalDrive d)
    {
        var sb = new StringBuilder();
        var letter = d.PrimaryMount ?? "(no drive letter)";
        sb.AppendLine($"{letter}   {d.VendorId} {d.ProductId}  (firmware {d.Revision})");

        var loaded = d.CurrentProfiles.Where(p => p.Code != 0).Select(p => p.Name).ToList();
        if (loaded.Count == 0)
        {
            sb.AppendLine("   Loaded:  (no disc)");
            sb.Append("   → Insert a disc, then Refresh.");
            return sb.ToString();
        }
        sb.AppendLine($"   Loaded:  {string.Join(", ", loaded)}");

        // Ask the drive (via SCSI) whether the disc is blank / finalized.
        string state  = "disc present";
        string action = "Use CD Info to inspect this disc.";
        if (d.PrimaryMount is { Length: >= 1 } m && char.IsLetter(m[0]))
        {
            try
            {
                using var dev = SptiDevice.OpenDriveLetter(m[0]);
                var info = dev.ReadDiscInformation();
                switch (info.Status)
                {
                    case SptiDevice.DiscStatus.Empty:
                        state  = "blank";
                        action = d.WritableProfiles.Any()
                            ? "Ready to burn — Burn Audio CD, or Burn Blu-ray / DVD."
                            : "Blank disc (this drive can't write).";
                        break;
                    case SptiDevice.DiscStatus.Finalized:
                        state  = "finalized" + (info.Erasable ? ", erasable" : "");
                        action = "Finalized disc — ready to read. CD Info shows the full TOC.";
                        break;
                    default:
                        state  = info.Status + (info.Erasable ? ", erasable" : "");
                        action = info.Erasable
                            ? "Rewritable disc with data — can be erased and reused."
                            : "Disc has data — CD Info shows what's on it.";
                        break;
                }
            }
            catch
            {
                // SCSI pass-through unavailable (drive busy, not elevated, ...)
                // — fall back to the generic state/action.
            }
        }
        sb.AppendLine($"   State:   {state}");
        sb.Append($"   → {action}");
        return sb.ToString();
    }
}
