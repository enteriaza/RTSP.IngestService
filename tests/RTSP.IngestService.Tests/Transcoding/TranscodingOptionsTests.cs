// Tests of the Transcoding configuration section: defaults, range and cross-property validation, strict binding of
// unknown keys (including every removed FFmpeg-era key) and undefined codec names, accepted-codec lists limited to
// what the managed pipeline transcodes, the managed video settings derived from the options, and a host that starts,
// reports the managed pipeline and stops with no FFmpeg configured or registered.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="TranscodingOptions"/> and <see cref="ManagedVideoSettings"/>.</summary>
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
        Assert.Equal(16, options.MaxConcurrentSessions);
        Assert.Equal(TranscodingOverloadPolicy.DropUntilKeyframe, options.OverloadPolicy);
        Assert.Equal(22, options.Video.Quantizer);
        Assert.Equal(8000, options.Video.MaxBitrateKilobits);
        Assert.Null(options.Video.KeyframeInterval);
        Assert.Null(options.AcceptedVideoCodecs);
        Assert.Null(options.AcceptedAudioCodecs);
        Assert.Equal(160, options.Audio.BitrateKilobits);
    }

    /// <summary>Every setting binds from configuration, including codec lists by name.</summary>
    [Fact]
    public void Values_Bind()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>(MinimalConfiguration)
        {
            ["Transcoding:Enabled"] = "false",
            ["Transcoding:MaxConcurrentSessions"] = "3",
            ["Transcoding:OverloadPolicy"] = "Backpressure",
            ["Transcoding:AcceptedVideoCodecs:0"] = "H264",
            ["Transcoding:AcceptedAudioCodecs:0"] = "Aac",
            ["Transcoding:Video:KeyframeInterval"] = "00:00:02",
            ["Transcoding:Video:Quantizer"] = "30",
            ["Transcoding:Video:MaxBitrateKilobits"] = "4000",
            ["Transcoding:Audio:BitrateKilobits"] = "96",
            ["Transcoding:DrainTimeout"] = "00:00:10",
        });
        var options = provider.GetRequiredService<IOptions<TranscodingOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Equal(3, options.MaxConcurrentSessions);
        Assert.Equal(TranscodingOverloadPolicy.Backpressure, options.OverloadPolicy);
        Assert.Equal([SourceVideoCodec.H264], options.AcceptedVideoCodecs);
        Assert.Equal([SourceAudioCodec.Aac], options.AcceptedAudioCodecs);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Video.KeyframeInterval);
        Assert.Equal(30, options.Video.Quantizer);
        Assert.Equal(4000, options.Video.MaxBitrateKilobits);
        Assert.Equal(96, options.Audio.BitrateKilobits);
        Assert.Equal(TimeSpan.FromSeconds(10), options.DrainTimeout);
    }

    /// <summary>
    /// Out-of-range and inconsistent values fail validation, as do the keys of the removed FFmpeg pipeline (strict
    /// binding rejects them as unknown) and codecs the managed pipeline does not transcode.
    /// </summary>
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
    [InlineData("Video:Quantizer", "-1")]
    [InlineData("Video:Quantizer", "52")]
    [InlineData("Video:MaxBitrateKilobits", "99")]
    [InlineData("Video:KeyframeInterval", "00:00:00.5")]
    [InlineData("Video:KeyframeInterval", "00:01:01")]
    [InlineData("Audio:BitrateKilobits", "31")]
    [InlineData("Audio:BitrateKilobits", "513")]
    [InlineData("AcceptedVideoCodecs:0", "Vp8")]
    [InlineData("AcceptedVideoCodecs:0", "Hevc")]
    [InlineData("AcceptedVideoCodecs:0", "Av1")]
    [InlineData("AcceptedAudioCodecs:0", "Pcm")]
    [InlineData("AcceptedAudioCodecs:0", "Opus")]
    [InlineData("OverloadPolicy", "Passthrough")]
    [InlineData("HardwareAcceleration", "Nvenc")]
    [InlineData("FfmpegPath", "ffmpeg")]
    [InlineData("ThreadsPerSession", "2")]
    [InlineData("Video:Encoder", "Libx264")]
    [InlineData("Video:Preset", "Veryfast")]
    [InlineData("Video:ConstantRateFactor", "21")]
    [InlineData("Audio:Engine", "Ffmpeg")]
    [InlineData("Audio:Engine", "ManagedAacLc")]
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

    /// <summary>
    /// The managed video settings take the tighter of the input limits and the managed codecs' limits (4096 per side,
    /// 36864 macroblocks), the media sample limits for the decoder, and the encoder settings from Transcoding:Video.
    /// </summary>
    [Fact]
    public void ManagedVideoSettings_AreDerivedFromTheOptions()
    {
        var defaults = ManagedVideoSettings.FromOptions(new TranscodingOptions(), new MediaOptions(), TimeSpan.FromSeconds(2));
        Assert.Equal((3840, 3840, 36864), (defaults.Decoder.MaxWidth, defaults.Decoder.MaxHeight, defaults.Decoder.MaxMacroblocks));
        Assert.Equal(new MediaOptions().MaxSampleSize, defaults.Decoder.MaxAccessUnitBytes);
        Assert.Equal(new MediaOptions().MaxNalUnitsPerSample, defaults.Decoder.MaxNalUnitsPerAccessUnit);
        Assert.Equal((22, 8000, 60, TimeSpan.FromSeconds(2)), (defaults.Quantizer, defaults.MaxBitrateKilobits, defaults.MaxFrameRate, defaults.KeyframeInterval));

        var options = new TranscodingOptions { MaxInputWidth = 8192, MaxInputHeight = 720, MaxInputFrameRate = 30 };
        options.Video.Quantizer = 30;
        var custom = ManagedVideoSettings.FromOptions(options, new MediaOptions { MaxSampleSize = 1 << 20 }, TimeSpan.FromSeconds(4));
        Assert.Equal((4096, 720, 256 * 45), (custom.Decoder.MaxWidth, custom.Decoder.MaxHeight, custom.Decoder.MaxMacroblocks));
        Assert.Equal((30, 30, 1 << 20), (custom.Quantizer, custom.MaxFrameRate, custom.MaxSampleSize));
    }

    /// <summary>
    /// A host with transcoding enabled starts and stops with no FFmpeg configured, reports the managed pipeline, and
    /// registers no FFmpeg service.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task EnabledHost_StartsWithoutFfmpeg()
    {
        var logs = new FakeLogCollector();
        using var host = TestHosts.CreateBuilder(logs).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains(logs.GetSnapshot(), static r => r.Id.Id == 10015 && r.Message.Contains("managed pipeline", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), static s => s.GetType().Name.Contains("Ffmpeg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(host.Services.GetServices<IHostedService>(), static s => s is TranscodingLifecycleService);
    }

    /// <summary>The production assembly has no FFmpeg type: no process boundary, argument builder or capability check remains.</summary>
    [Fact]
    public void ProductionAssembly_HasNoFfmpegTypes()
    {
        var types = typeof(IngestServiceHost).Assembly.GetTypes();
        Assert.DoesNotContain(types, static t => t.Name.Contains("Ffmpeg", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("TranscoderProcess", StringComparison.Ordinal));
    }

    /// <summary>Disabled transcoding starts and logs that publishes are rejected.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Disabled_LogsThatPublishesAreRejected()
    {
        var logs = new FakeLogCollector();
        using var host = TestHosts.CreateBuilder(logs, overrides: new Dictionary<string, string?> { ["Transcoding:Enabled"] = "false" }).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains(logs.GetSnapshot(), static r => r.Id.Id == 10016);
        Assert.DoesNotContain(logs.GetSnapshot(), static r => r.Id.Id == 10015);
    }

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
