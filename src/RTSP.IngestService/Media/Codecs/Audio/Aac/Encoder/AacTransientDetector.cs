// Attack detection for block switching. The region examined for frame k is the span its eight short windows would
// cover, frame samples 448 to 1599 (nine 128-sample sub-blocks; consecutive regions overlap by one sub-block). The
// signal is high-pass filtered by a first difference, and an attack is a sub-block whose energy exceeds both
// EnergyRatio times the mean energy of the preceding sub-blocks in the region and an absolute floor of about -60 dBFS.
// The decision is a pure function of the region's samples, so it does not depend on how input was chunked.

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

/// <summary>Detects attacks that require short windows. Stateless and thread-safe.</summary>
internal static class AacTransientDetector
{
    /// <summary>The first frame sample of the region.</summary>
    public const int RegionStart = 448;

    /// <summary>The region length: nine sub-blocks of 128 samples.</summary>
    public const int RegionLength = 1152;

    /// <summary>The energy rise, relative to the preceding mean, that marks an attack (10 dB).</summary>
    public const float EnergyRatio = 10f;

    /// <summary>The sub-block length.</summary>
    private const int SubBlock = 128;

    /// <summary>The minimum high-passed sub-block energy for an attack, in 16-bit-scale units (RMS of about 32).</summary>
    private const float MinimumEnergy = SubBlock * 32f * 32f;

    /// <summary>Returns whether a region contains an attack.</summary>
    /// <param name="region"><see cref="RegionLength"/> samples in 16-bit scale.</param>
    /// <returns><see langword="true"/> if short windows are needed.</returns>
    public static bool HasAttack(ReadOnlySpan<float> region)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(region.Length, RegionLength);
        var sum = 0.0;
        for (var block = 0; block < RegionLength / SubBlock; block++)
        {
            var energy = 0.0;
            for (var n = Math.Max(1, block * SubBlock); n < (block + 1) * SubBlock; n++)
            {
                var difference = region[n] - region[n - 1];
                energy += difference * difference;
            }

            if (block > 0 && energy > MinimumEnergy && energy > EnergyRatio * (sum / block))
            {
                return true;
            }

            sum += energy;
        }

        return false;
    }
}
