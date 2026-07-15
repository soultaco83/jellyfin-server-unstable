using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests
{
    public class SubtitleEncoderTests
    {
        private const int StreamCount = 8;
        private const int CueCount = 500;

        public static TheoryData<MediaSourceInfo, MediaStream, SubtitleEncoder.SubtitleInfo> GetReadableFile_Valid_TestData()
        {
            var data = new TheoryData<MediaSourceInfo, MediaStream, SubtitleEncoder.SubtitleInfo>();

            data.Add(
                new MediaSourceInfo()
                {
                    Protocol = MediaProtocol.File
                },
                new MediaStream()
                {
                    Path = "/media/sub.ass",
                    IsExternal = true
                },
                new SubtitleEncoder.SubtitleInfo()
                {
                    Path = "/media/sub.ass",
                    Protocol = MediaProtocol.File,
                    Format = "ass",
                    IsExternal = true
                });

            data.Add(
                new MediaSourceInfo()
                {
                    Protocol = MediaProtocol.File
                },
                new MediaStream()
                {
                    Path = "/media/sub.ssa",
                    IsExternal = true
                },
                new SubtitleEncoder.SubtitleInfo()
                {
                    Path = "/media/sub.ssa",
                    Protocol = MediaProtocol.File,
                    Format = "ssa",
                    IsExternal = true
                });

            data.Add(
                new MediaSourceInfo()
                {
                    Protocol = MediaProtocol.File
                },
                new MediaStream()
                {
                    Path = "/media/sub.srt",
                    IsExternal = true
                },
                new SubtitleEncoder.SubtitleInfo()
                {
                    Path = "/media/sub.srt",
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                });

            data.Add(
                new MediaSourceInfo()
                {
                    Protocol = MediaProtocol.Http
                },
                new MediaStream()
                {
                    Path = "/media/sub.ass",
                    IsExternal = true
                },
                new SubtitleEncoder.SubtitleInfo()
                {
                    Path = "/media/sub.ass",
                    Protocol = MediaProtocol.File,
                    Format = "ass",
                    IsExternal = true
                });

            return data;
        }

        [Theory]
        [MemberData(nameof(GetReadableFile_Valid_TestData))]
        public async Task GetReadableFile_Valid_Success(MediaSourceInfo mediaSource, MediaStream subtitleStream, SubtitleEncoder.SubtitleInfo subtitleInfo)
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            var subtitleEncoder = fixture.Create<SubtitleEncoder>();
            var result = await subtitleEncoder.GetReadableFile(mediaSource, subtitleStream, CancellationToken.None);
            Assert.Equal(subtitleInfo.Path, result.Path);
            Assert.Equal(subtitleInfo.Protocol, result.Protocol);
            Assert.Equal(subtitleInfo.Format, result.Format);
            Assert.Equal(subtitleInfo.IsExternal, result.IsExternal);
        }

        [Fact]
        public void ConvertSubtitles_SequentialCalls_AreDeterministic()
        {
            using var encoder = CreateEncoder();
            var sources = GenerateSources();

            var first = ConvertAllSequential(encoder, sources);
            var second = ConvertAllSequential(encoder, sources);

            for (var i = 0; i < StreamCount; i++)
            {
                Assert.Contains($"S{i}C{CueCount - 1}", first[i], StringComparison.Ordinal);
                Assert.Equal(first[i], second[i]);
            }
        }

        [Fact]
        public async Task ConvertSubtitles_ConcurrentCalls_MatchSequentialBaseline()
        {
            const int Iterations = 10;

            using var encoder = CreateEncoder();
            var sources = GenerateSources();
            var baseline = ConvertAllSequential(encoder, sources);

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var results = await Task.WhenAll(Enumerable.Range(0, StreamCount)
                    .Select(i => Task.Run(() => Convert(encoder, sources[i], i)))
                    .ToArray());

                for (var i = 0; i < StreamCount; i++)
                {
                    Assert.True(
                        string.Equals(baseline[i], results[i], StringComparison.Ordinal),
                        $"Iteration {iteration}: stream {i} returned corrupted content ({results[i].Length} chars vs {baseline[i].Length} baseline)");
                }
            }
        }

        private static SubtitleEncoder CreateEncoder()
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            fixture.Inject<ISubtitleParser>(new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance));
            return fixture.Create<SubtitleEncoder>();
        }

        private static byte[][] GenerateSources()
        {
            return Enumerable.Range(0, StreamCount)
                .Select(i => Encoding.UTF8.GetBytes(GenerateSrt(i, CueCount)))
                .ToArray();
        }

        private static string Convert(SubtitleEncoder encoder, byte[] source, int streamIndex)
        {
            using var input = new MemoryStream(source);
            var info = new SubtitleEncoder.SubtitleInfo { Path = $"track{streamIndex}.srt", Format = "srt" };
            using var output = encoder.ConvertSubtitles(input, info, "vtt", 0, 0, false);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private static string[] ConvertAllSequential(SubtitleEncoder encoder, byte[][] sources)
        {
            return sources.Select((source, i) => Convert(encoder, source, i)).ToArray();
        }

        private static string GenerateSrt(int streamIndex, int cueCount)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < cueCount; i++)
            {
                var start = TimeSpan.FromSeconds(i * 4);
                var end = start + TimeSpan.FromSeconds(2);
                builder.Append(i + 1).AppendLine()
                    .Append(start.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture))
                    .Append(" --> ")
                    .AppendLine(end.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture))
                    .Append('S').Append(streamIndex).Append('C').Append(i).AppendLine()
                    .AppendLine();
            }

            return builder.ToString();
        }
    }
}
