// Tests of media processing registration in the service host: MediaPublishSessionHandler is the default publish
// session handler, the discarding media handler is the default downstream, and a host-registered consumer replaces
// it without touching the RTMP registration.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Tests for <see cref="MediaServiceCollectionExtensions"/>.</summary>
public sealed class MediaRegistrationTests
{
    /// <summary>The host registers media processing in front of HLS packaging and the in-memory segment store.</summary>
    [Fact]
    public void Host_UsesMediaProcessing()
    {
        using var host = TestHosts.CreateBuilder(new FakeLogCollector()).Build();

        Assert.IsType<MediaPublishSessionHandler>(host.Services.GetRequiredService<IPublishSessionHandler>());
        Assert.IsType<RTSP.IngestService.Hls.HlsPackagingHandler>(host.Services.GetRequiredService<IMediaSessionHandler>());
        Assert.IsType<RTSP.IngestService.Hls.InMemoryHlsSegmentStore>(host.Services.GetRequiredService<RTSP.IngestService.Hls.IHlsSegmentStore>());
    }

    /// <summary>Media processing alone falls back to the discarding downstream handler.</summary>
    [Fact]
    public void MediaProcessingAlone_DiscardsMedia()
    {
        var services = new ServiceCollection().AddLogging().AddMediaProcessing();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RTSP.IngestService.Configuration.MediaOptions()));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DiscardingMediaSessionHandler>(provider.GetRequiredService<IMediaSessionHandler>());
    }

    /// <summary>A registered consumer replaces the default downstream handler; repeating the registration is harmless.</summary>
    [Fact]
    public void RegisteredConsumer_ReplacesDefault()
    {
        var consumer = new RecordingMediaHandler();
        var builder = TestHosts.CreateBuilder(new FakeLogCollector());
        builder.Services.AddSingleton<IMediaSessionHandler>(consumer);
        builder.Services.AddMediaProcessing();

        using var host = builder.Build();

        Assert.Same(consumer, host.Services.GetRequiredService<IMediaSessionHandler>());
        Assert.IsType<MediaPublishSessionHandler>(host.Services.GetRequiredService<IPublishSessionHandler>());
    }
}
