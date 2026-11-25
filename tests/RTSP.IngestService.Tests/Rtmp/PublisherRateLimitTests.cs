// Tests for publisher authentication rate limiting and provisioning: the token bucket limits failures per address,
// refunds successes, refills over the window, is bounded and reclaims idle entries, is race-free, is keyed by the TCP
// peer only, and is applied by the authenticator before verification. Also tests the publisher-key command.

using System.Net;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Hosting;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="PublisherAuthenticationRateLimiter"/> and <see cref="PublisherKeyCommand"/>.
/// </summary>
public sealed class PublisherRateLimitTests
{
    /// <summary>A valid key.</summary>
    private const string Key = "rate-limit-key-0000000001";

    /// <summary>Verifies that failures are limited to the burst and refill evenly over the window.</summary>
    [Fact]
    public void Failures_AreLimitedAndRefill()
    {
        var time = new FakeTimeProvider();
        var limiter = new PublisherAuthenticationRateLimiter(3, TimeSpan.FromSeconds(30), time);
        var peer = Peer("192.0.2.1");

        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.False(limiter.TryAcquire(peer, out _));

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(limiter.TryAcquire(peer, out _));
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.False(limiter.TryAcquire(peer, out _));
    }

    /// <summary>Verifies that released (successful) attempts are never charged.</summary>
    [Fact]
    public void Successes_AreNotCharged()
    {
        var limiter = new PublisherAuthenticationRateLimiter(2, TimeSpan.FromMinutes(1), new FakeTimeProvider());
        var peer = Peer("192.0.2.1");

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire(peer, out var address));
            limiter.Release(address);
        }

        Assert.Equal(0, limiter.TrackedAddressCount);
        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.True(limiter.TryAcquire(peer, out _));
        Assert.False(limiter.TryAcquire(peer, out _));
    }

    /// <summary>Verifies that addresses are independent, IPv4-mapped addresses share the IPv4 bucket, and the port is ignored.</summary>
    [Fact]
    public void Buckets_AreKeyedByNormalisedPeerAddress()
    {
        var limiter = new PublisherAuthenticationRateLimiter(1, TimeSpan.FromMinutes(1), new FakeTimeProvider());

        Assert.True(limiter.TryAcquire(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 1000), out _));
        Assert.False(limiter.TryAcquire(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 2000), out _));
        Assert.False(limiter.TryAcquire(new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.1"), 3000), out _));
        Assert.True(limiter.TryAcquire(Peer("192.0.2.2"), out _));
        Assert.True(limiter.TryAcquire(Peer("2001:db8::1"), out _));
        Assert.True(limiter.TryAcquire(null, out _));
        Assert.False(limiter.TryAcquire(null, out _));
    }

    /// <summary>
    /// Verifies the table bound: a new address is limited when the table is full of buckets that have not refilled, and
    /// is admitted once a sweep reclaims a refilled bucket; idle buckets are reclaimed after the window.
    /// </summary>
    [Fact]
    public void Table_IsBoundedAndReclaimsRefilledEntries()
    {
        var time = new FakeTimeProvider();
        var limiter = new PublisherAuthenticationRateLimiter(2, TimeSpan.FromSeconds(10), time, maxTrackedAddresses: 3);
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(limiter.TryAcquire(Peer($"192.0.2.{i}"), out _));
        }

        Assert.False(limiter.TryAcquire(Peer("192.0.2.4"), out _));
        Assert.Equal(3, limiter.TrackedAddressCount);

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(limiter.TryAcquire(Peer("192.0.2.4"), out _));
        Assert.Equal(1, limiter.TrackedAddressCount);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(limiter.TryAcquire(Peer("192.0.2.5"), out var address));
        limiter.Release(address);
        Assert.Equal(0, limiter.TrackedAddressCount);
    }

    /// <summary>Verifies that a flood of distinct addresses never grows the table beyond its bound.</summary>
    [Fact]
    public void ManyAddresses_StayWithinBound()
    {
        var limiter = new PublisherAuthenticationRateLimiter(5, TimeSpan.FromMinutes(1), new FakeTimeProvider(), maxTrackedAddresses: 100);

        for (var i = 0; i < 5000; i++)
        {
            limiter.TryAcquire(new IPEndPoint(new IPAddress(0x0A000000 + i), 1), out _);
        }

        Assert.Equal(100, limiter.TrackedAddressCount);
    }

    /// <summary>Verifies that concurrent attempts from one address cannot exceed the allowance.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentAttempts_CannotBypassTheLimit()
    {
        var limiter = new PublisherAuthenticationRateLimiter(10, TimeSpan.FromHours(1), new FakeTimeProvider());
        var peer = Peer("192.0.2.1");
        var token = TestContext.Current.CancellationToken;
        using var start = new ManualResetEventSlim();

        var attempts = Enumerable.Range(0, 200).Select(i => Task.Run(
            () =>
            {
                start.Wait(token);
                return limiter.TryAcquire(peer, out _);
            },
            token)).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts).WithinSafetyTimeout();

        Assert.Equal(10, results.Count(static r => r));
    }

    /// <summary>
    /// Verifies the authenticator applies the limit before verification: once an address is limited even the correct
    /// key is rejected as rate limited, other addresses are unaffected, and the limit lifts after the window.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Authenticator_RejectsLimitedAddressBeforeVerification()
    {
        var settings = TestCredential.Settings(new TestCredential("cam", "live", Key));
        settings["Rtmp:Authentication:RateLimit:FailureLimit"] = "2";
        settings["Rtmp:Authentication:RateLimit:Window"] = "00:00:10";
        using var fixture = new AuthenticationFixture(settings);
        const string attacker = "198.51.100.7";

        Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(Key, address: attacker))).IsAuthorized);
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request("wrong-key-00000000000001", address: attacker))).IsAuthorized);
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request("wrong-key-00000000000002", address: attacker))).IsAuthorized);
        var limited = await fixture.AuthenticateAsync(AuthenticationFixture.Request(Key, address: attacker));
        var other = await fixture.AuthenticateAsync(AuthenticationFixture.Request(Key, address: "198.51.100.8"));

        Assert.False(limited.IsAuthorized);
        Assert.Contains("Too many failed", limited.RejectionReason, StringComparison.Ordinal);
        Assert.True(other.IsAuthorized);
        Assert.Equal(["success", "invalid_credential", "invalid_credential", "rate_limited", "success"], fixture.AttemptResults());

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(Key, address: attacker))).IsAuthorized);
    }

    /// <summary>Verifies that publisher-supplied fields (tcUrl, query, application) do not influence the limiter key.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Authenticator_IgnoresPublisherSuppliedIdentity()
    {
        var settings = TestCredential.Settings(new TestCredential("cam", "live", Key));
        settings["Rtmp:Authentication:RateLimit:FailureLimit"] = "1";
        using var fixture = new AuthenticationFixture(settings);
        var peer = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);

        await fixture.AuthenticateAsync(new PublishAuthenticationRequest(1, peer, "live", "wrong-key-00000000000001", "client=1", "rtmp://a/live", "live"));
        var second = await fixture.AuthenticateAsync(new PublishAuthenticationRequest(2, new IPEndPoint(peer.Address, 40001), "other", Key, "client=2", "rtmp://b/other", "live"));

        Assert.Contains("Too many failed", second.RejectionReason, StringComparison.Ordinal);
    }

    /// <summary>Verifies <c>publisher-key generate</c>: prints a key once and a hash that verifies it.</summary>
    [Fact]
    public void KeyCommand_Generate_PrintsKeyAndMatchingHash()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(PublisherKeyCommand.TryRun(["publisher-key", "generate"], TextReader.Null, output, error, out var exitCode));

        Assert.Equal(ExitCodes.Success, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var key = lines[0]["StreamKey: ".Length..];
        Assert.True(PublisherKeyHash.TryParse(lines[1]["KeyHash: ".Length..], out var hash));
        Assert.True(hash.Matches(System.Text.Encoding.ASCII.GetBytes(key)));
        Assert.Empty(error.ToString());
    }

    /// <summary>Verifies <c>publisher-key hash</c>: reads the key from input and prints only its hash.</summary>
    [Fact]
    public void KeyCommand_Hash_ReadsStdinAndNeverEchoesTheKey()
    {
        using var input = new StringReader(Key + Environment.NewLine);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(PublisherKeyCommand.TryRun(["publisher-key", "hash"], input, output, error, out var exitCode));

        Assert.Equal(ExitCodes.Success, exitCode);
        var text = output.ToString();
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.True(PublisherKeyHash.TryParse(text.Trim()["KeyHash: ".Length..], out var hash));
        Assert.True(hash.Matches(System.Text.Encoding.ASCII.GetBytes(Key)));
    }

    /// <summary>Verifies that a byte order mark, as written by Windows PowerShell 5.1 to piped input, is not part of the key.</summary>
    [Fact]
    public void KeyCommand_Hash_IgnoresByteOrderMark()
    {
        using var input = new StringReader("\uFEFF" + Key + "\r\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(PublisherKeyCommand.TryRun(["publisher-key", "hash"], input, output, error, out var exitCode));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(PublisherKeyHash.TryParse(output.ToString().Trim()["KeyHash: ".Length..], out var hash));
        Assert.True(hash.Matches(System.Text.Encoding.ASCII.GetBytes(Key)));
    }

    /// <summary>Verifies usage errors and that other arguments run the service.</summary>
    [Fact]
    public void KeyCommand_UsageErrors_AndOtherArgumentsAreNotHandled()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.True(PublisherKeyCommand.TryRun(["publisher-key"], TextReader.Null, output, error, out var usage));
        Assert.True(PublisherKeyCommand.TryRun(["publisher-key", "hash"], new StringReader("short"), output, error, out var shortKey));
        Assert.False(PublisherKeyCommand.TryRun(["--environment", "Testing"], TextReader.Null, output, error, out _));
        Assert.False(PublisherKeyCommand.TryRun([], TextReader.Null, output, error, out _));

        Assert.Equal(ExitCodes.UsageError, usage);
        Assert.Equal(ExitCodes.UsageError, shortKey);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("short", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Creates a TCP peer endpoint.</summary>
    /// <param name="address">The address text.</param>
    /// <returns>The endpoint.</returns>
    private static IPEndPoint Peer(string address) => new(IPAddress.Parse(address), 50000);
}
