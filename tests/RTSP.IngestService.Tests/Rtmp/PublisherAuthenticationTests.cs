// Tests for hashed publisher credentials: the key hash format, verification outcomes and metrics, per-credential
// application assignment, reload of add/remove/disable/rotate/reassign with last-known-good retention, snapshot
// consistency under concurrent reloads, and the absence of secrets in results, logs and validation messages.

using System.Text;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtmp;

/// <summary>
/// Tests for <see cref="PublisherKeyHash"/>, <see cref="ConfiguredPublisherAuthenticator"/> and
/// <see cref="PublisherCredentialStore"/>.
/// </summary>
public sealed class PublisherAuthenticationTests
{
    /// <summary>Key of credential A.</summary>
    private const string KeyA = "test-key-aaaaaaaaaaaaaaaa";

    /// <summary>Key of credential B.</summary>
    private const string KeyB = "test-key-bbbbbbbbbbbbbbbb";

    /// <summary>Key of credential C.</summary>
    private const string KeyC = "test-key-cccccccccccccccc";

    /// <summary>A rate limit high enough not to interfere.</summary>
    private static readonly Dictionary<string, string?> Unlimited = new() { ["Rtmp:Authentication:RateLimit:FailureLimit"] = "1000" };

    /// <summary>Verifies that a created hash verifies its key only, uses a fresh salt each time and never formats its contents.</summary>
    [Fact]
    public void KeyHash_VerifiesOnlyItsKey_IsSalted_AndIsRedacted()
    {
        var first = PublisherKeyHash.Create(KeyA);
        var second = PublisherKeyHash.Create(KeyA);

        Assert.NotEqual(first, second);
        Assert.StartsWith("hmac-sha256$", first, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyA, first, StringComparison.Ordinal);
        Assert.True(PublisherKeyHash.TryParse(first, out var hash));
        Assert.True(hash.Matches(Encoding.ASCII.GetBytes(KeyA)));
        Assert.False(hash.Matches(Encoding.ASCII.GetBytes(KeyB)));
        Assert.False(hash.Matches(Encoding.ASCII.GetBytes(KeyA[..^1])));
        Assert.Equal("hmac-sha256$<redacted>", hash.ToString());
        Assert.DoesNotContain(first.Split('$')[2], hash.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Verifies generated keys: 43 base64url characters, acceptable, and unique.</summary>
    [Fact]
    public void GeneratedKeys_AreAcceptableAndUnique()
    {
        var keys = Enumerable.Range(0, 100).Select(static _ => PublisherKeyHash.GenerateKey()).ToList();

        Assert.All(keys, static k => Assert.Equal(43, k.Length));
        Assert.All(keys, static k => Assert.True(PublisherKeyHash.IsAcceptableKey(k)));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Verifies the accepted key shape.</summary>
    /// <param name="key">The candidate.</param>
    /// <param name="acceptable">Whether it is acceptable.</param>
    [Theory]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcdef?query", false)]
    [InlineData("0123456789 abcdef", false)]
    [InlineData("0123456789abcdéf", false)]
    public void IsAcceptableKey_ChecksLengthAndCharacters(string key, bool acceptable)
    {
        Assert.Equal(acceptable, PublisherKeyHash.IsAcceptableKey(key));
    }

    /// <summary>Verifies the 256-character upper bound.</summary>
    [Fact]
    public void IsAcceptableKey_EnforcesMaximumLength()
    {
        Assert.True(PublisherKeyHash.IsAcceptableKey(new string('k', 256)));
        Assert.False(PublisherKeyHash.IsAcceptableKey(new string('k', 257)));
        Assert.Throws<ArgumentException>(() => PublisherKeyHash.Create(new string('k', 257)));
    }

    /// <summary>Verifies that a valid key authorises with the credential's name and application and records success.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ValidKey_AuthorisesWithCredentialNameAndApplication()
    {
        using var fixture = new AuthenticationFixture(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-b", "studio", KeyB)));

        var a = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA, "live"));
        var b = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB, "studio"));

        Assert.True(a.IsAuthorized);
        Assert.Equal(("cam-a", "live"), (a.StreamIdentity, a.Application));
        Assert.True(b.IsAuthorized);
        Assert.Equal(("cam-b", "studio"), (b.StreamIdentity, b.Application));
        Assert.Equal(["success", "success"], fixture.AttemptResults());
    }

