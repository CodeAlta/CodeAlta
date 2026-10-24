using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using CodeAlta.Catalog;
using CodeAlta.Search;

namespace CodeAlta.Tests.Search;

[TestClass]
public sealed class SearchVectorKernelTests
{
    private delegate int Kernel(ref sbyte left, ref sbyte right, int length);

    [TestMethod]
    public void Dot_EveryKernelOfThisMachine_EqualsTheScalarReference()
    {
        var kernels = new List<(string Name, Kernel Run)> { ("vector128", SearchVectorKernels.DotVector128) };
        if (Dp.IsSupported) kernels.Add(("arm64-sdot", SearchVectorKernels.DotArm));
        if (Avx2.IsSupported) kernels.Add(("x64-avx2", SearchVectorKernels.DotAvx2));
        if (AvxVnni.IsSupported) kernels.Add(("x64-avx-vnni", SearchVectorKernels.DotAvxVnni));
        var random = new Random(12);
        foreach (var length in new[] { 0, 1, 7, 15, 16, 17, 31, 32, 33, 47, 63, 64, 100, 255, 384, 768, 1023 })
        {
            for (var round = 0; round < 20; round++)
            {
                var left = new sbyte[length];
                var right = new sbyte[length];
                for (var i = 0; i < length; i++)
                {
                    // The whole range of a byte, -128 included, and extremes often.
                    left[i] = round == 0 ? sbyte.MinValue : (sbyte)random.Next(-128, 128);
                    right[i] = round == 0 ? sbyte.MinValue : round == 1 ? sbyte.MaxValue : (sbyte)random.Next(-128, 128);
                }

                var expected = SearchVectorKernels.DotScalar(ref MemoryMarshal.GetArrayDataReference(left), ref MemoryMarshal.GetArrayDataReference(right), length);
                foreach (var (name, run) in kernels)
                {
                    Assert.AreEqual(expected, run(ref MemoryMarshal.GetArrayDataReference(left), ref MemoryMarshal.GetArrayDataReference(right), length), $"{name}, length {length}");
                }

                Assert.AreEqual(expected, SearchVectorKernels.Dot(left, right), $"the kernel of this machine ({SearchVectorKernels.Name}), length {length}");
            }
        }

        Console.WriteLine($"Kernels checked: {string.Join(", ", kernels.Select(static kernel => kernel.Name))}; this machine uses {SearchVectorKernels.Name}.");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) Assert.IsTrue(kernels.Any(static kernel => kernel.Name == "arm64-sdot") || !Dp.IsSupported);
    }

    [TestMethod]
    public void Quantize_KeepsEachValueWithinHalfAStep()
    {
        var random = new Random(3);
        foreach (var dimensions in new[] { 1, 17, 384, 768 })
        {
            var vector = SearchVectorTestData.RandomUnit(random, dimensions);
            var codes = new sbyte[dimensions];
            var scale = SearchVectorKernels.Quantize(vector, codes);
            Assert.AreEqual(vector.Max(static value => Math.Abs(value)) / 127f, scale, 1e-7f);
            Assert.AreEqual(127, codes.Max(static code => Math.Abs((int)code)), "the largest value takes the largest code");
            for (var i = 0; i < dimensions; i++) Assert.IsLessThanOrEqualTo(scale / 2 * 1.0001f, Math.Abs(vector[i] - codes[i] * scale));
        }

        var zeros = new sbyte[4];
        Assert.AreEqual(0f, SearchVectorKernels.Quantize(new float[4], zeros));
        Assert.IsTrue(zeros.All(static code => code == 0));
        Assert.ThrowsExactly<ArgumentException>(() => SearchVectorKernels.Quantize([1f, float.NaN], new sbyte[2]));
        Assert.ThrowsExactly<ArgumentException>(() => SearchVectorKernels.Quantize([1f, 2f], new sbyte[1]));
    }

    [TestMethod]
    public void Quantize_KeepsTheOrderOfSimilarities_AsFloatsGiveIt()
    {
        const int Dimensions = 384, Rows = 2000, Queries = 20, K = 10;
        var random = new Random(5);
        var vectors = Enumerable.Range(0, Rows).Select(_ => SearchVectorTestData.RandomUnit(random, Dimensions)).ToArray();
        var index = new SearchVectorIndex(Dimensions);
        for (var row = 0; row < Rows; row++) index.Add(row, vectors[row], SearchVectorTestData.Info(SearchKind.Message));
        var overlaps = new List<int>();
        for (var q = 0; q < Queries; q++)
        {
            // A query near a row, as a real query is near what it looks for.
            var near = vectors[random.Next(Rows)];
            var query = SearchVectorTestData.Normalize(near.Zip(SearchVectorTestData.RandomUnit(random, Dimensions), static (a, b) => a + 0.8f * b).ToArray());
            var exact = Enumerable.Range(0, Rows).OrderByDescending(row => SearchVectorTestData.DotF(vectors[row], query)).Take(K).Select(static row => (long)row).ToHashSet();
            var found = index.Search(query, new SearchScope(), K);
            overlaps.Add(found.Count(hit => exact.Contains(hit.ChunkId)));
            Assert.AreEqual(SearchVectorTestData.DotF(vectors[found[0].ChunkId], query), found[0].Similarity, 0.02f, "the similarity is the dot product of the vectors");
        }

        Assert.IsGreaterThanOrEqualTo(9.0, overlaps.Average(), $"top {K} overlaps: {string.Join(",", overlaps)}");
        Assert.IsGreaterThanOrEqualTo(8, overlaps.Min());
    }
}

