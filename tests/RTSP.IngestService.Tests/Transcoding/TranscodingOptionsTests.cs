// Tests of the Transcoding configuration section and of the FFmpeg argument builder: defaults, range and
// cross-property validation, strict binding of unknown keys and undefined codec names, path resolution without the
// PATH, the startup capability check failing with a validation error for a missing or unusable executable, disabled
// transcoding skipping the check, and argument lists that contain only fixed text and validated numbers.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="TranscodingOptions"/>, <see cref="FfmpegArguments"/> and <see cref="FfmpegCapabilityCheck"/>.</summary>
public sealed class TranscodingOptionsTests
{
    /// <summary>The minimal configuration.</summary>
    private static readonly Dictionary<string, string?> MinimalConfiguration = new() { ["Application:Name"] = "TestService" };

    /// <summary>Defaults bind and validate.</summary>
    [Fact]
    public void Defaults_AreValid()
    {
        using var provider = BuildProvider(MinimalConfiguration);
        var options = provider.GetRequiredService<IOptions<TranscodingOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Null(options.FfmpegPath);
        Assert.Equal(16, options.MaxConcurrentSessions);
        Assert.Equal(TranscodingOverloadPolicy.DropUntilKeyframe, options.OverloadPolicy);
        Assert.Equal(TranscodingVideoPreset.Veryfast, options.Video.Preset);
        Assert.Null(options.Video.KeyframeInterval);
        Assert.Null(options.AcceptedVideoCodecs);
        Assert.Equal(160, options.Audio.BitrateKilobits);
    }

