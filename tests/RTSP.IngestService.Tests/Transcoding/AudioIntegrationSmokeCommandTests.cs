using RTSP.IngestService.Hosting;
using RTSP.IngestService.Tests.Media.Codecs.Aac;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="AudioIntegrationSmokeCommand"/>.</summary>
public sealed class AudioIntegrationSmokeCommandTests
{
    /// <summary>Each fixture publishes through the managed stage and multiplexer and passes every check.</summary>
    /// <param name="fixture">The ADTS fixture.</param>
    /// <param name="rate">Its sample rate.</param>
    /// <param name="channels">Its channel count.</param>
    [Theory]
    [InlineData("aac-lc-48k-stereo.aac", 48000, 2)]
    [InlineData("aac-lc-44k-mono.aac", 44100, 1)]
    [InlineData("aac-lc-48k-5.1.aac", 48000, 6)]
    [InlineData("aac-lc-8k-mono.aac", 8000, 1)]
    public void Fixture_PassesEveryCheck(string fixture, int rate, int channels)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var input = Path.Combine(AacTestSupport.FixtureDirectory, fixture);

        Assert.True(AudioIntegrationSmokeCommand.TryRun([AudioIntegrationSmokeCommand.CommandName, input], stdout, stderr, out var exitCode));

        Assert.True(exitCode == ExitCodes.Success, stderr.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains($"Input: AAC-LC {rate} Hz, {channels} channel(s)", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Output: AAC-LC 48000 Hz, 2 channels", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Digest: delivered audio ", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The same input gives the same delivered audio on every run.</summary>
    [Fact]
    public void Output_IsDeterministic()
    {
        var input = Path.Combine(AacTestSupport.FixtureDirectory, "aac-lc-44k-mono.aac");
        Assert.Equal(Run(input), Run(input));

        static string Run(string input)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.True(AudioIntegrationSmokeCommand.TryRun([AudioIntegrationSmokeCommand.CommandName, input], stdout, stderr, out var exitCode));
            Assert.Equal(ExitCodes.Success, exitCode);
            return stdout.ToString();
        }
    }

    /// <summary>Other commands are not run; bad usage, missing and undecodable inputs fail.</summary>
    [Fact]
    public void BadInput_Fails()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var directory = Directory.CreateTempSubdirectory("rtsp-audio-smoke-");
        try
        {
            Assert.False(AudioIntegrationSmokeCommand.TryRun([CodecSmokeCommand.CommandName], stdout, stderr, out _));
            Assert.True(AudioIntegrationSmokeCommand.TryRun([AudioIntegrationSmokeCommand.CommandName], stdout, stderr, out var exitCode));
            Assert.Equal(ExitCodes.UsageError, exitCode);
            Assert.True(AudioIntegrationSmokeCommand.TryRun([AudioIntegrationSmokeCommand.CommandName, Path.Combine(directory.FullName, "missing.aac")], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.UsageError, exitCode);
            var garbage = Path.Combine(directory.FullName, "garbage.aac");
            File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.True(AudioIntegrationSmokeCommand.TryRun([AudioIntegrationSmokeCommand.CommandName, garbage], stdout, stderr, out exitCode));
            Assert.Equal(ExitCodes.Failure, exitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
