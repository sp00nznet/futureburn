using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Futureburn.Core.Tools;

// Wrapper for spumux.exe — the subtitle multiplexer from the dvdauthor suite.
// spumux reads an MPEG-PS on stdin, takes an XML control file describing one
// subpicture stream, and writes the MPEG-PS with that subtitle muxed in to
// stdout:  spumux -s <n> control.xml < in.mpg > out.mpg
//
// It ships in the same bin\ folder as dvdauthor.exe (DVDStyler bundles both),
// so we locate dvdauthor first and swap the filename.

[SupportedOSPlatform("windows")]
public sealed class SpumuxRunner
{
    public string ExePath { get; }

    private SpumuxRunner(string exePath) => ExePath = exePath;

    /// <summary>Locate spumux.exe next to dvdauthor.exe. Null if not found.</summary>
    public static SpumuxRunner? Locate()
    {
        var dvdauthor = DvdauthorLocator.Locate();
        if (dvdauthor is null) return null;

        // dvdauthor resolved by bare name on PATH → assume spumux is on PATH too.
        var dir = Path.GetDirectoryName(dvdauthor.Path);
        if (string.IsNullOrEmpty(dir)) return new SpumuxRunner("spumux");

        var spumux = Path.Combine(dir, "spumux.exe");
        return File.Exists(spumux) ? new SpumuxRunner(spumux) : null;
    }

    public static SpumuxRunner LocateOrThrow() =>
        Locate() ?? throw new InvalidOperationException(
            "spumux not found. It ships with the dvdauthor suite — install DVDStyler " +
            "(winget install AlexThuering.DVDStyler) to get dvdauthor.exe + spumux.exe.");

    /// <summary>
    /// Build a spumux control file for one text subtitle (SRT). spumux's
    /// &lt;textsub&gt; renderer rasterizes the text using freetype/fontconfig;
    /// we hand it a Windows system font by absolute path.
    /// </summary>
    public static string BuildTextSubtitleXml(string srtPath, bool isPal)
    {
        int    width  = 720;
        int    height = isPal ? 576 : 480;
        string fps    = isPal ? "25" : "30000/1001";
        string font   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");

        var sb = new StringBuilder();
        sb.AppendLine("<subpictures>");
        sb.AppendLine("  <stream>");
        sb.AppendLine($"    <textsub filename=\"{XmlEscape(srtPath)}\" characterset=\"UTF-8\"");
        sb.AppendLine($"             font=\"{XmlEscape(font)}\" fontsize=\"28\"");
        sb.AppendLine("             horizontal-alignment=\"center\" vertical-alignment=\"bottom\"");
        sb.AppendLine("             left-margin=\"60\" right-margin=\"60\" bottom-margin=\"30\"");
        sb.AppendLine($"             subtitle-fps=\"{fps}\" movie-fps=\"{fps}\"");
        sb.AppendLine($"             movie-width=\"{width}\" movie-height=\"{height}\"/>");
        sb.AppendLine("  </stream>");
        sb.AppendLine("</subpictures>");
        return sb.ToString();
    }

    /// <summary>
    /// Multiplex one text-subtitle stream into an MPEG-PS. <paramref name="streamIndex"/>
    /// is the subtitle stream number (0..31) the XML's stream will become.
    /// </summary>
    public void Mux(string inputPs, string outputPs, string xmlControlFile,
                    int streamIndex, bool isPal, Action<string>? onLog = null)
        => RunSpumux(new[] { "-s", streamIndex.ToString(), xmlControlFile },
                     inputPs, outputPs, isPal, onLog);

    /// <summary>
    /// Multiplex a DVD menu's button subpictures into the menu MPEG-PS.
    /// Uses spumux's <c>-m dvd</c> mode; the XML describes the button rectangles
    /// and the highlight/select overlay images.
    /// </summary>
    public void MuxMenu(string inputPs, string outputPs, string xmlControlFile,
                        bool isPal, Action<string>? onLog = null)
        => RunSpumux(new[] { "-m", "dvd", xmlControlFile },
                     inputPs, outputPs, isPal, onLog);

    // spumux reads the MPEG-PS from stdin and writes the result to stdout.
    private void RunSpumux(IReadOnlyList<string> args, string inputPs, string outputPs,
                           bool isPal, Action<string>? onLog)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = ExePath,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // spumux refuses to run without knowing the video system — it reads
        // this env var even when the XML also carries the dimensions.
        psi.EnvironmentVariables["VIDEO_FORMAT"] = isPal ? "PAL" : "NTSC";

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Couldn't start {ExePath}");

        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
        p.BeginErrorReadLine();

        // Feed the input PS into stdin while draining stdout into the output
        // file concurrently, or the pipes deadlock on a large file.
        using (var inFs  = File.OpenRead(inputPs))
        using (var outFs = File.Create(outputPs))
        {
            var drainStdout = p.StandardOutput.BaseStream.CopyToAsync(outFs);
            inFs.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
            drainStdout.GetAwaiter().GetResult();
        }
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"spumux exited with code {p.ExitCode}.");
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