[TestClass]
public sealed class SearchVectorIndexTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Search_AppliesTheScopeBeforeComparing()
    {
        const int Dimensions = 32;
        var random = new Random(9);
        var query = SearchVectorTestData.RandomUnit(random, Dimensions);
        var index = new SearchVectorIndex(Dimensions);
        // Row 1 is the closest of all; each other row is a little less close, so that a scope that leaves out the
        // closest ones must still find the best row it holds.
        var rows = new (long Id, SearchVectorRowInfo Info)[]
        {
            (1, new(SearchKind.Message, SearchRole.User, Day, "default", "p1", "s1")),
            (2, new(SearchKind.Message, SearchRole.Assistant, Day.AddDays(1), "default", "p1", "s1")),
            (3, new(SearchKind.Message, SearchRole.User, Day.AddDays(2), "work", "p2", "s2")),
            (4, new(SearchKind.Plan, SearchRole.None, Day.AddDays(3), null, "P2", null)),
            (5, new(SearchKind.Skill, SearchRole.None, Day.AddDays(4), null, null, null)),
            (6, new(SearchKind.Message, SearchRole.Agent, Day.AddDays(5), "work", "p3", "s3")),
        };
        foreach (var (id, info) in rows) index.Add(id, SearchVectorTestData.Near(query, random, 0.3f * id), info);

        long[] Find(SearchScope scope, int count = 10) => index.Search(query, scope, count).Select(static hit => hit.ChunkId).ToArray();

        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, Find(new SearchScope()), "the closest first");
        CollectionAssert.AreEqual(new long[] { 3 }, Find(new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Message }, ProjectId = "p2" }, 1));
        CollectionAssert.AreEqual(new long[] { 4, 5 }, Find(new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Plan, SearchKind.Skill } }));
        CollectionAssert.AreEqual(new long[] { 3, 4, 5, 6 }, Find(new SearchScope { SpaceId = "WORK" }), "a space keeps the rows of no space");
        CollectionAssert.AreEqual(new long[] { 3, 4 }, Find(new SearchScope { ProjectId = "p2" }), "projects compare without case, as SQL does");
        CollectionAssert.AreEqual(new long[] { 1, 2, 5 }, Find(new SearchScope { Projects = new HashSet<string> { "p1" } }), "the projects of a space, and the rows of no project");
        CollectionAssert.AreEqual(new long[] { 1, 2 }, Find(new SearchScope { SessionId = "s1" }));
        CollectionAssert.AreEqual(new long[] { 3, 4, 5, 6 }, Find(new SearchScope { ExceptSessionId = "s1" }));
        CollectionAssert.AreEqual(new long[] { 1, 3 }, Find(new SearchScope { Role = SearchRole.User }));
        CollectionAssert.AreEqual(new long[] { 2, 3, 4 }, Find(new SearchScope { Since = Day.AddDays(1), Until = Day.AddDays(3) }));
        CollectionAssert.AreEqual(new long[] { 1, 2, 5, 6 }, Find(new SearchScope { ExcludedProjects = new HashSet<string> { "P2" } }));
        CollectionAssert.AreEqual(new long[] { 1, 2, 4, 5 }, Find(new SearchScope { ExcludedSpaces = new HashSet<string> { "work" } }));
        Assert.IsEmpty(Find(new SearchScope { ProjectId = "unknown" }));
        Assert.IsEmpty(Find(new SearchScope { SessionId = "unknown" }));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, Find(new SearchScope { ExceptSessionId = "unknown" }));
        Assert.IsEmpty(Find(new SearchScope(), 0));
        Assert.ThrowsExactly<ArgumentException>(() => index.Search(new float[Dimensions + 1], new SearchScope(), 1));
    }

    [TestMethod]
    public void Remove_LeavesATombstone_ThenCompactsBeyondAFifth()
    {
        var random = new Random(1);
        var index = new SearchVectorIndex(16);
        var query = SearchVectorTestData.RandomUnit(random, 16);
        for (var id = 1; id <= 10; id++) index.Add(id, SearchVectorTestData.Near(query, random, 0.1f * id), SearchVectorTestData.Info(SearchKind.Doc));
        Assert.IsTrue(index.Remove(1));
        Assert.IsTrue(index.Remove(2));
        Assert.IsFalse(index.Remove(2), "removed once");
        Assert.AreEqual(2, index.Tombstones, "a fifth of the rows is kept as tombstones");
        Assert.AreEqual(3, index.Search(query, new SearchScope(), 1)[0].ChunkId, "a removed row is never found");
        Assert.IsTrue(index.Remove(3));
        Assert.AreEqual(0, index.Tombstones, "beyond a fifth, the rows are compacted");
        Assert.AreEqual(7, index.Count);
        CollectionAssert.AreEqual(new long[] { 4, 5, 6, 7, 8, 9, 10 }, index.Search(query, new SearchScope(), 10).Select(static hit => hit.ChunkId).ToArray(), "the rows that remain keep their order");

        // A chunk added again gets its new row; its old one is a tombstone.
        index.Add(10, query, SearchVectorTestData.Info(SearchKind.Doc));
        Assert.AreEqual(7, index.Count);
        Assert.AreEqual(1, index.Tombstones);
        Assert.AreEqual(10, index.Search(query, new SearchScope(), 1)[0].ChunkId);
        Assert.IsTrue(index.Contains(10));
        index.Compact();
        Assert.AreEqual(0, index.Tombstones);
        Assert.AreEqual(10, index.Search(query, new SearchScope(), 1)[0].ChunkId);
    }

    [TestMethod]
    public void Search_InParallel_FindsWhatOneThreadFinds()
    {
        const int Dimensions = 48;
        var random = new Random(4);
        var index = new SearchVectorIndex(Dimensions);
        var rows = SearchVectorIndex.ParallelThreshold + 7_000;
        for (var id = 0; id < rows; id++)
        {
            index.Add(id, SearchVectorTestData.RandomUnit(random, Dimensions), new SearchVectorRowInfo(id % 3 == 0 ? SearchKind.Plan : SearchKind.Message, SearchRole.None, Day, null, "p" + (id % 5), null));
        }

        var query = SearchVectorTestData.RandomUnit(random, Dimensions);
        foreach (var scope in new[] { new SearchScope(), new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Plan }, ProjectId = "p2" } })
        {
            var single = index.Search(query, scope, 50, parallelism: 1);
            var parallel = index.Search(query, scope, 50, parallelism: 8);
            Assert.HasCount(50, single);
            CollectionAssert.AreEqual(single.Select(static hit => hit.ChunkId).ToArray(), parallel.Select(static hit => hit.ChunkId).ToArray());
        }
    }

    [TestMethod]
    public async Task Search_RunsWhileTheWriterAddsAndRemoves()
    {
        // The work is counted, not timed: a writer that runs until a clock stops it fills the index as fast as the
        // processor goes, every search scans it all, and on a loaded machine the clock comes late (8 GB, 40 minutes).
        // 90 000 adds leave 60 000 rows, above ParallelThreshold, so that the searches also run on several threads.
        const int Dimensions = 24, Readers = 4, Adds = 90_000;
        var index = new SearchVectorIndex(Dimensions);
        var random = new Random(6);
        var vectors = Enumerable.Range(0, 64).Select(_ => SearchVectorTestData.RandomUnit(random, Dimensions)).ToArray();
        using var reading = new CountdownEvent(Readers);
        using var written = new ManualResetEventSlim();
        // Threads of their own: the searches fill the pool with their parts, which must not keep these from running.
        var writer = Task.Factory.StartNew(() =>
        {
            try
            {
                // The writer starts once every reader searches, so that they overlap even on one processor.
                Assert.IsTrue(reading.Wait(TimeSpan.FromSeconds(30)), "The readers did not start.");
                for (long id = 0; id < Adds; id++)
                {
                    index.Add(id, vectors[id % vectors.Length], SearchVectorTestData.Info(SearchKind.Message));
                    if (id % 3 == 0) index.Remove(id - 2);
                }
            }
            finally
            {
                written.Set();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var readers = Enumerable.Range(0, Readers).Select(_ => Task.Factory.StartNew(() =>
        {
            var searches = 0;
            do
            {
                var hits = index.Search(vectors[searches % vectors.Length], new SearchScope { Kinds = new HashSet<SearchKind> { SearchKind.Message } }, 5);
                Assert.IsTrue(hits.All(static hit => hit.Similarity is > -1.1f and < 1.1f));
                if (searches++ == 0) reading.Signal();
            }
            while (!written.IsSet);

            return searches;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await writer;
        var searched = await Task.WhenAll(readers);
        Assert.IsTrue(searched.All(static count => count > 0));
        Assert.AreEqual(index.Count, Enumerable.Range(0, Adds).Count(id => index.Contains(id)));
        Assert.IsGreaterThan(SearchVectorIndex.ParallelThreshold, index.Count);
    }

    [TestMethod]
    public void Search_Benchmark_100kBy768_OneThreadAndParallel()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_SEARCH_BENCH") is not { Length: > 0 })
        {
            Assert.Inconclusive("Set CODEALTA_SEARCH_BENCH=1 to measure the search by meaning.");
        }

        const int Dimensions = 768, Rows = 100_000, K = 50;
        var random = new Random(42);
        var index = new SearchVectorIndex(Dimensions, Rows);
        var vector = new float[Dimensions];
        for (var id = 0; id < Rows; id++)
        {
            for (var i = 0; i < Dimensions; i++) vector[i] = random.NextSingle() * 2 - 1;
            index.Add(id, SearchVectorTestData.Normalize(vector), new SearchVectorRowInfo(SearchKind.Message, SearchRole.User, Day.AddMinutes(id), "default", "p" + (id / 5_000), "s" + (id / 500)));
        }

        var query = SearchVectorTestData.RandomUnit(random, Dimensions);
        double Median(SearchScope scope, int parallelism)
        {
            // The threads of the pool are started first, as in a process that has been running.
            for (var i = 0; i < 30; i++) index.Search(query, scope, K, parallelism);
            var times = new double[31];
            for (var i = 0; i < times.Length; i++)
            {
                var started = Stopwatch.GetTimestamp();
                index.Search(query, scope, K, parallelism);
                times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            Array.Sort(times);
            return times[times.Length / 2];
        }

        var all = new SearchScope();
        var project = new SearchScope { ProjectId = "p3", Kinds = new HashSet<SearchKind> { SearchKind.Message } };
        Console.WriteLine($"{Rows:N0} x {Dimensions}, top {K}, kernel {SearchVectorKernels.Name}, {Environment.ProcessorCount} cores, {index.MemoryBytes / (1024.0 * 1024.0):F0} MB");
        Console.WriteLine($"  every row: 1 thread {Median(all, 1):F2} ms, parallel {Median(all, Environment.ProcessorCount):F2} ms");
        Console.WriteLine($"  one project (5%): 1 thread {Median(project, 1):F2} ms, parallel {Median(project, Environment.ProcessorCount):F2} ms");
    }
}

[TestClass]
public sealed class SearchVectorStoreTests
{
    private const int Dimensions = 64;
    private static readonly SearchVectorSetIdentity ModelA = new("model-a", "r1", Dimensions, "p1", "ort-1.28.2");
    private static readonly SearchVectorSetIdentity ModelB = new("model-b", "r1", 32, "p1", "ort-1.28.2");

    private static SearchSourceState Source(string key) => new(key, "1", null, null, 0, null, DateTimeOffset.UtcNow);

    private static SearchDocument Doc(string reference, string text, SearchKind kind = SearchKind.Doc, string? project = null, string? session = null, DateTimeOffset? at = null, string? embeddingTitle = null)
        => new(reference, kind, "Title", text, at ?? DateTimeOffset.UtcNow) { ProjectId = project, SessionId = session, EmbeddingTitle = embeddingTitle };

    private static async Task<int> EmbedAllAsync(SearchIndexStore store, SearchVectorSet set)
    {
        var total = 0;
        while (await store.ListMissingVectorsAsync(set.Id, 7) is { Count: > 0 } work)
        {
            total += await store.PutVectorsAsync(set.Id, work.Select(item => new SearchVectorInput(item.EmbedHash, SearchVectorTestData.VectorOf(item.Text, set.Identity.Dimensions))).ToList());
        }

        return total;
    }

    private static async Task<string?> RefOfAsync(SearchFixture fixture, long chunkId)
        => (await fixture.Store.GetItemsAsync((await fixture.Store.GetChunksAsync([chunkId], default)).Values.Select(static chunk => chunk.ItemId).ToList(), default)).Values.SingleOrDefault()?.Ref;

    [TestMethod]
    public async Task ANewModel_IsBuiltInTheBackground_WhileTheOldOneStaysActive()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });

        var a = await store.OpenVectorSetAsync(ModelA);
        Assert.AreEqual(SearchVectorSetState.Active, a.State, "the first set answers at once, while it fills");
        Assert.AreEqual(a.Id, (await store.OpenVectorSetAsync(ModelA)).Id, "one set per identity");
        Assert.AreEqual(2, await EmbedAllAsync(store, a));
        Assert.AreEqual(new SearchVectorCoverage(2, 2), await store.GetVectorCoverageAsync(a.Id));

        var b = await store.OpenVectorSetAsync(ModelB);
        Assert.AreEqual(SearchVectorSetState.Building, b.State, "another model is built while the first one answers");
        Assert.AreNotEqual(a.Id, b.Id);
        Assert.AreEqual(a.Id, (await store.GetActiveVectorSetAsync())!.Id);
        var missing = await store.ListMissingVectorsAsync(b.Id, 1);
        Assert.HasCount(1, missing);
        await store.PutVectorsAsync(b.Id, [new SearchVectorInput(missing[0].EmbedHash, SearchVectorTestData.VectorOf(missing[0].Text, 32))]);
        Assert.IsFalse(await store.CompleteVectorSetAsync(b.Id), "not while a chunk has no vector in it");
        Assert.AreEqual(a.Id, (await store.GetActiveVectorSetAsync())!.Id);
        await EmbedAllAsync(store, b);
        Assert.IsTrue(await store.CompleteVectorSetAsync(b.Id));

        var sets = await store.GetVectorSetsAsync();
        Assert.AreEqual(SearchVectorSetState.Retired, sets.Single(set => set.Id == a.Id).State);
        Assert.AreEqual(SearchVectorSetState.Active, sets.Single(set => set.Id == b.Id).State);
        Assert.AreEqual(ModelB, sets.Single(set => set.Id == b.Id).Identity);

        var back = await store.OpenVectorSetAsync(ModelA);
        Assert.AreEqual(a.Id, back.Id, "a retired set comes back");
        Assert.AreEqual(SearchVectorSetState.Building, back.State);
        Assert.AreEqual(new SearchVectorCoverage(2, 2), await store.GetVectorCoverageAsync(a.Id), "with its vectors");
        Assert.IsTrue(await store.ActivateVectorSetAsync(a.Id));
        Assert.IsTrue(await store.DeleteVectorSetAsync(b.Id));
        Assert.HasCount(1, await store.GetVectorSetsAsync());
        Assert.IsFalse(await store.DeleteVectorSetAsync(b.Id));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.PutVectorsAsync(a.Id, [new SearchVectorInput([1], new float[3])]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.PutVectorsAsync(b.Id, [new SearchVectorInput([1], new float[32])]));
    }

    [TestMethod]
    public async Task Vectors_AreKeyedByTheirText_AndKeptWhenItemsAreWrittenAgain()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings"), Doc("doc:c", "provider settings")]) { Complete = true });
        var set = await store.OpenVectorSetAsync(ModelA);
        Assert.AreEqual(2, await EmbedAllAsync(store, set), "a text in two chunks is embedded once");
        Assert.AreEqual(new SearchVectorCoverage(3, 3), await store.GetVectorCoverageAsync(set.Id));

        // doc:a changes, doc:b goes, doc:c is written again with the same text.
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup of branches"), Doc("doc:c", "provider settings")]) { Complete = true });
        Assert.AreEqual(new SearchVectorCoverage(2, 1), await store.GetVectorCoverageAsync(set.Id), "the unchanged text keeps its vector");
        Assert.AreEqual(1, await store.CompactVectorsAsync(), "the vector of the old text of doc:a is used by no chunk");
        Assert.AreEqual(0, await store.CompactVectorsAsync());
        Assert.AreEqual(1, await EmbedAllAsync(store, set));
    }

    [TestMethod]
    public async Task ListMissing_GivesTheCatalogFirst_ThenTheNewest_WithTheTitleToEmbed()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        var now = DateTimeOffset.UtcNow;
        await store.ApplyAsync(new SearchSourceUpdate(Source("mixed"),
        [
            Doc("msg:old", "an old prompt that is rather long", SearchKind.Message, at: now.AddDays(-3)),
            Doc("msg:new", "a new prompt", SearchKind.Message, at: now, embeddingTitle: "My session · Alpha"),
            Doc("tool:read_file", "reads a file", SearchKind.Tool, at: DateTimeOffset.UnixEpoch),
        ]) { Complete = true });
        var set = await store.OpenVectorSetAsync(ModelA);

        var first = await store.ListMissingVectorsAsync(set.Id, 1);
        Assert.AreEqual("reads a file", first.Single().Text, "the catalog first: tool search needs it");
        var two = await store.ListMissingVectorsAsync(set.Id, 2);
        Assert.AreEqual("reads a file", two[0].Text, "a page is ordered by length");
        Assert.AreEqual("a new prompt", two[1].Text, "then the newest");
        Assert.AreEqual("My session · Alpha", two[1].Title, "the title the text was hashed with");
        Assert.AreEqual("Title", (await store.ListMissingVectorsAsync(set.Id, 10)).Single(work => work.Text.StartsWith("an old", StringComparison.Ordinal)).Title);

        // A rename changes the titles of the index, not the title the hash of the text was made with.
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s9"), [Doc("msg:s9/1", "a prompt of s9", SearchKind.Message, session: "s9")]) { Complete = true });
        await store.RetitleSessionAsync("s9", "Renamed");
        Assert.AreEqual("Title", (await store.ListMissingVectorsAsync(set.Id, 10)).Single(work => work.Text == "a prompt of s9").Title);
    }

    [TestMethod]
    public async Task LoadVectorIndex_ReadsTheVectorsWithTheColumnsOfTheirItems_AfterAReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "search.sqlite3");
        try
        {
            long setId;
            await using (var store = new SearchIndexStore(path))
            {
                await store.ApplyAsync(new SearchSourceUpdate(Source("plans"),
                [
                    Doc("plan:p1/a", "release checklist", SearchKind.Plan, "p1"),
                    Doc("plan:p2/b", "release notes", SearchKind.Plan, "p2"),
                    Doc("doc:c", "release train", SearchKind.Doc),
                ]) { Complete = true });
                var set = await store.OpenVectorSetAsync(ModelA);
                setId = set.Id;
                await EmbedAllAsync(store, set);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using (var store = new SearchIndexStore(path))
            {
                var index = (await store.LoadVectorIndexAsync(setId))!;
                Assert.AreEqual(3, index.Count);
                Assert.AreEqual(Dimensions, index.Dimensions);
                var query = SearchVectorTestData.VectorOf("release notes", Dimensions);
                var best = index.Search(query, new SearchScope(), 1).Single();
                Assert.AreEqual(1f, best.Similarity, 0.02f);
                var chunks = await store.GetChunksAsync([best.ChunkId], default);
                Assert.AreEqual("release notes", chunks.Single().Value.Text);
                Assert.IsEmpty(index.Search(query, new SearchScope { ProjectId = "p1", Kinds = new HashSet<SearchKind> { SearchKind.Doc } }, 3));
                Assert.HasCount(2, index.Search(query, new SearchScope { Projects = new HashSet<string> { "p1" } }, 3), "a plan of p1 and the doc of no project");
                Assert.IsNull(await store.LoadVectorIndexAsync(setId + 100));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task MeaningIndex_FollowsTheIndex_AddsMovesAndForgetsDeletedSessions()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        await using var meaning = new SearchMeaningIndex(store);
        await meaning.LoadAsync();
        Assert.IsTrue(meaning.IsLoaded);
        Assert.IsNull(meaning.ActiveSet);
        Assert.AreEqual(0, meaning.Coverage);
        Assert.IsEmpty(await meaning.SearchAsync(new float[Dimensions], new SearchScope(), 5));

        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s1"), [Doc("msg:s1/1", "the scroll jumps after a resize", SearchKind.Message, "p1", "s1")]) { Complete = true });
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s2"), [Doc("msg:s2/1", "the scroll jumps after a resize", SearchKind.Message, "p1", "s2")]) { Complete = true });
        var set = await store.OpenVectorSetAsync(ModelA);
        var query = SearchVectorTestData.VectorOf("the scroll jumps after a resize", Dimensions);
        Assert.IsEmpty(await meaning.SearchAsync(query, new SearchScope(), 5), "no vector yet");
        Assert.AreEqual(set.Id, meaning.ActiveSet!.Id, "the set that became active was loaded");
        Assert.AreEqual(0, meaning.Coverage);

        await EmbedAllAsync(store, set);
        var hits = await meaning.SearchAsync(query, new SearchScope(), 5);
        Assert.HasCount(2, hits, "the vector that came gives a row to each chunk of its text");
        Assert.AreEqual(1, meaning.Coverage);

        // A session deleted: its rows vanish from the next answer.
        await fixture.Indexer.ForgetSessionAsync("s1");
        hits = await meaning.SearchAsync(query, new SearchScope(), 5);
        Assert.AreEqual("msg:s2/1", await RefOfAsync(fixture, hits.Single().ChunkId));

        // An item moved to another project, with the same text: its row follows.
        await store.ApplyAsync(new SearchSourceUpdate(Source("journal:s2"), [Doc("msg:s2/1", "the scroll jumps after a resize", SearchKind.Message, "p2", "s2")]) { Complete = true });
        Assert.IsEmpty(await meaning.SearchAsync(query, new SearchScope { ProjectId = "p1" }, 5));
        Assert.HasCount(1, await meaning.SearchAsync(query, new SearchScope { ProjectId = "p2" }, 5));

        // A new text has no row until its vector comes.
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:z", "provider settings")]) { Complete = true });
        Assert.HasCount(1, await meaning.SearchAsync(query, new SearchScope(), 5));
        Assert.AreEqual(0.5, meaning.Coverage, 1e-9);
        await EmbedAllAsync(store, set);
        Assert.HasCount(2, await meaning.SearchAsync(query, new SearchScope(), 5));
        Assert.AreEqual(2, meaning.Count);
    }

    [TestMethod]
    public async Task MeaningIndex_KeepsTheOldModel_UntilTheNewSetIsComplete()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });
        var a = await store.OpenVectorSetAsync(ModelA);
        await EmbedAllAsync(store, a);
        await using var meaning = new SearchMeaningIndex(store);
        await meaning.Start();
        Assert.AreEqual(a.Id, meaning.ActiveSet!.Id);

        var b = await store.OpenVectorSetAsync(ModelB);
        var work = await store.ListMissingVectorsAsync(b.Id, 1);
        await store.PutVectorsAsync(b.Id, [new SearchVectorInput(work[0].EmbedHash, SearchVectorTestData.VectorOf(work[0].Text, 32))]);
        Assert.HasCount(1, await meaning.SearchAsync(SearchVectorTestData.VectorOf("provider settings", Dimensions), new SearchScope(), 1), "the old model answers");
        Assert.AreEqual(a.Id, meaning.ActiveSet!.Id, "the vectors of a set being built are not loaded");
        Assert.AreEqual(2, meaning.Count);

        await EmbedAllAsync(store, b);
        Assert.IsTrue(await store.CompleteVectorSetAsync(b.Id));
        Assert.IsEmpty(await meaning.SearchAsync(SearchVectorTestData.VectorOf("provider settings", Dimensions), new SearchScope(), 1), "a vector of the old model's width finds nothing in the new set");
        Assert.AreEqual(b.Id, meaning.ActiveSet!.Id, "the new set was loaded once complete");
        var hits = await meaning.SearchAsync(SearchVectorTestData.VectorOf("provider settings", 32), new SearchScope(), 1);
        Assert.AreEqual("doc:b", await RefOfAsync(fixture, hits.Single().ChunkId));

        await store.DeleteVectorSetAsync(b.Id);
        Assert.IsEmpty(await meaning.SearchAsync(SearchVectorTestData.VectorOf("provider settings", 32), new SearchScope(), 1));
        Assert.IsNull(meaning.ActiveSet, "no set is active once the active one is deleted");
    }

    [TestMethod]
    public async Task MeaningIndex_LoadsAgain_WhenTheChangesItNeedsWereTrimmed_OrTheIndexWasCleared()
    {
        await using var fixture = new SearchFixture();
        var store = fixture.Store;
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup")]) { Complete = true });
        var set = await store.OpenVectorSetAsync(ModelA);
        await EmbedAllAsync(store, set);
        await using var meaning = new SearchMeaningIndex(store);
        await meaning.LoadAsync();
        Assert.AreEqual(1, meaning.Count);

        // Another reader trims the log past the changes this one has not read.
        await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });
        await EmbedAllAsync(store, set);
        await store.PruneVectorChangesAsync(long.MaxValue / 2, default);
        Assert.HasCount(2, await meaning.SearchAsync(SearchVectorTestData.VectorOf("worktree cleanup", Dimensions), new SearchScope(), 5));

        await store.ClearAsync();
        await meaning.SyncAsync();
        Assert.IsNull(meaning.ActiveSet, "a cleared index has no set");
        Assert.AreEqual(0, meaning.Count);
    }

    [TestMethod]
    public async Task AnIndexWrittenBeforeTheNarrowIndexes_GetsThemAtItsFirstVectorQuery_AndKeepsItsVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "search.sqlite3");
        try
        {
            await using (var store = new SearchIndexStore(path))
            {
                await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup"), Doc("doc:b", "provider settings")]) { Complete = true });
                // The indexes of the chunks as the first builds of version 3 made them, and none of the vectors.
                await store.WriteAsync(async (connection, token) =>
                {
                    await SearchIndexStore.ExecuteAsync(connection, """
                        DROP INDEX search_chunk_of_item; DROP INDEX search_chunk_hash; DROP INDEX search_vector_key;
                        CREATE INDEX search_chunk_item ON search_chunk(item_id); CREATE INDEX search_chunk_embed ON search_chunk(embed_hash);
                        """, token);
                    return 0;
                }, default);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using (var store = new SearchIndexStore(path))
            {
                var set = await store.OpenVectorSetAsync(ModelA);
                Assert.AreEqual(2, await EmbedAllAsync(store, set));
                Assert.AreEqual(new SearchVectorCoverage(2, 2), await store.GetVectorCoverageAsync(set.Id));
                Assert.IsTrue(await store.CompleteVectorSetAsync(set.Id));
                Assert.AreEqual(2, (await store.LoadVectorIndexAsync(set.Id))!.Count);
                var (indexes, version) = await store.ReadAsync(async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name IN ('search_chunk', 'search_vector') AND sql IS NOT NULL ORDER BY name";
                    var names = new List<string>();
                    await using (var reader = await command.ExecuteReaderAsync(token))
                    {
                        while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
                    }

                    command.CommandText = $"SELECT version FROM app_meta WHERE owner = '{SearchIndexStore.Owner}'";
                    return (names, Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture));
                }, default);
                CollectionAssert.AreEqual(new[] { "search_chunk_hash", "search_chunk_of_item", "search_vector_key" }, indexes);
                Assert.AreEqual(3, version, "an older build opens the file as it is");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task Open_MakesAVersion1IndexAgain_WithTheColumnsOfVersion2()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-search-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "search.sqlite3");
        Directory.CreateDirectory(root);
        try
        {
            await using (var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path, BackupDirectory = Path.Combine(root, "backups") }))
            {
                await database.MigrateAsync(SearchIndexStore.Owner, "search_", 1, static async (connection, _, _, token) =>
                {
                    await SearchIndexStore.ExecuteAsync(connection, """
                        CREATE TABLE search_source (id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE);
                        INSERT INTO search_source(key) VALUES ('journal:old');
                        CREATE TABLE search_item (id INTEGER PRIMARY KEY);
                        CREATE TABLE search_chunk (id INTEGER PRIMARY KEY, title TEXT, text TEXT, words TEXT);
                        CREATE VIRTUAL TABLE search_fts USING fts5(title, text, words, content='search_chunk', content_rowid='id');
                        CREATE TABLE search_vector_set (id INTEGER PRIMARY KEY);
                        CREATE TABLE search_vector (set_id INTEGER NOT NULL, embed_hash BLOB NOT NULL, PRIMARY KEY (set_id, embed_hash)) WITHOUT ROWID;
                        CREATE TABLE search_activity (at INTEGER NOT NULL);
                        CREATE TABLE search_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                        """, token);
                });
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var store = new SearchIndexStore(path);
            Assert.IsEmpty(await store.ListSourcesAsync(""), "the old cursors are gone, so everything is read again");
            await store.ApplyAsync(new SearchSourceUpdate(Source("docs"), [Doc("doc:a", "worktree cleanup", embeddingTitle: "Guide · Worktrees")]) { Complete = true });
            var set = await store.OpenVectorSetAsync(ModelA);
            Assert.AreEqual("Guide · Worktrees", (await store.ListMissingVectorsAsync(set.Id, 1)).Single().Title);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

internal static class SearchVectorTestData
{
    public static float[] RandomUnit(Random random, int dimensions)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++) vector[i] = random.NextSingle() * 2 - 1;
        return Normalize(vector);
    }

    public static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(DotF(vector, vector));
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    // A unit vector whose similarity with a unit vector is exactly 1 / sqrt(1 + distance²): it moves away along an orthogonal direction.
    public static float[] Near(float[] vector, Random random, float distance)
    {
        var other = RandomUnit(random, vector.Length);
        var along = DotF(other, vector);
        var orthogonal = Normalize(other.Zip(vector, (o, v) => o - along * v).ToArray());
        return Normalize(vector.Zip(orthogonal, (v, o) => v + distance * o).ToArray());
    }

    public static float DotF(float[] left, float[] right)
    {
        var sum = 0f;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    // A stand-in for a model: the same text always gives the same unit vector, and two texts nearly orthogonal ones.
    public static float[] VectorOf(string text, int dimensions)
    {
        var seed = 17;
        foreach (var character in text) seed = unchecked(seed * 31 + character);
        return RandomUnit(new Random(seed), dimensions);
    }

    public static SearchVectorRowInfo Info(SearchKind kind) => new(kind, SearchRole.None, DateTimeOffset.UnixEpoch, null, null, null);
}
