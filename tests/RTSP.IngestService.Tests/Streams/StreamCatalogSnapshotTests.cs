// Tests of the persistent catalog snapshot: snapshots derived by adding, replacing and removing entries equal the
// snapshot built from scratch over the same entries (order, Current flags and both lookups), and deriving one never
// changes the snapshot it was derived from. Seeded random sequences; no timing.

using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Tests.Streams;

/// <summary>Tests for <see cref="StreamCatalogSnapshot"/>.</summary>
public sealed class StreamCatalogSnapshotTests
{
    /// <summary>Verifies random sequences of changes keep the derived snapshot equal to a reference built from scratch.</summary>
    /// <param name="seed">The random seed.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DerivedSnapshots_EqualTheReference(int seed)
    {
        var random = new Random(seed);
        var model = new Dictionary<string, StreamCatalogEntry>(StringComparer.Ordinal);
        var snapshot = StreamCatalogSnapshot.Empty;
        for (var step = 0; step < 2000; step++)
        {
            var upserts = new List<StreamCatalogEntry>();
            var removals = new List<string>();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            for (var change = random.Next(1, 5); change > 0; change--)
            {
                var incarnation = "i" + random.Next(300).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!touched.Add(incarnation))
                {
                    continue;
                }

                if (model.ContainsKey(incarnation) && random.Next(3) == 0)
                {
                    removals.Add(incarnation);
                    model.Remove(incarnation);
                }
                else
                {
                    var entry = Entry(incarnation, model.GetValueOrDefault(incarnation), random);
                    upserts.Add(entry);
                    model[incarnation] = entry;
                }
            }

            var before = snapshot;
            var beforeStreams = before.Streams.ToArray();
            snapshot = before.With(before.Version + 1, true, null, upserts, removals);

            Assert.Equal(beforeStreams, before.Streams);
            AssertEqual(Reference(model.Values), snapshot);
        }

        AssertEqual(Reference(model.Values), new StreamCatalogSnapshot(1, true, null, model.Values));
    }

    /// <summary>Verifies removing the newest incarnation of a stream makes the next newest current, and its return restores it.</summary>
    [Fact]
    public void RemovingTheNewest_PromotesTheNext()
    {
        var older = Entry("a", 1);
        var newer = Entry("b", 2);
        var snapshot = new StreamCatalogSnapshot(1, true, null, [older, newer]);
        Assert.Equal("b", snapshot.Find("live", "cam")!.IncarnationId);
        Assert.Equal([true, false], snapshot.Streams.Select(static e => e.Current));

        var removed = snapshot.With(2, true, null, [], ["b"]);
        Assert.Equal("a", removed.Find("live", "cam")!.IncarnationId);
        Assert.True(removed.FindIncarnation("a")!.Current);
        Assert.Null(removed.FindIncarnation("b"));
        Assert.Equal("b", snapshot.Find("live", "cam")!.IncarnationId);

        var restored = removed.With(3, true, null, [newer], []);
        Assert.Equal(["b", "a"], restored.Streams.Select(static e => e.IncarnationId));
        Assert.Equal([true, false], restored.Streams.Select(static e => e.Current));
        Assert.False(restored.FindIncarnation("a")!.Current);
    }

    /// <summary>Verifies an unchanged entry is shared by the derived snapshot rather than copied.</summary>
    [Fact]
    public void UnchangedEntries_AreShared()
    {
        var snapshot = new StreamCatalogSnapshot(1, true, null, [Entry("a", 1, "one"), Entry("b", 2, "two")]);
        var derived = snapshot.With(2, true, null, [Entry("b", 2, "two") with { Hls = new StreamHlsInfo { State = StreamHlsState.Ended } }], []);

        Assert.Same(snapshot.FindIncarnation("a"), derived.FindIncarnation("a"));
        Assert.Equal(StreamHlsState.Ended, derived.FindIncarnation("b")!.Hls.State);
        Assert.Equal(StreamHlsState.Live, snapshot.FindIncarnation("b")!.Hls.State);
    }

    /// <summary>Returns an entry.</summary>
    /// <param name="incarnation">The incarnation.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="stream">The stream identity.</param>
    /// <returns>The entry.</returns>
    private static StreamCatalogEntry Entry(string incarnation, long generation, string stream = "cam") => new()
    {
        Application = "live",
        Stream = stream,
        IncarnationId = incarnation,
        Generation = generation,
        Publishing = new StreamPublishingInfo { State = StreamPublishingState.Publishing },
        Hls = new StreamHlsInfo { State = StreamHlsState.Live, PlaylistAvailable = true },
    };

    /// <summary>Returns a random entry for an incarnation; its name stays fixed once it exists, as the catalog's do.</summary>
    /// <param name="incarnation">The incarnation.</param>
    /// <param name="existing">Its current entry, if any.</param>
    /// <param name="random">The random source.</param>
    /// <returns>The entry.</returns>
    private static StreamCatalogEntry Entry(string incarnation, StreamCatalogEntry? existing, Random random) => new()
    {
        Application = existing?.Application ?? (random.Next(4) == 0 ? "other" : "live"),
        Stream = existing?.Stream ?? "s" + random.Next(20).ToString(System.Globalization.CultureInfo.InvariantCulture),
        IncarnationId = incarnation,
        Generation = random.Next(4) == 0 ? existing?.Generation ?? 0 : random.Next(50),
        Current = random.Next(2) == 0,
        Publishing = new StreamPublishingInfo { State = random.Next(2) == 0 ? StreamPublishingState.Publishing : StreamPublishingState.Ended },
        Hls = new StreamHlsInfo { State = (StreamHlsState)random.Next(4) },
    };

    /// <summary>
    /// Returns the expected listing, computed independently: ordered by application, stream, newest generation first
    /// and incarnation, with the first entry of each stream current.
    /// </summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The expected listing.</returns>
    private static List<StreamCatalogEntry> Reference(IEnumerable<StreamCatalogEntry> entries)
    {
        var ordered = entries
            .OrderBy(static e => e.Application, StringComparer.Ordinal)
            .ThenBy(static e => e.Stream, StringComparer.Ordinal)
            .ThenByDescending(static e => e.Generation)
            .ThenBy(static e => e.IncarnationId, StringComparer.Ordinal)
            .ToList();
        return [.. ordered.Select((e, i) => e with { Current = i == 0 || ordered[i - 1].Application != e.Application || ordered[i - 1].Stream != e.Stream })];
    }

    /// <summary>Asserts a snapshot lists and finds exactly the expected entries.</summary>
    /// <param name="expected">The expected listing.</param>
    /// <param name="actual">The snapshot.</param>
    private static void AssertEqual(List<StreamCatalogEntry> expected, StreamCatalogSnapshot actual)
    {
        Assert.Equal(expected, actual.Streams);
        foreach (var entry in expected)
        {
            Assert.Equal(entry, actual.FindIncarnation(entry.IncarnationId));
            if (entry.Current)
            {
                Assert.Equal(entry, actual.Find(entry.Application, entry.Stream));
            }
        }

        Assert.Null(actual.FindIncarnation("absent"));
        Assert.Null(actual.Find("live", "absent"));
    }
}
