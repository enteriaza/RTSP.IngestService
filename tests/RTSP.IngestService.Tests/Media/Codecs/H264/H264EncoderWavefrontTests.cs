// The managed H.264 encoder's row wavefront: with any helper count it must produce exactly what serial coding
// produces (access units, reconstruction, statistics), under arbitrary scheduling, and fail, cancel and dispose
// without disturbing its helpers or other encoders. A shadow checker driven by the wavefront probe proves that every
// macroblock is coded and deblocked once, only after its dependencies, and that a picture completes only when all
// work is done; the same checker detects the illegal one-macroblock lag (the negative control).

using System.Collections.Concurrent;
using System.Diagnostics;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>The wavefront tests' collection: their spinning helper threads would starve timing-sensitive tests running beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class H264WavefrontTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "H.264 wavefront";
}

/// <summary>Row-wavefront determinism, scheduling, failure and lifecycle tests.</summary>
[Collection(H264WavefrontTestGroup.Name)]
public sealed class H264EncoderWavefrontTests
{
    /// <summary>The perturbation seeds; recorded so any failure reproduces.</summary>
    private static readonly int[] Seeds = [1, 7, 42, 1009, 65537];

    /// <summary>Gets the helper counts.</summary>
    public static TheoryData<int> HelperCounts => [1, 2, 3, 7, 15];

    /// <summary>Gets the helper counts with the perturbation seeds and modes.</summary>
    public static TheoryData<int, int, bool> StressCases
    {
        get
        {
            var data = new TheoryData<int, int, bool>();
            foreach (var helpers in new[] { 1, 2, 3, 7, 15 })
            {
                foreach (var seed in Seeds)
                {
                    data.Add(helpers, seed, seed % 2 == 0);
                }
            }

            return data;
        }
    }

