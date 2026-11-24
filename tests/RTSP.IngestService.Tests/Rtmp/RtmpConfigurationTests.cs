// Tests for RTMP configuration validation (RtmpOptions, RtmpAuthenticationOptions) and for the configured publisher
// authenticator: ranges, address parsing, the cross-property buffer rule, duplicate publishers without echoing keys,
// constant-time key matching and redaction of secrets in request text.

using System.Net;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="RtmpOptionsValidator"/> and <see cref="ConfiguredPublisherAuthenticator"/>.
/// </summary>
public sealed class RtmpConfigurationTests
{
    /// <summary>A valid stream key.</summary>
    private const string Key = "key-0123456789abcdef";

    /// <summary>Verifies that the defaults are valid and match the documented values.</summary>
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new RtmpOptions();

        Assert.True(Validate(options).Succeeded);
        Assert.Equal(("0.0.0.0", 1935, 100), (options.ListenAddress, options.Port, options.MaxConnections));
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)), (options.HandshakeTimeout, options.ConnectionTimeout));
        Assert.Equal((8 * 1024 * 1024, 128, 4096, 16 * 1024 * 1024), (options.MaxMessageSize, options.InboundChunkSize, options.OutboundChunkSize, options.MaxBufferedBytes));
        Assert.Equal("live", options.Authentication.Application);
        Assert.Empty(options.Authentication.Publishers);
    }

    /// <summary>Verifies that each out-of-range value fails validation naming the property, without clamping it.</summary>
    /// <param name="property">The property to set.</param>
    /// <param name="value">The invalid value.</param>
    [Theory]
    [InlineData(nameof(RtmpOptions.Port), "-1")]
    [InlineData(nameof(RtmpOptions.Port), "65536")]
    [InlineData(nameof(RtmpOptions.MaxConnections), "0")]
    [InlineData(nameof(RtmpOptions.MaxConnections), "10001")]
    [InlineData(nameof(RtmpOptions.HandshakeTimeout), "00:00:00.5")]
    [InlineData(nameof(RtmpOptions.HandshakeTimeout), "00:05:01")]
    [InlineData(nameof(RtmpOptions.ConnectionTimeout), "00:00:00")]
    [InlineData(nameof(RtmpOptions.ConnectionTimeout), "01:00:01")]
    [InlineData(nameof(RtmpOptions.MaxMessageSize), "127")]
    [InlineData(nameof(RtmpOptions.MaxMessageSize), "16777216")]
    [InlineData(nameof(RtmpOptions.InboundChunkSize), "127")]
    [InlineData(nameof(RtmpOptions.InboundChunkSize), "65537")]
    [InlineData(nameof(RtmpOptions.OutboundChunkSize), "127")]
    [InlineData(nameof(RtmpOptions.OutboundChunkSize), "65537")]
    [InlineData(nameof(RtmpOptions.MaxBufferedBytes), "65535")]
    [InlineData(nameof(RtmpOptions.MaxBufferedBytes), "268435457")]
    public void OutOfRangeValue_FailsValidation(string property, string value)
    {
        var options = new RtmpOptions();
        switch (property)
        {
            case nameof(RtmpOptions.HandshakeTimeout):
                options.HandshakeTimeout = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case nameof(RtmpOptions.ConnectionTimeout):
                options.ConnectionTimeout = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                var number = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                typeof(RtmpOptions).GetProperty(property)!.SetValue(options, number);
                break;
        }

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(property, result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies listen address validation: IPv4 and IPv6 literals pass; names and malformed text fail.</summary>
    /// <param name="address">The address.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1%3", true)]
    [InlineData("", false)]
    [InlineData("localhost", false)]
    [InlineData("1", false)]
    [InlineData("300.1.1.1", false)]
    [InlineData("1.2.3.4:1935", false)]
    public void ListenAddress_IsValidated(string address, bool valid)
    {
        var result = Validate(new RtmpOptions { ListenAddress = address });

        Assert.Equal(valid, result.Succeeded);
    }

    /// <summary>Verifies the cross-property rule that the buffered-bytes limit must hold at least one maximum-size message.</summary>
    [Fact]
    public void MaxBufferedBytesBelowMaxMessageSize_FailsValidation()
    {
        var result = Validate(new RtmpOptions { MaxMessageSize = 1024 * 1024, MaxBufferedBytes = 512 * 1024 });

        Assert.True(result.Failed);
        Assert.Contains(nameof(RtmpOptions.MaxBufferedBytes), result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies application and publisher name validation.</summary>
    /// <param name="name">The name.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData("live", true)]
    [InlineData("cam-01.main_2", true)]
    [InlineData("", false)]
    [InlineData("-live", false)]
    [InlineData("live/app", false)]
    [InlineData("has space", false)]
    public void Names_AreValidated(string name, bool valid)
    {
        var application = new RtmpOptions();
        application.Authentication.Application = name;
        var publisher = WithPublishers((name, Key));

        Assert.Equal(valid, Validate(application).Succeeded);
        Assert.Equal(valid, Validate(publisher).Succeeded);
    }

    /// <summary>Verifies that stream keys must be 16 to 256 characters.</summary>
    /// <param name="length">The key length.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void StreamKeyLength_IsValidated(int length, bool valid)
    {
        Assert.Equal(valid, Validate(WithPublishers(("cam", new string('k', length)))).Succeeded);
    }

    /// <summary>Verifies that duplicate publisher names and duplicate keys fail validation and the key is never echoed.</summary>
    [Fact]
    public void DuplicatePublishers_FailValidationWithoutEchoingKeys()
    {
        var duplicateName = Validate(WithPublishers(("cam", Key), ("cam", Key + "x")));
        var duplicateKey = Validate(WithPublishers(("cam1", Key), ("cam2", Key)));

        Assert.True(duplicateName.Failed);
        Assert.True(duplicateKey.Failed);
        Assert.DoesNotContain(Key, duplicateName.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, duplicateKey.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies that a configured key for the configured application authorises with the publisher name as identity.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Authenticator_KnownKey_AuthorisesWithPublisherName()
    {
        var authenticator = new ConfiguredPublisherAuthenticator(Options.Create(WithPublishers(("cam1", Key + "1"), ("cam2", Key + "2"))));

        var result = await authenticator.AuthenticateAsync(Request("live", Key + "2"), TestContext.Current.CancellationToken);

        Assert.True(result.IsAuthorized);
        Assert.Equal("cam2", result.StreamIdentity);
    }

    /// <summary>Verifies rejections: unknown key, key prefix, wrong application, application case, and no publishers configured.</summary>
    /// <param name="application">The requested application.</param>
    /// <param name="streamName">The presented stream name.</param>
    /// <param name="configured">Whether a publisher is configured.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("live", "wrong-key-000000000", true)]
    [InlineData("live", "key-0123456789abcde", true)]
    [InlineData("other", Key, true)]
    [InlineData("LIVE", Key, true)]
    [InlineData("live", Key, false)]
    public async Task Authenticator_InvalidRequest_IsRejectedWithoutSecrets(string application, string streamName, bool configured)
    {
        var options = configured ? WithPublishers(("cam", Key)) : new RtmpOptions();
        var authenticator = new ConfiguredPublisherAuthenticator(Options.Create(options));

        var result = await authenticator.AuthenticateAsync(Request(application, streamName), TestContext.Current.CancellationToken);

        Assert.False(result.IsAuthorized);
        Assert.False(string.IsNullOrEmpty(result.RejectionReason));
        Assert.DoesNotContain(streamName, result.RejectionReason!, StringComparison.Ordinal);
    }

    /// <summary>Verifies that a request's text form omits the stream name, query and tcUrl, which may contain secrets.</summary>
    [Fact]
    public void AuthenticationRequest_ToString_RedactsSecrets()
    {
        var request = new PublishAuthenticationRequest(1, new IPEndPoint(IPAddress.Loopback, 5000), "live", Key, "token=secret-token", "rtmp://h/live?pw=secret-pw", "live");

        var text = request.ToString();

        Assert.Contains("live", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-pw", text, StringComparison.Ordinal);
    }

    /// <summary>Validates options with the source-generated validator.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The result.</returns>
    private static ValidateOptionsResult Validate(RtmpOptions options) => new RtmpOptionsValidator().Validate(null, options);

    /// <summary>Creates default options with the given publishers.</summary>
    /// <param name="publishers">Name and key pairs.</param>
    /// <returns>The options.</returns>
    private static RtmpOptions WithPublishers(params (string Name, string Key)[] publishers)
    {
        var options = new RtmpOptions();
        foreach (var (name, key) in publishers)
        {
            options.Authentication.Publishers.Add(new RtmpPublisherCredential { Name = name, StreamKey = key });
        }

        return options;
    }

    /// <summary>Creates a publish request.</summary>
    /// <param name="application">The application.</param>
    /// <param name="streamName">The stream name.</param>
    /// <returns>The request.</returns>
    private static PublishAuthenticationRequest Request(string application, string streamName) =>
        new(1, null, application, streamName, null, null, "live");
}