    /// <summary>Verifies each rejection outcome, its metric tag, and that no reason contains the key.</summary>
    /// <param name="key">The presented key.</param>
    /// <param name="application">The application the publisher connected to.</param>
    /// <param name="expected">The expected metric result.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("test-key-wrongwrongwrongw", "live", "invalid_credential")]
    [InlineData("test-key-aaaaaaaaaaaaaaa", "live", "invalid_credential")]
    [InlineData("short-key", "live", "malformed_key")]
    [InlineData("test-key-aaaaaaaaaaaaaaaé", "live", "malformed_key")]
    [InlineData(KeyC, "live", "disabled")]
    [InlineData(KeyA, "studio", "application_mismatch")]
    [InlineData(KeyA, "LIVE", "application_mismatch")]
    [InlineData(KeyB, "live", "application_mismatch")]
    public async Task InvalidAttempt_IsRejectedWithFixedReason(string key, string application, string expected)
    {
        using var fixture = new AuthenticationFixture(
            TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-b", "studio", KeyB), new TestCredential("cam-c", "live", KeyC, Enabled: false)));

        var result = await fixture.AuthenticateAsync(AuthenticationFixture.Request(key, application));

        Assert.False(result.IsAuthorized);
        Assert.Null(result.Application);
        Assert.DoesNotContain(key, result.RejectionReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("cam-", result.RejectionReason!, StringComparison.Ordinal);
        Assert.Equal([expected], fixture.AttemptResults());
    }

    /// <summary>Verifies that a key matching two credentials is rejected as ambiguous rather than picking one.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task KeyMatchingTwoCredentials_IsRejectedAsAmbiguous()
    {
        using var fixture = new AuthenticationFixture(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-a2", "live", KeyA)));

        var result = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA));

        Assert.False(result.IsAuthorized);
        Assert.Equal(["ambiguous"], fixture.AttemptResults());
    }

    /// <summary>Verifies that with no credentials configured every attempt is rejected.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task NoCredentials_RejectsEverything()
    {
        using var fixture = new AuthenticationFixture(new Dictionary<string, string?>());

        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA))).IsAuthorized);
        Assert.Empty(fixture.Store.Current.Credentials);
    }

    /// <summary>
    /// Verifies that there is no plaintext fallback: the former <c>StreamKey</c> setting and a plaintext value in
    /// <c>KeyHash</c> both fail configuration, and the error text never contains the key.
    /// </summary>
    /// <param name="property">The configured property.</param>
    [Theory]
    [InlineData("StreamKey")]
    [InlineData("KeyHash")]
    public void PlaintextKeyConfiguration_FailsClosedWithoutEchoingTheKey(string property)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Rtmp:Authentication:Publishers:0:Name"] = "cam-a",
            ["Rtmp:Authentication:Publishers:0:Application"] = "live",
            [$"Rtmp:Authentication:Publishers:0:{property}"] = KeyA,
        };

        var exception = Assert.Throws<OptionsValidationException>(() => new AuthenticationFixture(settings));

        Assert.Contains(property, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyA, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies that the global application setting of the plaintext scheme is rejected: there is no default application.</summary>
    [Fact]
    public void FormerGlobalApplicationSetting_FailsConfiguration()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => new AuthenticationFixture(new Dictionary<string, string?> { ["Rtmp:Authentication:Application"] = "live" }));

        Assert.Contains("Application", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies add, remove, disable, rotate and reassign, each effective after a reload, with reload metrics and logs.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Reload_AppliesAddRemoveDisableRotateAndReassign()
    {
        using var fixture = new AuthenticationFixture(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA)), Unlimited);
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);

        fixture.Reload(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA), new TestCredential("cam-b", "live", KeyB)));
        Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);

        fixture.Reload(TestCredential.Settings(new TestCredential("cam-b", "live", KeyB)));
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA))).IsAuthorized);

        fixture.Reload(TestCredential.Settings(new TestCredential("cam-b", "live", KeyB, Enabled: false)));
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);

        fixture.Reload(TestCredential.Settings(new TestCredential("cam-b", "live", KeyC)));
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);
        Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyC))).IsAuthorized);

        fixture.Reload(TestCredential.Settings(new TestCredential("cam-b", "studio", KeyC)));
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyC, "live"))).IsAuthorized);
        var reassigned = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyC, "studio"));
        Assert.Equal(("cam-b", "studio"), (reassigned.StreamIdentity, reassigned.Application));

        Assert.Equal(
            ["invalid_credential", "success", "invalid_credential", "disabled", "invalid_credential", "success", "application_mismatch", "success"],
            fixture.AttemptResults());
        Assert.All(fixture.Reloads.GetMeasurementSnapshot(), static m => Assert.Equal("success", m.Tags["result"]));
        Assert.Equal(5, fixture.Reloads.GetMeasurementSnapshot().Count);
        Assert.Equal(5, fixture.Logs.GetSnapshot().Count(static r => r.Id.Id == 5301));
    }

    /// <summary>
    /// Verifies that invalid reloads (malformed hash, plaintext key, duplicate name, missing application, unknown key)
    /// keep the last known-good credentials, are logged and counted without secrets, and that a later valid reload applies.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task InvalidReload_KeepsLastKnownGood_AndLogsWithoutSecrets()
    {
        using var fixture = new AuthenticationFixture(TestCredential.Settings(new TestCredential("cam-a", "live", KeyA)), Unlimited);
        var good = fixture.Store.Current;
        var validB = TestCredential.Settings(new TestCredential("cam-b", "live", KeyB));
        var hashB = validB["Rtmp:Authentication:Publishers:0:KeyHash"]!;

        Dictionary<string, string?>[] invalid =
        [
            With(validB, "Rtmp:Authentication:Publishers:0:KeyHash", hashB[..^2]),
            With(validB, "Rtmp:Authentication:Publishers:0:StreamKey", KeyB),
            With(validB, "Rtmp:Authentication:Publishers:0:KeyHash", KeyB),
            With(With(With(validB, "Rtmp:Authentication:Publishers:1:Name", "cam-b"), "Rtmp:Authentication:Publishers:1:Application", "live"), "Rtmp:Authentication:Publishers:1:KeyHash", PublisherKeyHash.Create(KeyC)),
            With(validB, "Rtmp:Authentication:Publishers:0:Application", null),
            With(validB, "Rtmp:Authentication:Publishers:0:Enabled", "maybe"),
            With(validB, "Rtmp:Port", "70000"),
        ];

        foreach (var settings in invalid)
        {
            fixture.Reload(settings);
            Assert.Same(good, fixture.Store.Current);
            Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA))).IsAuthorized);
            Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);
        }

        var rejections = fixture.Logs.GetSnapshot().Where(static r => r.Id.Id == 5302).ToList();
        Assert.Equal(invalid.Length, rejections.Count);
        Assert.All(fixture.Logs.GetSnapshot(), r =>
        {
            Assert.DoesNotContain(KeyA, r.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(KeyB, r.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(hashB[..^2], r.Message, StringComparison.Ordinal);
            Assert.Null(r.Exception);
        });
        Assert.Equal(invalid.Length, fixture.Reloads.GetMeasurementSnapshot().Count(static m => (string?)m.Tags["result"] == "failure"));

        fixture.Reload(validB);
        Assert.True((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB))).IsAuthorized);
        Assert.False((await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA))).IsAuthorized);
    }

    /// <summary>
    /// Verifies that every authentication sees one consistent snapshot while reloads alternate between two configurations
    /// that assign the same key to different names and applications: each result is one configuration's pair, never a mix.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ConcurrentReloads_EachAttemptSeesOneConsistentSnapshot()
    {
        var one = TestCredential.Settings(new TestCredential("cam-one", "live", KeyA), new TestCredential("other-one", "live", KeyB));
        var two = TestCredential.Settings(new TestCredential("other-two", "live", KeyB), new TestCredential("cam-two", "live", KeyA));
        using var fixture = new AuthenticationFixture(one, Unlimited);
        var token = TestContext.Current.CancellationToken;
        using var done = new CancellationTokenSource();

        var reloader = Task.Run(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    fixture.Reload(i % 2 == 0 ? two : one);
                }

                done.Cancel();
            },
            token);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            async () =>
            {
                var identities = new HashSet<string>(StringComparer.Ordinal);
                while (!done.IsCancellationRequested)
                {
                    var result = await fixture.Authenticator.AuthenticateAsync(AuthenticationFixture.Request(KeyA), token);
                    Assert.True(result.IsAuthorized);
                    Assert.Contains(result.StreamIdentity, (string[])["cam-one", "cam-two"]);
                    identities.Add(result.StreamIdentity);
                }

                return identities;
            },
            token)).ToList();

        await reloader.WithinSafetyTimeout();
        await Task.WhenAll(readers).WithinSafetyTimeout();

        Assert.DoesNotContain("failure", fixture.Reloads.GetMeasurementSnapshot().Select(static m => (string?)m.Tags["result"]));
        Assert.Equal(200, fixture.Reloads.GetMeasurementSnapshot().Count);
    }

    /// <summary>Verifies that the failure log reasons and results never expose stored hashes.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Results_DoNotExposeHashes()
    {
        var settings = TestCredential.Settings(new TestCredential("cam-a", "live", KeyA));
        var hash = settings["Rtmp:Authentication:Publishers:0:KeyHash"]!;
        using var fixture = new AuthenticationFixture(settings);

        var rejected = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyB));
        var accepted = await fixture.AuthenticateAsync(AuthenticationFixture.Request(KeyA));

        Assert.DoesNotContain(hash.Split('$')[2], rejected.RejectionReason!, StringComparison.Ordinal);
        Assert.Equal("cam-a", accepted.StreamIdentity);
        Assert.DoesNotContain(hash.Split('$')[2], fixture.Store.Current.Credentials[0].ToString(), StringComparison.Ordinal);
        Assert.All(fixture.Logs.GetSnapshot(), r => Assert.DoesNotContain(hash.Split('$')[2], r.Message, StringComparison.Ordinal));
    }

    /// <summary>Returns a copy of <paramref name="values"/> with one key set or removed.</summary>
    /// <param name="values">The values.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or <see langword="null"/> to remove the key.</param>
    /// <returns>The copy.</returns>
    private static Dictionary<string, string?> With(Dictionary<string, string?> values, string key, string? value)
    {
        var copy = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        if (value is null)
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        return copy;
    }
}