    /// <summary>Every setting binds from configuration, including codec lists by name.</summary>
    [Fact]
    public void Values_Bind()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration)
        {
            ["Transcoding:Enabled"] = "false",
            ["Transcoding:FfmpegPath"] = "tools/ffmpeg.exe",
            ["Transcoding:MaxConcurrentSessions"] = "3",
            ["Transcoding:OverloadPolicy"] = "Backpressure",
            ["Transcoding:AcceptedVideoCodecs:0"] = "Hevc",
            ["Transcoding:AcceptedVideoCodecs:1"] = "Av1",
            ["Transcoding:AcceptedAudioCodecs:0"] = "Opus",
            ["Transcoding:Video:Preset"] = "Faster",
            ["Transcoding:Video:KeyframeInterval"] = "00:00:02",
            ["Transcoding:Video:ConstantRateFactor"] = "30",
            ["Transcoding:Audio:BitrateKilobits"] = "96",
            ["Transcoding:DrainTimeout"] = "00:00:10",
        });
        var options = provider.GetRequiredService<IOptions<TranscodingOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Equal("tools/ffmpeg.exe", options.FfmpegPath);
        Assert.Equal(3, options.MaxConcurrentSessions);
        Assert.Equal(TranscodingOverloadPolicy.Backpressure, options.OverloadPolicy);
        Assert.Equal([SourceVideoCodec.Hevc, SourceVideoCodec.Av1], options.AcceptedVideoCodecs);
        Assert.Equal([SourceAudioCodec.Opus], options.AcceptedAudioCodecs);
        Assert.Equal(TranscodingVideoPreset.Faster, options.Video.Preset);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Video.KeyframeInterval);
        Assert.Equal(30, options.Video.ConstantRateFactor);
        Assert.Equal(96, options.Audio.BitrateKilobits);
        Assert.Equal(TimeSpan.FromSeconds(10), options.DrainTimeout);
    }

    /// <summary>Out-of-range and inconsistent values fail validation.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    [Theory]
    [InlineData("MaxConcurrentSessions", "0")]
    [InlineData("MaxConcurrentSessions", "1001")]
    [InlineData("MaxInputWidth", "15")]
    [InlineData("MaxInputHeight", "8193")]
    [InlineData("MaxInputFrameRate", "241")]
    [InlineData("MaxBufferedVideoFrames", "7")]
    [InlineData("MaxBufferedAudioFrames", "15")]
    [InlineData("MaxSessionBufferMegabytes", "0")]
    [InlineData("MaxSessionBufferMegabytes", "513")]
    [InlineData("MaxTotalBufferMegabytes", "0")]
    [InlineData("DrainTimeout", "00:00:00.5")]
    [InlineData("DrainTimeout", "00:01:01")]
    [InlineData("ThreadsPerSession", "65")]
    [InlineData("FfmpegPath", " ffmpeg")]
    [InlineData("Video:ConstantRateFactor", "52")]
    [InlineData("Video:MaxBitrateKilobits", "99")]
    [InlineData("Video:KeyframeInterval", "00:00:00.5")]
    [InlineData("Video:KeyframeInterval", "00:01:01")]
    [InlineData("Audio:BitrateKilobits", "31")]
    [InlineData("Audio:BitrateKilobits", "513")]
    [InlineData("AcceptedVideoCodecs:0", "Vp8")]
    [InlineData("AcceptedAudioCodecs:0", "Pcm")]
    [InlineData("OverloadPolicy", "Passthrough")]
    [InlineData("HardwareAcceleration", "Nvenc")]
    [InlineData("Video:Encoder", "Copy")]
    [InlineData("Video:Arguments", "-c copy")]
    [InlineData("Arguments", "-c copy")]
    public void InvalidValue_FailsValidation(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration) { ["Transcoding:" + key] = value });
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TranscodingOptions>>().Value);
        Assert.Equal(typeof(TranscodingOptions), ex.OptionsType);
    }

    /// <summary>Duplicate codec entries fail validation.</summary>
    [Fact]
    public void DuplicateCodec_FailsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration)
        {
            ["Transcoding:AcceptedVideoCodecs:0"] = "H264",
            ["Transcoding:AcceptedVideoCodecs:1"] = "H264",
        });
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TranscodingOptions>>().Value);
    }

    /// <summary>The default executable is resolved beside the service; a relative path from the service directory.</summary>
    [Fact]
    public void FfmpegPath_ResolvesAgainstTheServiceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svc");
        var resolved = new TranscodingOptions().ResolveFfmpegPath(root);
        Assert.Equal(Path.Combine(root, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), resolved);
        Assert.Equal(Path.Combine(root, "bin", "x"), new TranscodingOptions { FfmpegPath = Path.Combine("bin", "x") }.ResolveFfmpegPath(root));
    }

    /// <summary>A missing or unusable executable fails startup with a validation error naming the section.</summary>
    /// <param name="kind">missing or not-executable.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("not-executable")]
    public async Task UnusableFfmpeg_FailsStartup(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-ffmpeg-");
        try
        {
            var path = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (kind == "not-executable")
            {
                await File.WriteAllTextAsync(path, "not a program", TestContext.Current.CancellationToken);
            }

            using var host = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: new Dictionary<string, string?>
            {
                ["Transcoding:Enabled"] = "true",
                ["Transcoding:FfmpegPath"] = path,
            }).Build();
            var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Equal(TranscodingOptions.SectionName, ex.OptionsName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Disabled transcoding starts without an executable and logs that publishes are rejected.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Disabled_SkipsTheCapabilityCheck()
    {
        var logs = new FakeLogCollector();
        using var host = TestHosts.CreateBuilder(logs, overrides: new Dictionary<string, string?>
        {
            ["Transcoding:Enabled"] = "false",
            ["Transcoding:FfmpegPath"] = Path.Combine(Path.GetTempPath(), "does-not-exist", "ffmpeg"),
        }).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains(logs.GetSnapshot(), static r => r.Id.Id == 10016);
    }

    /// <summary>The installed FFmpeg passes the capability check.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task InstalledFfmpeg_PassesTheCapabilityCheck()
    {
        var path = FfmpegTool.Require(probe: false);
        var version = await FfmpegCapabilityCheck.VerifyAsync(path, TestContext.Current.CancellationToken);
        Assert.StartsWith("ffmpeg version", version, StringComparison.Ordinal);
    }

    /// <summary>The argument list selects the source decoders and the fixed canonical encoders.</summary>
    [Fact]
    public void Arguments_AreCanonical()
    {
        var configuration = Configuration(SourceVideoCodec.Hevc, SourceAudioCodec.Opus, null);
        var args = FfmpegArguments.Build(configuration, new TranscodingOptions(), TimeSpan.FromSeconds(2));
        AssertPair(args, "-c:v", "hevc");
        AssertPair(args, "-c:a", "opus");
        AssertPair(args, "-profile:v", "high");
        AssertPair(args, "-pix_fmt", "yuv420p");
        AssertPair(args, "-preset", "veryfast");
        AssertPair(args, "-colorspace", "bt709");
        AssertPair(args, "-color_range", "tv");
        AssertPair(args, "-profile:a", "aac_low");
        AssertPair(args, "-ar", "48000");
        AssertPair(args, "-ac", "2");
        AssertPair(args, "-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t-prev_forced_t,2))");
        Assert.Equal("pipe:1", args[^1]);
        Assert.DoesNotContain(args, static a => a.Contains("in_transfer", StringComparison.Ordinal));
        Assert.DoesNotContain("-threads", args);
    }

    /// <summary>Declared HDR states the input colour for tone mapping; tracks absent from the source are not mapped.</summary>
    /// <param name="transfer">The transfer characteristics.</param>
    /// <param name="name">The FFmpeg transfer name.</param>
    [Theory]
    [InlineData(16, "smpte2084")]
    [InlineData(18, "arib-std-b67")]
    public void Arguments_ToneMapDeclaredHdr(int transfer, string name)
    {
        var configuration = Configuration(SourceVideoCodec.Av1, null, new VideoColorDescription(9, transfer, 9));
        var args = FfmpegArguments.Build(configuration, new TranscodingOptions { ThreadsPerSession = 2 }, TimeSpan.FromSeconds(1.5));
        Assert.Contains(args, a => a.Contains("in_transfer=" + name, StringComparison.Ordinal) && a.Contains("intent=perceptual", StringComparison.Ordinal));
        AssertPair(args, "-c:v", "libdav1d");
        AssertPair(args, "-threads", "2");
        Assert.DoesNotContain("0:a:0", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.Contains(args, static a => a.EndsWith("gte(t-prev_forced_t,1.5))", StringComparison.Ordinal));
    }

    /// <summary>Asserts that <paramref name="name"/> is followed by <paramref name="value"/>.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="name">The option.</param>
    /// <param name="value">The value.</param>
    private static void AssertPair(List<string> args, string name, string value)
    {
        var indexes = args.Select((a, i) => (a, i)).Where(x => x.a == name).Select(static x => x.i).ToList();
        Assert.Contains(indexes, i => i + 1 < args.Count && args[i + 1] == value);
    }

    /// <summary>Builds a stream configuration.</summary>
    /// <param name="video">The video codec.</param>
    /// <param name="audio">The audio codec.</param>
    /// <param name="color">The declared colour.</param>
    /// <returns>The configuration.</returns>
    private static SourceStreamConfiguration Configuration(SourceVideoCodec? video, SourceAudioCodec? audio, VideoColorDescription? color) => new(
        video is { } v ? new SourceVideoConfiguration(v, [1], 4, 64, 64, 8, v.ToString()) : null,
        audio is { } a ? new SourceAudioConfiguration(a, [1], 48000, 2, a.ToString()) : null,
        color,
        1);

    /// <summary>Builds a provider with the options registrations.</summary>
    /// <param name="values">Configuration values.</param>
    /// <returns>The provider.</returns>
    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddIngestServiceOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }
}
