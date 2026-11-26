// Tests of RTP output configuration: binding of every setting, defaults, range and pattern validation, the
// cross-field rules (ports, address families, unicast and multicast destinations, multicast-only settings, unique
// names) and that the shipped configuration and the service host start with no RTP output.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtp;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Tests for <see cref="RtpOptions"/> and <see cref="RtpOutputOptions"/>.</summary>
public sealed class RtpOptionsTests
{
    /// <summary>A valid unicast output.</summary>
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Rtp:Outputs:0:Name"] = "studio-a",
        ["Rtp:Outputs:0:Application"] = "live",
        ["Rtp:Outputs:0:Stream"] = "camera1",
        ["Rtp:Outputs:0:Destination"] = "192.0.2.10",
        ["Rtp:Outputs:0:VideoPort"] = "5004",
        ["Rtp:Outputs:0:AudioPort"] = "5006",
    };

    /// <summary>Verifies the defaults of an output that sets only the required values.</summary>
    [Fact]
    public void Defaults_AreApplied()
    {
        var output = Assert.Single(Bind(Valid).Outputs);

        Assert.Equal(RtpOutputMode.Unicast, output.Mode);
        Assert.Equal((96, 97), (output.VideoPayloadType, output.AudioPayloadType));
        Assert.Null(output.AudioClockRate);
        Assert.Equal(1500, output.Mtu);
        Assert.Null(output.TimeToLive);
        Assert.Equal(64, output.EffectiveTimeToLive);
        Assert.Null(output.SourceAddress);
        Assert.False(output.MulticastLoopback);
        Assert.Null(output.SendBufferSize);
        Assert.Equal(2048, output.QueueCapacity);
        Assert.True(output.RtcpEnabled);
        Assert.Equal(1, new RtpOutputOptions { Mode = RtpOutputMode.Multicast }.EffectiveTimeToLive);
    }

    /// <summary>Verifies the shipped configuration and the code defaults configure no output.</summary>
    [Fact]
    public void ShippedConfiguration_HasNoOutputs()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).Build();
        using var provider = new ServiceCollection().AddIngestServiceOptions(configuration).BuildServiceProvider();

        Assert.Empty(provider.GetRequiredService<IOptions<RtpOptions>>().Value.Outputs);
        Assert.Empty(Bind(new Dictionary<string, string?>()).Outputs);
    }

    /// <summary>Verifies every setting binds, for a multicast IPv4 output and an IPv6 unicast output.</summary>
    [Fact]
    public void AllSettings_Bind()
    {
        var options = Bind(new Dictionary<string, string?>(Valid)
        {
            ["Rtp:Outputs:0:Mode"] = "Multicast",
            ["Rtp:Outputs:0:Destination"] = "239.255.10.1",
            ["Rtp:Outputs:0:VideoPayloadType"] = "100",
            ["Rtp:Outputs:0:AudioPayloadType"] = "101",
            ["Rtp:Outputs:0:AudioClockRate"] = "90000",
            ["Rtp:Outputs:0:Mtu"] = "9000",
            ["Rtp:Outputs:0:TimeToLive"] = "16",
            ["Rtp:Outputs:0:SourceAddress"] = "192.0.2.1",
            ["Rtp:Outputs:0:MulticastLoopback"] = "true",
            ["Rtp:Outputs:0:SendBufferSize"] = "1048576",
            ["Rtp:Outputs:0:QueueCapacity"] = "64",
            ["Rtp:Outputs:0:RtcpEnabled"] = "false",
            ["Rtp:Outputs:1:Name"] = "v6",
            ["Rtp:Outputs:1:Application"] = "live",
            ["Rtp:Outputs:1:Stream"] = "camera1",
            ["Rtp:Outputs:1:Destination"] = "2001:db8::10",
            ["Rtp:Outputs:1:AudioPort"] = "6000",
        });

        var multicast = options.Outputs[0];
        Assert.Equal(
            (RtpOutputMode.Multicast, "239.255.10.1", 100, 101, (int?)90000, 9000, (int?)16, "192.0.2.1", true, (int?)1048576, 64, false),
            (multicast.Mode, multicast.Destination, multicast.VideoPayloadType, multicast.AudioPayloadType, multicast.AudioClockRate, multicast.Mtu, multicast.TimeToLive, multicast.SourceAddress, multicast.MulticastLoopback, multicast.SendBufferSize, multicast.QueueCapacity, multicast.RtcpEnabled));
        Assert.Equal(16, multicast.EffectiveTimeToLive);
        Assert.Equal(("2001:db8::10", (int?)null, (int?)6000), (options.Outputs[1].Destination, options.Outputs[1].VideoPort, options.Outputs[1].AudioPort));
    }

    /// <summary>Verifies valid multicast groups of both families, including scoped IPv6 groups, are accepted.</summary>
    /// <param name="group">The group.</param>
    [Theory]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("ff02::1")]
    [InlineData("ff15::1234")]
    [InlineData("ff3e::8000:1")]
    public void MulticastGroup_IsAccepted(string group) =>
        Assert.Single(Bind(new Dictionary<string, string?>(Valid) { ["Rtp:Outputs:0:Mode"] = "Multicast", ["Rtp:Outputs:0:Destination"] = group }).Outputs);

    /// <summary>Verifies each invalid setting or combination fails validation with a message naming the problem.</summary>
    /// <param name="key">The key under the first output, or a full key.</param>
    /// <param name="value">The value.</param>
    /// <param name="expected">Text the failure must contain.</param>
    [Theory]
    [InlineData("Name", "", "Name")]
    [InlineData("Name", "bad name", "Name")]
    [InlineData("Application", "", "Application")]
    [InlineData("Stream", "a/b", "Stream")]
    [InlineData("Destination", "", "Destination")]
    [InlineData("Destination", "rtp.example.com", "Destination")]
    [InlineData("Destination", "224.1.2.3", "unicast but Destination is a multicast")]
    [InlineData("Destination", "ff15::1", "unicast but Destination is a multicast")]
    [InlineData("Destination", "255.255.255.255", "unicast but Destination is a multicast, broadcast")]
    [InlineData("Destination", "0.0.0.0", "unspecified")]
    [InlineData("Destination", "::", "unspecified")]
    [InlineData("VideoPort", "5005", "odd")]
    [InlineData("AudioPort", "5004", "both video and audio")]
    [InlineData("VideoPort", "0", "VideoPort")]
    [InlineData("VideoPort", "65536", "VideoPort")]
    [InlineData("VideoPayloadType", "95", "VideoPayloadType")]
    [InlineData("AudioPayloadType", "128", "AudioPayloadType")]
    [InlineData("AudioClockRate", "7999", "AudioClockRate")]
    [InlineData("Mtu", "575", "Mtu")]
    [InlineData("Mtu", "9217", "Mtu")]
    [InlineData("TimeToLive", "0", "TimeToLive")]
    [InlineData("TimeToLive", "256", "TimeToLive")]
    [InlineData("SendBufferSize", "1024", "SendBufferSize")]
    [InlineData("QueueCapacity", "63", "QueueCapacity")]
    [InlineData("SourceAddress", "not-an-address", "SourceAddress")]
    [InlineData("SourceAddress", "2001:db8::1", "same address family")]
    [InlineData("MulticastLoopback", "true", "applies only to multicast")]
    [InlineData("Mode", "Multicast", "not a multicast group")]
    public void InvalidSetting_FailsValidation(string key, string value, string expected)
    {
        var values = new Dictionary<string, string?>(Valid) { ["Rtp:Outputs:0:" + key] = value };

        var ex = Assert.Throws<OptionsValidationException>(() => Bind(values));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies an output with neither port, a multicast source address, and a duplicate name are rejected.</summary>
    [Fact]
    public void CrossFieldRules_FailValidation()
    {
        var noPorts = new Dictionary<string, string?>(Valid);
        noPorts.Remove("Rtp:Outputs:0:VideoPort");
        noPorts.Remove("Rtp:Outputs:0:AudioPort");
        Assert.Contains("sends nothing", Assert.Throws<OptionsValidationException>(() => Bind(noPorts)).Message, StringComparison.Ordinal);

        var multicastSource = new Dictionary<string, string?>(Valid) { ["Rtp:Outputs:0:Mode"] = "Multicast", ["Rtp:Outputs:0:Destination"] = "239.1.1.1", ["Rtp:Outputs:0:SourceAddress"] = "239.1.1.2" };
        Assert.Contains("not a multicast group", Assert.Throws<OptionsValidationException>(() => Bind(multicastSource)).Message, StringComparison.Ordinal);

        var duplicate = new Dictionary<string, string?>(Valid)
        {
            ["Rtp:Outputs:1:Name"] = "studio-a",
            ["Rtp:Outputs:1:Application"] = "live",
            ["Rtp:Outputs:1:Stream"] = "camera2",
            ["Rtp:Outputs:1:Destination"] = "192.0.2.11",
            ["Rtp:Outputs:1:VideoPort"] = "5004",
        };
        Assert.Contains("more than once", Assert.Throws<OptionsValidationException>(() => Bind(duplicate)).Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies more than <see cref="RtpOptions.MaxOutputs"/> outputs are rejected.</summary>
    [Fact]
    public void TooManyOutputs_FailValidation()
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i <= RtpOptions.MaxOutputs; i++)
        {
            values[$"Rtp:Outputs:{i}:Name"] = $"out{i}";
            values[$"Rtp:Outputs:{i}:Application"] = "live";
            values[$"Rtp:Outputs:{i}:Stream"] = "camera1";
            values[$"Rtp:Outputs:{i}:Destination"] = "192.0.2.10";
            values[$"Rtp:Outputs:{i}:VideoPort"] = "5004";
        }

        Assert.Contains("At most", Assert.Throws<OptionsValidationException>(() => Bind(values)).Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies an unknown key in an output is rejected by strict binding.</summary>
    [Fact]
    public void UnknownKey_FailsBinding()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Bind(new Dictionary<string, string?>(Valid) { ["Rtp:Outputs:0:Port"] = "5004" }));

        Assert.Contains("Port", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the composition: with no outputs the media handler is HLS packaging itself, with outputs it is the RTP
    /// stage wrapping HLS packaging, and invalid RTP configuration stops the host before it starts.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_ComposesRtpOutputOnlyWhenConfigured()
    {
        using (var plain = TestHosts.CreateBuilder(new FakeLogCollector()).Build())
        {
            Assert.IsType<HlsPackagingHandler>(plain.Services.GetRequiredService<IMediaSessionHandler>());
        }

        var configured = new Dictionary<string, string?>(Valid);
        using (var host = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: configured).Build())
        {
            var handler = Assert.IsType<RtpOutputHandler>(host.Services.GetRequiredService<IMediaSessionHandler>());
            Assert.IsType<HlsPackagingHandler>(handler.Inner);
            Assert.Same(handler, host.Services.GetRequiredService<IMediaSessionHandler>());
            Assert.Equal("studio-a", Assert.Single(handler.GetOutputs()).Output.Name);
        }

        configured["Rtp:Outputs:0:VideoPort"] = "5005";
        using var invalid = TestHosts.CreateBuilder(new FakeLogCollector(), overrides: configured).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => invalid.StartAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Binds and validates the RTP options from configuration.</summary>
    /// <param name="values">The configuration.</param>
    /// <returns>The options.</returns>
    private static RtpOptions Bind(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddIngestServiceOptions(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RtpOptions>>().Value;
    }
}