    /// <summary>Gets the points at which a fault is injected, with the worker that faults: 0, the encoding thread, or 1, a helper.</summary>
    public static TheoryData<int, int> FaultPoints
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var point in Enum.GetValues<H264WavefrontPoint>())
            {
                if (point is not H264WavefrontPoint.HelperAttach and not H264WavefrontPoint.HelperLeave)
                {
                    data.Add((int)point, 0);
                }

                if (point != H264WavefrontPoint.PictureComplete)
                {
                    data.Add((int)point, 1);
                }
            }

            return data;
        }
    }

    /// <summary>Gets the points at which cancellation is requested, with the requesting worker.</summary>
    public static TheoryData<int, int> CancelPoints
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var point in Enum.GetValues<H264WavefrontPoint>())
            {
                if (point is H264WavefrontPoint.PictureComplete or H264WavefrontPoint.HelperLeave)
                {
                    continue;
                }

                if (point != H264WavefrontPoint.HelperAttach)
                {
                    data.Add((int)point, 0);
                }

                data.Add((int)point, 1);
            }

            return data;
        }
    }

    /// <summary>Every helper count reproduces serial coding exactly, across odd sizes, slices, I_PCM, references and rate-control retries.</summary>
    /// <param name="count">The helper count.</param>
    [Theory]
    [MemberData(nameof(HelperCounts))]
    public void HelperCountsMatchSerial(int count)
    {
        var cases = new (H264EncoderOptions Options, SyntheticPattern Pattern, Func<long, H264PictureControl?>? Control)[]
        {
            (H264EncoderTestSupport.Options(200, 120) with { MaxReferenceFrames = 2 }, SyntheticPattern.Motion, null),
            (H264EncoderTestSupport.Options(176, 144) with { SlicesPerPicture = 3 }, SyntheticPattern.Cuts, null),
            (H264EncoderTestSupport.Options(100, 52) with { SlicesPerPicture = 4 }, SyntheticPattern.Noise, static i => i % 3 == 1 ? new H264PictureControl { ForcePcm = static mb => mb % 7 == 3 } : null),
            (H264EncoderTestSupport.Options(240, 136) with { MaxBitrateKilobits = 250 }, SyntheticPattern.Noise, null),
            (H264EncoderTestSupport.Options(16, 16), SyntheticPattern.Detail, null),
            (H264EncoderTestSupport.Options(48, 320), SyntheticPattern.Motion, null),
        };

        using var helpers = new H264WavefrontHelpers(count);
        foreach (var (options, pattern, control) in cases)
        {
            var expected = EncodeSerial(options, pattern, 8, control);
            using var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers, Control = control });
            var actual = new List<EncodedFrame>();
            var units = new List<H264EncodedAccessUnit>();
            var rows = (options.Height + 15) / 16;
            for (var i = 0; i < 8; i++)
            {
                using var frame = H264EncoderTestSupport.CreateFrame(pattern, options.Width, options.Height, i);
                encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
                actual.Add(H264EncoderTestSupport.Take(encoder, units));
                var job = encoder.Wavefront!;
                int coded = 0, deblocked = 0, bands = 0;
                foreach (var worker in job.Workers)
                {
                    coded += worker.Rows;
                    deblocked += worker.DeblockRows;
                    bands += worker.Bands;
                }

                Assert.Equal(rows, coded);
                Assert.Equal(rows, deblocked);
                Assert.Equal(job.Bands, bands);
            }

            AssertSame(expected, actual);
            H264EncoderTestSupport.AssertMatchesReconstruction(actual, H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, actual));
        }
    }

    /// <summary>Zero helpers code serially: no wavefront state and the serial memory accounting.</summary>
    [Fact]
    public void ZeroHelpersAreSerial()
    {
        using var helpers = new H264WavefrontHelpers(0);
        var options = H264EncoderTestSupport.Options(96, 64);
        using var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
        using var serial = new H264Encoder(options);
        Assert.Null(encoder.Wavefront);
        Assert.Equal(serial.PictureBytes, encoder.PictureBytes);
        AssertSame(H264EncoderTestSupport.EncodeAll(serial, SyntheticPattern.Motion, 4), H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 4));
    }

    /// <summary>Half-sample planes computed in disjoint bands, in any order and from dirty sums, equal the serial planes.</summary>
    [Fact]
    public void PreparedBandsMatchSerial()
    {
        foreach (var (mbw, mbh) in new[] { (13, 8), (80, 45), (1, 1), (3, 17) })
        {
            using var reference = new H264EncoderPicture(mbw, mbh) { Id = 5 };
            var random = new Random(mbw * 1000 + mbh);
            random.NextBytes(reference.Luma.AsSpan(0, reference.Width * reference.Height));
            var serial = new H264HalfSamplePlanes(reference.Width, reference.Height);
            serial.Prepare(reference);
            foreach (var bandRows in new[] { 1, 5, 16, 32, 48, reference.Height })
            {
                var banded = new H264HalfSamplePlanes(reference.Width, reference.Height);
                var sums = new int[H264HalfSamplePlanes.SumsLength(reference.Width)];
                var bands = Enumerable.Range(0, (reference.Height + bandRows - 1) / bandRows).ToArray();
                random.Shuffle(bands);
                foreach (var band in bands)
                {
                    sums.AsSpan().Fill(random.Next());
                    banded.PrepareRows(reference.Luma, band * bandRows, Math.Min((band + 1) * bandRows, reference.Height), sums);
                }

                Assert.True(serial.Planes.B.AsSpan().SequenceEqual(banded.Planes.B), $"b differs at {mbw}x{mbh} MBs, {bandRows}-row bands");
                Assert.True(serial.Planes.H.AsSpan().SequenceEqual(banded.Planes.H), $"h differs at {mbw}x{mbh} MBs, {bandRows}-row bands");
                Assert.True(serial.Planes.J.AsSpan().SequenceEqual(banded.Planes.J), $"j differs at {mbw}x{mbh} MBs, {bandRows}-row bands");
            }
        }
    }

    /// <summary>Under seeded random or deterministic delays and yields at every wavefront point, the output equals serial coding and the shadow checker sees only legal execution.</summary>
    /// <param name="count">The helper count.</param>
    /// <param name="seed">The perturbation seed.</param>
    /// <param name="random">Whether delays are random (seeded) rather than a deterministic pattern.</param>
    [Theory]
    [MemberData(nameof(StressCases))]
    public void PerturbedSchedulesMatchSerial(int count, int seed, bool random)
    {
        var options = H264EncoderTestSupport.Options(176, 112) with { SlicesPerPicture = 2, MaxReferenceFrames = 2 };
        var shadow = new WavefrontShadow(options, legalOnly: true);
        var perturb = new Perturbation(seed, random);
        using var helpers = new H264WavefrontHelpers(count, (point, worker, row, column) =>
        {
            perturb.Delay(point, worker, row, column);
            shadow.Observe(point, worker, row, column);
        });

        var expected = EncodeSerial(options, SyntheticPattern.Cuts, 6);
        List<EncodedFrame> actual;
        using (var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers }))
        {
            actual = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Cuts, 6);
        }

        Assert.True(shadow.Errors.IsEmpty, $"seed {seed}: {string.Join("; ", shadow.Errors.Take(5))}");
        Assert.True(shadow.Pictures >= 6, $"seed {seed}: {shadow.Pictures} pictures completed");
        Assert.True(shadow.HelperMacroblocks > 0, $"seed {seed}: no helper coded a macroblock");
        Assert.True(helpers.Attachments <= (long)count * shadow.Pictures, $"seed {seed}: {helpers.Attachments} attachments to {shadow.Pictures} pictures");
        AssertSame(expected, actual);
    }

    /// <summary>The negative control: a one-macroblock lag is illegal and the shadow checker detects it in both passes.</summary>
    [Fact]
    public void OneMacroblockLagIsDetected()
    {
        var options = H264EncoderTestSupport.Options(160, 96);
        var shadow = new WavefrontShadow(options, legalOnly: false);
        using var helpers = new H264WavefrontHelpers(3, (point, worker, row, column) =>
        {
            // Rows above run slowly, so a row whose wait is one macroblock short overtakes the top-right neighbour.
            if (point is H264WavefrontPoint.MacroblockStart or H264WavefrontPoint.DeblockStart && row % 2 == 0)
            {
                Thread.Sleep(1);
            }

            shadow.Observe(point, worker, row, column);
        }, dependencyLag: 1);

        using var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
        _ = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 3);
        Assert.Contains(shadow.Errors, static e => e.StartsWith("code dependency", StringComparison.Ordinal));
        Assert.Contains(shadow.Errors, static e => e.StartsWith("deblock dependency", StringComparison.Ordinal));
    }

    /// <summary>A fault at any point, on the encoding thread or a helper, surfaces with its type, faults only that encoder, and leaves the helpers serving.</summary>
    /// <param name="pointValue">The point.</param>
    /// <param name="worker">The faulting worker.</param>
    [Theory]
    [MemberData(nameof(FaultPoints))]
    public void FaultsSurfaceAndHelpersSurvive(int pointValue, int worker)
    {
        var point = (H264WavefrontPoint)pointValue;
        var options = H264EncoderTestSupport.Options(160, 96);
        var armed = 0;
        var fired = 0;
        var order = new HelperFirst();
        using var helpers = new H264WavefrontHelpers(1, (p, w, row, column) =>
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return;
            }

            order.Observe(p, w, column, worker);
            if (p == point && w == worker && Interlocked.Exchange(ref fired, 1) == 0)
            {
                throw new InjectedException($"{p} on worker {w}");
            }
        });

        using (var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers }))
        {
            var units = new List<H264EncodedAccessUnit>();
            using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 160, 96, 0))
            {
                encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
            }

            units[0].Dispose();
            units.Clear();
            Volatile.Write(ref armed, 1);
            using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 160, 96, 1))
            {
                var error = Assert.Throws<InjectedException>(() => encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal($"{point} on worker {worker}", error.Message);
            }

            Volatile.Write(ref armed, 0);
            Assert.True(encoder.IsFaulted);
            Assert.Empty(units);
            Assert.Equal(0, encoder.OutstandingUnits);
        }

        Assert.False(helpers.Ended);
        var attachments = helpers.Attachments;
        using var next = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
        AssertSame(EncodeSerial(options, SyntheticPattern.Motion, 4), H264EncoderTestSupport.EncodeAll(next, SyntheticPattern.Motion, 4));
        Assert.True(helpers.Attachments >= attachments);
    }

    /// <summary>Cancellation requested at any point drops the picture without faulting; the stream continues exactly as if the frame never arrived.</summary>
    /// <param name="pointValue">The point.</param>
    /// <param name="worker">The worker that requests cancellation.</param>
    [Theory]
    [MemberData(nameof(CancelPoints))]
    public void CancellationAtAnyPointLeavesStreamIntact(int pointValue, int worker)
    {
        var point = (H264WavefrontPoint)pointValue;
        var options = H264EncoderTestSupport.Options(160, 96) with { MaxReferenceFrames = 2 };
        using var cancellation = new CancellationTokenSource();
        var armed = 0;
        var order = new HelperFirst();
        using var helpers = new H264WavefrontHelpers(1, (p, w, row, column) =>
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return;
            }

            order.Observe(p, w, column, worker);
            if (p == point && w == worker)
            {
                cancellation.Cancel();
            }
        });

        using var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 2);
        var units = new List<H264EncodedAccessUnit>();
        Volatile.Write(ref armed, 1);
        using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 160, 96, 2))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => encoder.Encode(frame, units, cancellationToken: cancellation.Token));
        }

        Volatile.Write(ref armed, 0);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(units);
        Assert.False(encoder.IsFaulted);
        frames.AddRange(H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 4, 3));

        using var reference = new H264Encoder(options);
        var expected = H264EncoderTestSupport.EncodeAll(reference, SyntheticPattern.Motion, 2);
        expected.AddRange(H264EncoderTestSupport.EncodeAll(reference, SyntheticPattern.Motion, 4, 3));
        AssertSame(expected, frames);
        Assert.False(helpers.Ended);
    }

    /// <summary>A cancelled token before a picture starts is observed by the encoding thread alone.</summary>
    [Fact]
    public void PreCancelledTokenThrows()
    {
        using var helpers = new H264WavefrontHelpers(3);
        using var encoder = new H264Encoder(H264EncoderTestSupport.Options(96, 64), new H264EncoderTestHooks { Wavefront = helpers });
        var units = new List<H264EncodedAccessUnit>();
        using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 96, 64, 0);
        Assert.ThrowsAny<OperationCanceledException>(() => encoder.Encode(frame, units, cancellationToken: new CancellationToken(true)));
        Assert.False(encoder.IsFaulted);
        Assert.Empty(units);
    }

    /// <summary>Disposing the helpers is bounded and ends every thread, whether idle, after pictures, or during a picture; an encoder whose helpers are gone codes serially with the same output.</summary>
    [Fact]
    public void HelperDisposalIsBoundedAtEveryLifecyclePoint()
    {
        var options = H264EncoderTestSupport.Options(160, 96);
        var expected = EncodeSerial(options, SyntheticPattern.Motion, 6);

        var idle = new H264WavefrontHelpers(15);
        var watch = Stopwatch.StartNew();
        idle.Dispose();
        Assert.True(idle.Ended);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"idle disposal took {watch.Elapsed}");
        idle.Dispose();

        var after = new H264WavefrontHelpers(3);
        using (var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = after }))
        {
            var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 3);
            after.Dispose();
            Assert.True(after.Ended);
            frames.AddRange(H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 3, 3));
            AssertSame(expected, frames);
        }

        Thread? disposer = null;
        H264WavefrontHelpers? during = null;
        during = new H264WavefrontHelpers(3, (point, worker, row, column) =>
        {
            if (point == H264WavefrontPoint.RowStart && worker > 0 && disposer is null)
            {
                var thread = new Thread(() => during!.Dispose());
                if (Interlocked.CompareExchange(ref disposer, thread, null) is null)
                {
                    thread.Start();
                }
            }
        });

        using (var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = during }))
        {
            AssertSame(expected, H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 6));
        }

        Assert.NotNull(disposer);
        Assert.True(disposer.Join(TimeSpan.FromSeconds(15)));
        Assert.True(during.Ended);
    }

    /// <summary>Disposing an encoder leaves its helpers serving other encoders.</summary>
    [Fact]
    public void EncoderDisposalLeavesHelpersServing()
    {
        var options = H264EncoderTestSupport.Options(160, 96);
        using var helpers = new H264WavefrontHelpers(2);
        using (var first = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers }))
        {
            _ = H264EncoderTestSupport.EncodeAll(first, SyntheticPattern.Motion, 2);
        }

        using var second = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
        AssertSame(EncodeSerial(options, SyntheticPattern.Motion, 4), H264EncoderTestSupport.EncodeAll(second, SyntheticPattern.Motion, 4));
        Assert.False(helpers.Ended);
    }

    /// <summary>Encoders at different sizes run concurrently, each with its own helpers or sharing one set; faulting, cancelling or disposing one leaves the others exact.</summary>
    [Fact]
    public void SessionsAreIsolated()
    {
        var small = H264EncoderTestSupport.Options(176, 112);
        var large = H264EncoderTestSupport.Options(320, 176) with { SlicesPerPicture = 2 };
        var expectedSmall = EncodeSerial(small, SyntheticPattern.Motion, 8);
        var expectedLarge = EncodeSerial(large, SyntheticPattern.Cuts, 8);
        using var own = new H264WavefrontHelpers(3);
        using var shared = new H264WavefrontHelpers(2);
        using var faulting = new H264WavefrontHelpers(2, static (point, worker, row, column) =>
        {
            if (point == H264WavefrontPoint.MacroblockStart && worker == 1 && row == 3)
            {
                throw new InjectedException("session fault");
            }
        });

        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentDictionary<string, List<EncodedFrame>>();
        var failures = new ConcurrentQueue<Exception>();
        void Run(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failures.Enqueue(new InvalidOperationException(name, e));
            }
        }

        var threads = new[]
        {
            new Thread(() => Run("own", () =>
            {
                using var encoder = new H264Encoder(large, new H264EncoderTestHooks { Wavefront = own });
                results["own"] = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Cuts, 8);
            })),
            new Thread(() => Run("shared-a", () =>
            {
                using var encoder = new H264Encoder(small, new H264EncoderTestHooks { Wavefront = shared });
                results["shared-a"] = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 8);
            })),
            new Thread(() => Run("shared-b", () =>
            {
                using var encoder = new H264Encoder(large, new H264EncoderTestHooks { Wavefront = shared });
                results["shared-b"] = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Cuts, 8);
            })),
            new Thread(() => Run("faulted", () =>
            {
                using var encoder = new H264Encoder(large, new H264EncoderTestHooks { Wavefront = faulting });
                var units = new List<H264EncodedAccessUnit>();
                for (var i = 0; i < 500; i++)
                {
                    using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Cuts, 320, 176, i);
                    try
                    {
                        encoder.Encode(frame, units, cancellationToken: TestContext.Current.CancellationToken);
                        units.ForEach(static u => u.Dispose());
                        units.Clear();
                    }
                    catch (InjectedException)
                    {
                        Assert.True(encoder.IsFaulted);
                        return;
                    }
                }

                Assert.Fail("no helper reached row 3");
            })),
            new Thread(() => Run("cancelled", () =>
            {
                using var helpers = new H264WavefrontHelpers(2);
                using var encoder = new H264Encoder(small, new H264EncoderTestHooks { Wavefront = helpers });
                var units = new List<H264EncodedAccessUnit>();
                for (var i = 0; ; i++)
                {
                    using var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, 176, 112, i);
                    if (i == 3)
                    {
                        cancellation.CancelAfter(1);
                    }

                    try
                    {
                        encoder.Encode(frame, units, cancellationToken: cancellation.Token);
                        units.ForEach(static u => u.Dispose());
                        units.Clear();
                    }
                    catch (OperationCanceledException)
                    {
                        Assert.False(encoder.IsFaulted);
                        return;
                    }
                }
            })),
        };

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(2)));
        }

        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures));
        AssertSame(expectedLarge, results["own"]);
        AssertSame(expectedSmall, results["shared-a"]);
        AssertSame(expectedLarge, results["shared-b"]);
        Assert.False(own.Ended);
        Assert.False(shared.Ended);
        Assert.False(faulting.Ended);
    }

    /// <summary>Picture memory accounts for the per-worker payloads, contexts, band sums and wavefront state, and grows with the helper count.</summary>
    [Fact]
    public void PictureBytesAccountForWorkers()
    {
        const int Macroblocks = 80 * 45;
        var options = H264EncoderTestSupport.Options(1280, 720);
        using var serial = new H264Encoder(options);
        var previous = serial.PictureBytes;
        foreach (var count in new[] { 1, 2, 3, 7, 15 })
        {
            using var helpers = new H264WavefrontHelpers(count);
            using var encoder = new H264Encoder(options, new H264EncoderTestHooks { Wavefront = helpers });
            var payloads = (count * (long)encoder.MaxAccessUnitBytes) + Macroblocks;
            var contexts = (count * (long)H264MacroblockContext.ScratchBytes) + ((1L + count) * 4 * 6 * 1280);
            var job = (45L * ((2 * 16 * 4) + 4 + 8 + 1)) + ((1L + count) * 80);
            Assert.Equal(serial.PictureBytes + payloads + contexts + job, encoder.PictureBytes);
            Assert.True(encoder.PictureBytes > previous);
            previous = encoder.PictureBytes;
        }
    }

    /// <summary>Encodes frames serially.</summary>
    /// <param name="options">The options.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="count">The frame count.</param>
    /// <param name="control">The per-picture control.</param>
    /// <returns>The frames.</returns>
    private static List<EncodedFrame> EncodeSerial(H264EncoderOptions options, SyntheticPattern pattern, int count, Func<long, H264PictureControl?>? control = null)
    {
        using var encoder = new H264Encoder(options, control is null ? null : new H264EncoderTestHooks { Control = control });
        return H264EncoderTestSupport.EncodeAll(encoder, pattern, count);
    }

    /// <summary>Asserts identical access units, reconstructions and statistics.</summary>
    /// <param name="expected">The serial frames.</param>
    /// <param name="actual">The wavefront frames.</param>
    private static void AssertSame(List<EncodedFrame> expected, List<EncodedFrame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Data.AsSpan().SequenceEqual(actual[i].Data), $"access unit {i} differs");
            Assert.True(expected[i].Reconstruction.AsSpan().SequenceEqual(actual[i].Reconstruction), $"reconstruction {i} differs");
            Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
            Assert.Equal(expected[i].IsKeyframe, actual[i].IsKeyframe);
            Assert.Equal(expected[i].Statistics, actual[i].Statistics);
        }
    }

    /// <summary>Orders the workers so the one under test reaches every point: a helper under test is waited for and given the lead at every claim; the encoding thread under test is given the lead at the start.</summary>
    private sealed class HelperFirst
    {
        /// <summary>1 once helper 1 attached.</summary>
        private int _attached;

        /// <summary>Observes a probe report.</summary>
        /// <param name="point">The point.</param>
        /// <param name="worker">The reporting worker.</param>
        /// <param name="column">The column.</param>
        /// <param name="target">The worker under test.</param>
        public void Observe(H264WavefrontPoint point, int worker, int column, int target)
        {
            if (worker == 1 && point == H264WavefrontPoint.HelperAttach)
            {
                Volatile.Write(ref _attached, 1);
                if (target == 0)
                {
                    // The encoding thread is under test: let it claim first.
                    Thread.Sleep(20);
                }
            }

            if (target == 0 && worker != 0 && point == H264WavefrontPoint.DeblockStart && column == 0)
            {
                Thread.Sleep(5);
            }

            if (target == 0 || worker != 0 || point is not (H264WavefrontPoint.BandClaim or H264WavefrontPoint.RowClaim or H264WavefrontPoint.DeblockStart) || (point == H264WavefrontPoint.DeblockStart && column != 0))
            {
                return;
            }

            var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
            while (Volatile.Read(ref _attached) == 0 && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(1);
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>The injected failure.</summary>
    /// <param name="message">The message.</param>
    private sealed class InjectedException(string message) : Exception(message);

    /// <summary>Seeded delays and yields per worker: random, or a deterministic pattern of the position.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="random">Whether to draw delays at random.</param>
    private sealed class Perturbation(int seed, bool random)
    {
        /// <summary>One generator per worker; each is used by its worker's thread only.</summary>
        private readonly Random[] _random = [.. Enumerable.Range(0, H264WavefrontHelpers.MaxCount + 1).Select(w => new Random((seed * 1000) + w))];

        /// <summary>Delays the reporting worker.</summary>
        /// <param name="point">The point.</param>
        /// <param name="worker">The worker.</param>
        /// <param name="row">The row.</param>
        /// <param name="column">The column.</param>
        public void Delay(H264WavefrontPoint point, int worker, int row, int column)
        {
            int draw;
            if (random)
            {
                draw = _random[worker].Next(64);
            }
            else
            {
                draw = (int)(((uint)(((int)point * 31) + (worker * 17) + (row * 7) + column + seed) * 2654435761u) >> 26);
            }

            switch (draw)
            {
                case < 40:
                    break;
                case < 50:
                    Thread.Yield();
                    break;
                case < 58:
                    Thread.SpinWait(50 * (draw - 49));
                    break;
                case < 62:
                    Thread.Sleep(0);
                    break;
                default:
                    if (point is H264WavefrontPoint.RowClaim or H264WavefrontPoint.BandClaim or H264WavefrontPoint.HelperAttach or H264WavefrontPoint.HelperLeave or H264WavefrontPoint.RowComplete)
                    {
                        Thread.Sleep(1);
                    }
                    else
                    {
                        Thread.SpinWait(2000);
                    }

                    break;
            }
        }
    }

    /// <summary>Tracks every macroblock of the current picture through both passes and records illegal execution.</summary>
    private sealed class WavefrontShadow
    {
        /// <summary>The width in macroblocks.</summary>
        private readonly int _width;

        /// <summary>The height in macroblocks.</summary>
        private readonly int _rows;

        /// <summary>Whether errors other than dependency violations are expected never to occur.</summary>
        private readonly bool _legalOnly;

        /// <summary>0 unstarted, 1 coding, 2 coded, per macroblock.</summary>
        private readonly int[] _coded;

        /// <summary>0 unstarted, 1 filtering, 2 filtered, per macroblock.</summary>
        private readonly int[] _deblocked;

        /// <summary>Claims per half-sample band.</summary>
        private readonly int[] _bands = new int[256];

        /// <summary>Completed pictures.</summary>
        private int _pictures;

        /// <summary>Macroblocks coded by helpers.</summary>
        private int _helperMacroblocks;

        /// <summary>Initializes a new instance of the <see cref="WavefrontShadow"/> class.</summary>
        /// <param name="options">The encoder options.</param>
        /// <param name="legalOnly">Whether the wavefront under test is the legal one.</param>
        public WavefrontShadow(H264EncoderOptions options, bool legalOnly)
        {
            _width = (options.Width + 15) / 16;
            _rows = (options.Height + 15) / 16;
            _legalOnly = legalOnly;
            _coded = new int[_width * _rows];
            _deblocked = new int[_width * _rows];
        }

        /// <summary>Gets the recorded violations.</summary>
        public ConcurrentQueue<string> Errors { get; } = new();

        /// <summary>Gets the completed pictures.</summary>
        public int Pictures => Volatile.Read(ref _pictures);

        /// <summary>Gets the macroblocks helpers coded.</summary>
        public int HelperMacroblocks => Volatile.Read(ref _helperMacroblocks);

        /// <summary>Checks one probe report.</summary>
        /// <param name="point">The point.</param>
        /// <param name="worker">The worker.</param>
        /// <param name="row">The row.</param>
        /// <param name="column">The column.</param>
        public void Observe(H264WavefrontPoint point, int worker, int row, int column)
        {
            switch (point)
            {
                case H264WavefrontPoint.BandClaim:
                    if (Interlocked.Increment(ref _bands[row]) != 1)
                    {
                        Errors.Enqueue($"duplicate band {row} by worker {worker}");
                    }

                    break;
                case H264WavefrontPoint.MacroblockStart:
                    Start(_coded, "code", worker, row, column);
                    Require(_coded, "code dependency", worker, row, column, column - 1, row);
                    Require(_coded, "code dependency", worker, row, column, column - 1, row - 1);
                    Require(_coded, "code dependency", worker, row, column, column, row - 1);
                    Require(_coded, "code dependency", worker, row, column, column + 1, row - 1);
                    break;
                case H264WavefrontPoint.MacroblockComplete:
                    Finish(_coded, "code", worker, row, column);
                    if (worker > 0)
                    {
                        Interlocked.Increment(ref _helperMacroblocks);
                    }

                    break;
                case H264WavefrontPoint.ProgressPublished:
                    if (Volatile.Read(ref _coded[(row * _width) + column]) != 2)
                    {
                        Errors.Enqueue($"published incomplete ({column}, {row}) by worker {worker}");
                    }

                    break;
                case H264WavefrontPoint.RowComplete:
                    for (var x = 0; x < _width; x++)
                    {
                        if (Volatile.Read(ref _coded[(row * _width) + x]) != 2)
                        {
                            Errors.Enqueue($"row {row} completed without ({x}, {row})");
                        }
                    }

                    break;
                case H264WavefrontPoint.DeblockStart:
                    if (_legalOnly)
                    {
                        AllDone(_coded, $"deblock of ({column}, {row}) before coding finished");
                    }

                    Start(_deblocked, "deblock", worker, row, column);
                    Require(_deblocked, "deblock dependency", worker, row, column, column - 1, row);
                    Require(_deblocked, "deblock dependency", worker, row, column, Math.Min(column + 1, _width - 1), row - 1);
                    break;
                case H264WavefrontPoint.DeblockComplete:
                    Finish(_deblocked, "deblock", worker, row, column);
                    break;
                case H264WavefrontPoint.PictureComplete:
                    AllDone(_coded, "picture completed with uncoded macroblocks");
                    AllDone(_deblocked, "picture completed with unfiltered macroblocks");
                    Array.Clear(_coded);
                    Array.Clear(_deblocked);
                    Array.Clear(_bands);
                    Interlocked.Increment(ref _pictures);
                    break;
                default:
                    break;
            }
        }

        /// <summary>Marks a macroblock started; a second start is duplicate work.</summary>
        /// <param name="state">The pass state.</param>
        /// <param name="pass">The pass.</param>
        /// <param name="worker">The worker.</param>
        /// <param name="row">The row.</param>
        /// <param name="column">The column.</param>
        private void Start(int[] state, string pass, int worker, int row, int column)
        {
            if (Interlocked.CompareExchange(ref state[(row * _width) + column], 1, 0) != 0)
            {
                Errors.Enqueue($"duplicate {pass} of ({column}, {row}) by worker {worker}");
            }
        }

        /// <summary>Marks a started macroblock done.</summary>
        /// <param name="state">The pass state.</param>
        /// <param name="pass">The pass.</param>
        /// <param name="worker">The worker.</param>
        /// <param name="row">The row.</param>
        /// <param name="column">The column.</param>
        private void Finish(int[] state, string pass, int worker, int row, int column)
        {
            if (Interlocked.CompareExchange(ref state[(row * _width) + column], 2, 1) != 1)
            {
                Errors.Enqueue($"{pass} of ({column}, {row}) by worker {worker} finished unstarted");
            }
        }

        /// <summary>Records a violation unless a dependency inside the picture is done.</summary>
        /// <param name="state">The pass state.</param>
        /// <param name="what">The violation.</param>
        /// <param name="worker">The worker.</param>
        /// <param name="row">The row.</param>
        /// <param name="column">The column.</param>
        /// <param name="x">The dependency column.</param>
        /// <param name="y">The dependency row.</param>
        private void Require(int[] state, string what, int worker, int row, int column, int x, int y)
        {
            if (x >= 0 && y >= 0 && x < _width && Volatile.Read(ref state[(y * _width) + x]) != 2)
            {
                Errors.Enqueue($"{what}: ({column}, {row}) by worker {worker} before ({x}, {y})");
            }
        }

        /// <summary>Records a violation unless every macroblock is done.</summary>
        /// <param name="state">The pass state.</param>
        /// <param name="what">The violation.</param>
        private void AllDone(int[] state, string what)
        {
            for (var i = 0; i < state.Length; i++)
            {
                if (Volatile.Read(ref state[i]) != 2)
                {
                    Errors.Enqueue($"{what}: ({i % _width}, {i / _width})");
                    return;
                }
            }
        }
    }
}
