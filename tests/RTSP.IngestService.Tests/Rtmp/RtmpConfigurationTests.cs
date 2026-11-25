// Tests for RTMP configuration validation (RtmpOptions, RtmpAuthenticationOptions): ranges, address parsing, the
// cross-property buffer rule, credential names, applications and key hashes, duplicates without echoing hashes, and
// redaction of secrets in request text. Authentication behaviour is tested in PublisherAuthenticationTests.

using System.Net;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="RtmpOptionsValidator"/> and <see cref="PublishAuthenticationRequest"/>.
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
        Assert.Empty(options.Authentication.Publishers);
        Assert.Equal((10, TimeSpan.FromMinutes(1)), (options.Authentication.RateLimit.FailureLimit, options.Authentication.RateLimit.Window));
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
        var publisherName = WithPublishers((name, "live", Key));
        var application = WithPublishers(("cam", name, Key));

        Assert.Equal(valid, Validate(publisherName).Succeeded);
        Assert.Equal(valid, Validate(application).Succeeded);
    }

    /// <summary>Verifies that a credential without an application or key hash fails validation: there is no default application.</summary>
    [Fact]
    public void MissingApplicationOrKeyHash_FailsValidation()
    {
        var noApplication = WithPublishers(("cam", "live", Key));
        noApplication.Authentication.Publishers[0].Application = string.Empty;
        var noHash = WithPublishers(("cam", "live", Key));
        noHash.Authentication.Publishers[0].KeyHash = string.Empty;

        Assert.Contains(nameof(RtmpPublisherCredential.Application), Validate(noApplication).FailureMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(RtmpPublisherCredential.KeyHash), Validate(noHash).FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies that a malformed key hash, including a plaintext key in its place, fails validation without echoing the value.</summary>
    /// <param name="keyHash">The configured value.</param>
    [Theory]
    [InlineData(Key)]
    [InlineData("hmac-sha256$AAAA$BBBB")]
    [InlineData("sha256$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("hmac-sha256$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA*")]
    [InlineData("hmac-sha256$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA$extra")]
    public void MalformedKeyHash_FailsValidationWithoutEchoingIt(string keyHash)
    {
        var options = WithPublishers(("cam", "live", Key));
        options.Authentication.Publishers[0].KeyHash = keyHash;

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(RtmpPublisherCredential.KeyHash), result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(keyHash, result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies that duplicate publisher names and duplicate key hashes fail validation and the hash is never echoed.</summary>
    [Fact]
    public void DuplicatePublishers_FailValidationWithoutEchoingHashes()
    {
        var duplicateName = Validate(WithPublishers(("cam", "live", Key), ("cam", "other", Key + "x")));
        var duplicateHash = WithPublishers(("cam1", "live", Key), ("cam2", "live", Key + "x"));
        var hash = duplicateHash.Authentication.Publishers[0].KeyHash;
        duplicateHash.Authentication.Publishers[1].KeyHash = hash;

        var duplicateHashResult = Validate(duplicateHash);

        Assert.True(duplicateName.Failed);
        Assert.True(duplicateHashResult.Failed);
        Assert.DoesNotContain(hash, duplicateHashResult.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>Verifies the publisher count bound and the rate-limit ranges.</summary>
    [Fact]
    public void PublisherCountAndRateLimit_AreValidated()
    {
        var tooMany = new RtmpOptions();
        var hash = PublisherKeyHash.Create(Key);
        for (var i = 0; i <= RtmpAuthenticationOptions.MaxPublishers; i++)
        {
            tooMany.Authentication.Publishers.Add(new RtmpPublisherCredential { Name = $"cam{i}", Application = "live", KeyHash = hash });
        }

        Assert.Contains($"At most {RtmpAuthenticationOptions.MaxPublishers}", Validate(tooMany).FailureMessage, StringComparison.Ordinal);
        Assert.True(Validate(new RtmpOptions { Authentication = { RateLimit = { FailureLimit = 0 } } }).Failed);
        Assert.True(Validate(new RtmpOptions { Authentication = { RateLimit = { FailureLimit = 1001 } } }).Failed);
        Assert.True(Validate(new RtmpOptions { Authentication = { RateLimit = { Window = TimeSpan.FromMilliseconds(500) } } }).Failed);
        Assert.True(Validate(new RtmpOptions { Authentication = { RateLimit = { Window = TimeSpan.FromHours(2) } } }).Failed);
        Assert.True(Validate(new RtmpOptions { Authentication = { RateLimit = { FailureLimit = 1, Window = TimeSpan.FromSeconds(1) } } }).Succeeded);
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

    /// <summary>Creates default options with the given publishers, hashing each key.</summary>
    /// <param name="publishers">Name, application and key triples.</param>
    /// <returns>The options.</returns>
    private static RtmpOptions WithPublishers(params (string Name, string Application, string Key)[] publishers)
    {
        var options = new RtmpOptions();
        foreach (var (name, application, key) in publishers)
        {
            options.Authentication.Publishers.Add(new RtmpPublisherCredential { Name = name, Application = application, KeyHash = PublisherKeyHash.Create(key) });
        }

        return options;
    }
}
