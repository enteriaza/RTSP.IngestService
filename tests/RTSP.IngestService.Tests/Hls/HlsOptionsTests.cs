// Tests for HLS option validation: defaults, range boundaries of every setting and the cross-setting rules
// (maximum segment duration not below the target; per-session storage holding a maximum-size segment; total
// storage not below per-session storage).

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="HlsOptions"/> and <see cref="HlsOptionsValidator"/>.
/// </summary>
public sealed class HlsOptionsTests
{
    /// <summary>Defaults are valid and match the documented values.</summary>
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new HlsOptions();
        Assert.True(new HlsOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal((TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10), 6), (options.TargetSegmentDuration, options.MaxSegmentDuration, options.PlaylistWindowSize));
        Assert.Equal((128L * 1024 * 1024, 2048L * 1024 * 1024), (options.MaxSessionStorageBytes, options.MaxTotalStorageBytes));
    }

    /// <summary>Each setting is validated at its boundaries.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="value">Its value.</param>
    /// <param name="valid">Whether it is valid.</param>
    [Theory]
    [InlineData(nameof(HlsOptions.TargetSegmentDuration), "00:00:01", true)]
    [InlineData(nameof(HlsOptions.TargetSegmentDuration), "00:00:00.999", false)]
    [InlineData(nameof(HlsOptions.TargetSegmentDuration), "00:00:10", true)]
    [InlineData(nameof(HlsOptions.TargetSegmentDuration), "00:00:10.001", false)]
    [InlineData(nameof(HlsOptions.MaxSegmentDuration), "00:00:04", true)]
    [InlineData(nameof(HlsOptions.MaxSegmentDuration), "00:00:03.999", false)]
    [InlineData(nameof(HlsOptions.MaxSegmentDuration), "00:02:00", true)]
    [InlineData(nameof(HlsOptions.MaxSegmentDuration), "00:02:00.001", false)]
    [InlineData(nameof(HlsOptions.PlaylistWindowSize), "3", true)]
    [InlineData(nameof(HlsOptions.PlaylistWindowSize), "2", false)]
    [InlineData(nameof(HlsOptions.PlaylistWindowSize), "100", true)]
    [InlineData(nameof(HlsOptions.PlaylistWindowSize), "101", false)]
    [InlineData(nameof(HlsOptions.MaxSessionStorageMegabytes), "32", true)]
    [InlineData(nameof(HlsOptions.MaxSessionStorageMegabytes), "31", false)]
    [InlineData(nameof(HlsOptions.MaxSessionStorageMegabytes), "2048", true)]
    [InlineData(nameof(HlsOptions.MaxSessionStorageMegabytes), "2049", false)]
    [InlineData(nameof(HlsOptions.MaxTotalStorageMegabytes), "128", true)]
    [InlineData(nameof(HlsOptions.MaxTotalStorageMegabytes), "127", false)]
    [InlineData(nameof(HlsOptions.MaxTotalStorageMegabytes), "65536", true)]
    [InlineData(nameof(HlsOptions.MaxTotalStorageMegabytes), "65537", false)]
    [InlineData(nameof(HlsOptions.MaxSessions), "1", true)]
    [InlineData(nameof(HlsOptions.MaxSessions), "0", false)]
    [InlineData(nameof(HlsOptions.MaxSessions), "10000", true)]
    [InlineData(nameof(HlsOptions.MaxSessions), "10001", false)]
    [InlineData(nameof(HlsOptions.MaxInitializationSegmentSize), "4096", true)]
    [InlineData(nameof(HlsOptions.MaxInitializationSegmentSize), "4095", false)]
    [InlineData(nameof(HlsOptions.MaxInitializationSegmentSize), "1048576", true)]
    [InlineData(nameof(HlsOptions.MaxInitializationSegmentSize), "1048577", false)]
    [InlineData(nameof(HlsOptions.MaxSegmentSize), "65536", true)]
    [InlineData(nameof(HlsOptions.MaxSegmentSize), "65535", false)]
    [InlineData(nameof(HlsOptions.MaxSegmentSize), "134217728", true)]
    [InlineData(nameof(HlsOptions.MaxSegmentSize), "134217729", false)]
    [InlineData(nameof(HlsOptions.EndedStreamRetention), "00:00:00", true)]
    [InlineData(nameof(HlsOptions.EndedStreamRetention), "-00:00:01", false)]
    [InlineData(nameof(HlsOptions.EndedStreamRetention), "00:10:00", true)]
    [InlineData(nameof(HlsOptions.EndedStreamRetention), "00:10:00.001", false)]
    public void Setting_IsValidatedAtItsBoundaries(string name, string value, bool valid)
    {
        var options = new HlsOptions();
        var property = typeof(HlsOptions).GetProperty(name)!;
        property.SetValue(options, property.PropertyType == typeof(TimeSpan)
            ? TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        ValidateOptionsResult result = new HlsOptionsValidator().Validate(null, options);

        Assert.True(valid == result.Succeeded, result.FailureMessage);
    }
}
