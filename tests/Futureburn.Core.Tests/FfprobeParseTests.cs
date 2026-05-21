using Futureburn.Core.Ffmpeg;

namespace Futureburn.Core.Tests;

public class FfprobeParseTests
{
    [Fact]
    public void Parse_TypicalAacM4a_ExtractsKeyFields()
    {
        // Trimmed sample of real ffprobe output for an AAC-in-M4A file.
        var json = """
        {
          "streams": [
            {
              "index": 0,
              "codec_name": "aac",
              "codec_long_name": "AAC (Advanced Audio Coding)",
              "codec_type": "audio",
              "sample_rate": "44100",
              "channels": 2,
              "bit_rate": "128000",
              "duration": "570.123000",
              "tags": { "language": "und" }
            }
          ],
          "format": {
            "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
            "format_long_name": "QuickTime / MOV",
            "duration": "570.123000",
            "size": "9226582",
            "bit_rate": "129500",
            "tags": {
              "major_brand": "dash",
              "creation_time": "2020-03-10T17:30:29.000000Z"
            }
          }
        }
        """;

        var p = FfprobeRunner.Parse(json);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", p.Format.FormatName);
        Assert.Equal("QuickTime / MOV", p.Format.FormatLongName);
        Assert.Equal(TimeSpan.FromSeconds(570.123), p.Format.Duration);
        Assert.Equal(9_226_582L, p.Format.Size);
        Assert.Equal(129_500L, p.Format.BitRate);
        Assert.Equal("dash", p.Format.Tags["major_brand"]);

        Assert.Single(p.Streams);
        var s = p.Streams[0];
        Assert.Equal("audio", s.CodecType);
        Assert.Equal("aac", s.CodecName);
        Assert.Equal("AAC (Advanced Audio Coding)", s.CodecLongName);
        Assert.Equal(44100, s.SampleRate);
        Assert.Equal(2, s.Channels);
        Assert.Equal(128_000L, s.BitRate);
        Assert.Equal("und", s.Language);
    }

    [Fact]
    public void Parse_VideoStream_ExtractsResolution()
    {
        var json = """
        {
          "streams": [
            {
              "index": 0,
              "codec_name": "h264",
              "codec_long_name": "H.264",
              "codec_type": "video",
              "width": 1920,
              "height": 1080
            },
            {
              "index": 1,
              "codec_name": "ac3",
              "codec_long_name": "ATSC A/52A (AC-3)",
              "codec_type": "audio",
              "sample_rate": "48000",
              "channels": 6
            }
          ],
          "format": {
            "format_name": "matroska,webm",
            "format_long_name": "Matroska / WebM"
          }
        }
        """;

        var p = FfprobeRunner.Parse(json);
        Assert.Equal(2, p.Streams.Count);
        var v = p.Streams[0];
        Assert.Equal("video", v.CodecType);
        Assert.Equal(1920, v.Width);
        Assert.Equal(1080, v.Height);
        var a = p.Streams[1];
        Assert.Equal("audio", a.CodecType);
        Assert.Equal(6, a.Channels);
    }

    [Fact]
    public void Parse_Chapters_ExtractsStartEndAndTitle()
    {
        var json = """
        {
          "format": { "format_name": "matroska,webm", "format_long_name": "Matroska" },
          "streams": [],
          "chapters": [
            {
              "id": 1, "time_base": "1/1000",
              "start": 0, "start_time": "0.000000",
              "end": 600000, "end_time": "600.000000",
              "tags": { "title": "Opening" }
            },
            {
              "id": 2, "time_base": "1/1000",
              "start": 600000, "start_time": "600.000000",
              "end": 1200000, "end_time": "1200.000000",
              "tags": { "title": "The Middle Bit" }
            }
          ]
        }
        """;

        var p = FfprobeRunner.Parse(json);
        Assert.Equal(2, p.Chapters.Count);
        Assert.Equal(TimeSpan.FromSeconds(0),   p.Chapters[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(600), p.Chapters[0].End);
        Assert.Equal("Opening", p.Chapters[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(600), p.Chapters[1].Start);
        Assert.Equal("The Middle Bit", p.Chapters[1].Title);
        Assert.Equal(TimeSpan.FromSeconds(600), p.Chapters[1].Duration);
    }

    [Fact]
    public void Parse_StreamClassifiers_AndLanguageTitle()
    {
        var json = """
        {
          "format": { "format_name": "matroska,webm", "format_long_name": "Matroska" },
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "codec_long_name": "H.264" },
            { "index": 1, "codec_type": "audio", "codec_name": "aac", "codec_long_name": "AAC",
              "tags": { "language": "fra", "title": "Commentary" } },
            { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "codec_long_name": "SubRip",
              "tags": { "language": "eng" } }
          ]
        }
        """;

        var p = FfprobeRunner.Parse(json);
        Assert.Single(p.VideoStreams);
        Assert.Single(p.AudioStreams);
        Assert.Single(p.SubtitleStreams);
        var a = p.AudioStreams.First();
        Assert.Equal("fra", a.Language);
        Assert.Equal("Commentary", a.Title);
        Assert.True(a.IsAudio);
        Assert.True(p.SubtitleStreams.First().IsSubtitle);
    }

    [Fact]
    public void Parse_NoChaptersKey_YieldsEmptyList()
    {
        var json = """
        { "format": { "format_name": "wav", "format_long_name": "WAV" }, "streams": [] }
        """;
        Assert.Empty(FfprobeRunner.Parse(json).Chapters);
    }

    [Fact]
    public void Parse_MissingOptionalFields_NullsThem()
    {
        var json = """
        {
          "format": { "format_name": "wav", "format_long_name": "WAV" },
          "streams": [
            {
              "index": 0,
              "codec_type": "audio",
              "codec_name": "pcm_s16le",
              "codec_long_name": "PCM signed 16-bit little-endian"
            }
          ]
        }
        """;

        var p = FfprobeRunner.Parse(json);
        Assert.Null(p.Format.Duration);
        Assert.Null(p.Format.BitRate);
        Assert.Null(p.Streams[0].SampleRate);
        Assert.Null(p.Streams[0].Channels);
    }
}
