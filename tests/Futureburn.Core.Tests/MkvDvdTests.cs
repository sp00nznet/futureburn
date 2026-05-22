using System.Runtime.Versioning;
using Futureburn.Core.Authoring;
using Futureburn.Core.Ffmpeg;
using Futureburn.Core.Tools;

namespace Futureburn.Core.Tests;

// Covers the pure logic of the MKV → DVD-Video pipeline: chapter-time
// formatting, dvdauthor / spumux XML generation, and language-code mapping.
// The actual transcode / author / burn steps shell out to external tools and
// are exercised end-to-end manually.
[SupportedOSPlatform("windows")]
public class MkvDvdTests
{
    // ---- IsoLanguage -------------------------------------------------------

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]   // ISO 639-2/B vs /T
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("jpn", "ja")]
    [InlineData("en",  "en")]   // already 2-letter
    [InlineData("FR",  "fr")]   // case-insensitive, lowercased
    public void IsoLanguage_MapsKnownCodes(string input, string expected)
        => Assert.Equal(expected, IsoLanguage.To2Letter(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("und")]         // undetermined
    [InlineData("xyz")]         // not a real code
    public void IsoLanguage_UnknownFallsBackToEnglish(string? input)
        => Assert.Equal("en", IsoLanguage.To2Letter(input));

    [Fact]
    public void IsoLanguage_HonorsCustomFallback()
        => Assert.Equal("de", IsoLanguage.To2Letter("und", fallback: "de"));

    // ---- DvdauthorRunner.FormatChapters -----------------------------------

    [Fact]
    public void FormatChapters_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", DvdauthorRunner.FormatChapters(null));
        Assert.Equal("", DvdauthorRunner.FormatChapters(Array.Empty<TimeSpan>()));
    }

    [Fact]
    public void FormatChapters_FormatsAsHmsMillis()
    {
        var chapters = new[]
        {
            TimeSpan.Zero,
            new TimeSpan(0, 10, 0),
            new TimeSpan(1, 2, 3),
        };
        Assert.Equal("0:00:00.000,0:10:00.000,1:02:03.000",
                     DvdauthorRunner.FormatChapters(chapters));
    }

    [Fact]
    public void FormatChapters_InsertsLeadingZeroWhenFirstIsNotZero()
    {
        // A disc whose first MKV chapter starts a few seconds in still needs a
        // chapter 1 at the title start.
        var result = DvdauthorRunner.FormatChapters(new[] { new TimeSpan(0, 5, 0) });
        Assert.StartsWith("0:00:00.000,", result);
        Assert.EndsWith("0:05:00.000", result);
    }

    [Fact]
    public void FormatChapters_CapsAt99()
    {
        var many = Enumerable.Range(1, 200).Select(i => TimeSpan.FromSeconds(i)).ToList();
        var result = DvdauthorRunner.FormatChapters(many);
        Assert.Equal(99, result.Split(',').Length);
    }

    [Fact]
    public void FormatChapters_SortsOutOfOrderInput()
    {
        var result = DvdauthorRunner.FormatChapters(new[]
        {
            new TimeSpan(0, 20, 0),
            new TimeSpan(0, 5, 0),
            new TimeSpan(0, 10, 0),
        });
        Assert.Equal("0:00:00.000,0:05:00.000,0:10:00.000,0:20:00.000", result);
    }

    // ---- DvdauthorRunner.BuildXml -----------------------------------------

    private static DvdauthorRunner.DvdTitleSpec Spec(
        bool pal = false, string aspect = "16:9",
        TimeSpan[]? chapters = null, string[]? audio = null, string[]? subs = null)
        => new("C:\\tmp\\title.mpg", pal, aspect, chapters, audio, subs);

    [Fact]
    public void BuildXml_NtscVsPal()
    {
        Assert.Contains("format=\"ntsc\"", DvdauthorRunner.BuildXml(Spec(pal: false)));
        Assert.Contains("format=\"pal\"",  DvdauthorRunner.BuildXml(Spec(pal: true)));
    }

    [Fact]
    public void BuildXml_CarriesAspectRatio()
        => Assert.Contains("aspect=\"16:9\"", DvdauthorRunner.BuildXml(Spec(aspect: "16:9")));

    [Fact]
    public void BuildXml_EmitsAudioAndSubpictureStreamsInOrder()
    {
        var xml = DvdauthorRunner.BuildXml(Spec(
            audio: new[] { "en", "fr" },
            subs:  new[] { "en" }));
        Assert.Contains("<audio lang=\"en\"/>", xml);
        Assert.Contains("<audio lang=\"fr\"/>", xml);
        Assert.Contains("<subpicture lang=\"en\"/>", xml);
        // English audio is declared before French (stream order matters).
        Assert.True(xml.IndexOf("lang=\"en\"") < xml.IndexOf("lang=\"fr\""));
    }

    [Fact]
    public void BuildXml_IncludesChaptersAttributeWhenPresent()
    {
        var xml = DvdauthorRunner.BuildXml(Spec(
            chapters: new[] { TimeSpan.Zero, new TimeSpan(0, 10, 0) }));
        Assert.Contains("chapters=\"0:00:00.000,0:10:00.000\"", xml);
    }

    [Fact]
    public void BuildXml_OmitsChaptersAttributeWhenNone()
        => Assert.DoesNotContain("chapters=", DvdauthorRunner.BuildXml(Spec()));

    [Fact]
    public void BuildXml_HasVmgmAutoplayHandoff()
    {
        // The vmgm PGC jumps straight to the title so the disc auto-plays.
        var xml = DvdauthorRunner.BuildXml(Spec());
        Assert.Contains("<vmgm>", xml);
        Assert.Contains("jump title 1;", xml);
    }

    // ---- SpumuxRunner.BuildTextSubtitleXml --------------------------------

    [Fact]
    public void SpumuxXml_ReferencesTheSrtFile()
    {
        var xml = SpumuxRunner.BuildTextSubtitleXml("C:\\tmp\\sub0.srt", isPal: false);
        Assert.Contains("filename=\"C:\\tmp\\sub0.srt\"", xml);
        Assert.Contains("<textsub", xml);
    }

    [Fact]
    public void SpumuxXml_UsesCorrectMovieSizePerVideoSystem()
    {
        Assert.Contains("movie-height=\"480\"", SpumuxRunner.BuildTextSubtitleXml("s.srt", isPal: false));
        Assert.Contains("movie-height=\"576\"", SpumuxRunner.BuildTextSubtitleXml("s.srt", isPal: true));
        Assert.Contains("movie-width=\"720\"",  SpumuxRunner.BuildTextSubtitleXml("s.srt", isPal: false));
    }

    // ---- MkvDvdPipeline helpers -------------------------------------------

    [Theory]
    [InlineData("subrip", true)]
    [InlineData("ass", true)]
    [InlineData("mov_text", true)]
    [InlineData("webvtt", true)]
    [InlineData("dvd_subtitle", false)]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("dvb_subtitle", false)]
    public void IsTextSubtitle_ClassifiesCodecs(string codec, bool expectedText)
        => Assert.Equal(expectedText, MkvDvdPipeline.IsTextSubtitle(codec));

    // GuessAspect is a storage-dimensions heuristic: ratio > 1.5 → 16:9.
    [Theory]
    [InlineData(1920, 1080, "16:9")]   // 1.78
    [InlineData(1280, 720,  "16:9")]   // 1.78
    [InlineData(1440, 1080, "4:3")]    // 1.33 storage — anamorphic DAR isn't inspected
    [InlineData(720,  480,  "4:3")]    // exactly 1.5 → not wide
    [InlineData(640,  480,  "4:3")]
    [InlineData(0,    0,    "4:3")]    // unknown dimensions → safe default
    public void GuessAspect_PicksFromDimensions(int w, int h, string expected)
    {
        var video = new FfprobeRunner.StreamInfo(
            Index: 0, CodecType: "video", CodecName: "h264", CodecLongName: "",
            SampleRate: null, Channels: null, BitRate: null, BitsPerRawSample: null,
            Duration: null, Width: w, Height: h, Language: null);
        Assert.Equal(expected, MkvDvdPipeline.GuessAspect(video));
    }

    [Fact]
    public void ParseFfmpegTime_ExtractsElapsedFromProgressLine()
    {
        var t = MkvDvdPipeline.ParseFfmpegTime(
            "frame= 1242 fps=805 q=2.0 size=24832KiB time=01:02:03.50 bitrate=4912.7kbits/s");
        Assert.NotNull(t);
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 500), t!.Value);
    }

    [Fact]
    public void ParseFfmpegTime_ReturnsNullWhenNoTimeField()
        => Assert.Null(MkvDvdPipeline.ParseFfmpegTime("Press [q] to stop, [?] for help"));
}
