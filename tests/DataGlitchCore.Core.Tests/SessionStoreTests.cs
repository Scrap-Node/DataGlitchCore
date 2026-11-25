namespace DataGlitchCore.Core.Tests;

/// <summary>Le store commun des sessions, ses budgets et ses limites (spec §8, lot 4).</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dgc-store-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SessionStore<FakeSession> Store(int maxOpen = 3, long budget = 1_000_000, TimeSpan? idle = null) =>
        new(new StoreOptions(_root, maxOpen, idle ?? TimeSpan.FromMinutes(30), budget, TimeSpan.FromDays(30)), _time);

    private FakeSession Open(SessionStore<FakeSession> store, long bytes = 0, long cache = 0)
    {
        var (id, dir) = store.CreateDirectory();
        File.WriteAllText(Path.Combine(dir, "source.bin"), "x");
        var session = store.Add(new FakeSession(id, dir) { Bytes = bytes, Cache = cache });
        _time.Advance(TimeSpan.FromSeconds(1));
        return session;
    }

    [Fact]
    public void Beyond_MaxOpenSessions_The_Least_Recently_Used_Is_Closed_And_Its_Directory_Deleted()
    {
        using var store = Store(maxOpen: 2);
        var a = Open(store);
        var b = Open(store);
        store.Get(a.Id); // a redevient la plus récente
        _time.Advance(TimeSpan.FromSeconds(1));
        var c = Open(store);

        Assert.Equal(2, store.Count);
        Assert.True(b.Disposed);
        Assert.False(Directory.Exists(b.Directory));
        Assert.Null(store.Get(b.Id));
        Assert.Same(a, store.Get(a.Id));
        Assert.Same(c, store.Get(c.Id));
    }

    [Fact]
    public void Closing_A_Session_Deletes_Its_Directory_Before_Lot_5()
    {
        using var store = Store();
        var a = Open(store);
        Assert.True(store.Close(a.Id));
        Assert.True(a.Disposed);
        Assert.False(Directory.Exists(a.Directory));
        Assert.False(store.Close(a.Id));
        Assert.False(store.Delete(a.Id));
    }

    [Fact]
    public void An_Import_Frees_Caches_First_Then_Closes_The_Oldest_Sessions()
    {
        using var store = Store(budget: 1000);
        var old = Open(store, bytes: 300, cache: 100);
        var recent = Open(store, bytes: 300, cache: 100);

        // 800 o pris : 200 de libres, 400 demandés ; les caches donnent 200, le plus ancien d'abord
        Assert.True(store.TryReserve(400));
        Assert.Equal(0, old.Cache);
        Assert.Equal(0, recent.Cache);
        Assert.False(old.Disposed);

        // 600 o pris : 700 demandés ; il faut fermer la plus ancienne
        Assert.True(store.TryReserve(700));
        Assert.True(old.Disposed);
        Assert.False(recent.Disposed);

        // plus que le budget entier : impossible, tout a été tenté
        Assert.False(store.TryReserve(1001));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_Pass_Only_Frees_Caches_And_Never_Closes_Another_Session()
    {
        using var store = Store(budget: 1000);
        var other = Open(store, bytes: 400, cache: 100);
        var mine = Open(store, bytes: 300, cache: 100);

        Assert.True(store.TryReserve(150, requester: mine.Id));
        Assert.Equal(0, other.Cache);     // les caches des autres d'abord…
        Assert.Equal(100, mine.Cache);    // …les siens en dernier

        Assert.False(store.TryReserve(500, requester: mine.Id));
        Assert.False(other.Disposed);
        Assert.Equal(2, store.Count);
        Assert.Equal(0, mine.Cache);
    }

    [Fact]
    public void Sweep_Closes_Idle_Sessions_And_Trims_Caches_Over_Budget_Without_Closing_For_It()
    {
        using var store = Store(budget: 500, idle: TimeSpan.FromMinutes(10));
        var idle = Open(store, bytes: 100);
        _time.Advance(TimeSpan.FromMinutes(11));
        var a = Open(store, bytes: 300, cache: 50);
        var b = Open(store, bytes: 300, cache: 50);

        Assert.Equal(1, store.Sweep());
        Assert.True(idle.Disposed);
        // 700 o pour un budget de 500 : les caches partent, pas les sessions
        Assert.Equal(0, a.Cache);
        Assert.Equal(0, b.Cache);
        Assert.False(a.Disposed);
        Assert.False(b.Disposed);
    }

    [Fact]
    public void The_Budget_Of_A_Session_Is_What_The_Others_Leave()
    {
        using var store = Store(budget: 1000);
        var a = Open(store, bytes: 300);
        var b = Open(store, bytes: 200);
        Assert.Equal(800, store.BudgetFor(a.Id));
        Assert.Equal(700, store.BudgetFor(b.Id));
        Assert.Equal(500, store.BudgetFor("une session à venir"));
    }

    [Fact]
    public void Settings_Change_For_What_Follows_But_Not_The_Root()
    {
        using var store = Store(maxOpen: 3);
        var a = Open(store);
        Open(store);
        store.Options = store.Options with { MaxOpenSessions = 1 };
        Open(store);
        Assert.Equal(1, store.Count);
        Assert.True(a.Disposed);

        Assert.Throws<ArgumentException>(() => store.Options = store.Options with { Root = _root + "-ailleurs" });
        Assert.ThrowsAny<ArgumentException>(() => store.Options = store.Options with { MaxOpenSessions = 0 });
    }

    [Fact]
    public void Limits_Take_The_Smallest_Of_Format_Setting_Disk_And_Budget()
    {
        Assert.Equal(100, Limits.Describe(100, 200, 300, 400).MaxFileBytes);
        Assert.Equal(100, Limits.Describe(400, 100, 300, 200).MaxFileBytes);
        Assert.Equal(100, Limits.Describe(400, 300, 100, 200).MaxFileBytes);
        Assert.Equal(100, Limits.Describe(400, 300, 200, 100).MaxFileBytes);
        var limits = Limits.Describe(4L << 30, 1L << 30, 50L << 30, StoreOptions.DefaultDiskBudgetBytes);
        Assert.Equal(new LimitsDto(1L << 30, 4L << 30, 1L << 30, 50L << 30, StoreOptions.DefaultDiskBudgetBytes), limits);

        using var store = Store(budget: 5000);
        var fromStore = store.Limits(1 << 20, 4000);
        Assert.Equal(4000, fromStore.MaxFileBytes);
        Assert.True(fromStore.DiskFreeBytes > 0);
    }

    [Fact]
    public void Stopping_The_Store_Closes_Every_Session_And_Deletes_The_Root()
    {
        var store = Store();
        var a = Open(store);
        var b = Open(store);
        store.Dispose();
        Assert.True(a.Disposed && b.Disposed);
        Assert.False(Directory.Exists(_root));
    }

    // ---- avec de vrais buffers : le budget global passe par les journaux ----

    [Fact]
    public async Task A_Pass_Is_Refused_When_The_Other_Sessions_Leave_Too_Little_Of_The_Global_Budget()
    {
        var data = new byte[64 * 1024];
        // 8 fois le fichier pour deux sessions : chacune garde l'original, ses passes, et la place d'une base
        using var store = new SessionStore<BufferSession>(StoreOptions.Defaults(_root) with { DiskBudgetBytes = 8 * data.Length }, _time);
        var big = AddBuffer(store, data);
        var small = AddBuffer(store, data);
        var options = new DestructionOptions { Seed = 1, Density = 1, BlockSize = 64, Operator = DestructionOperator.Fixed, FixedValue = 3 };

        // assez de place : la passe passe
        small.Journal.Apply(DestructionEngine.BuildBatch(small.Buffer, options));

        // l'autre session grossit : sous le budget global, elle fusionne pour garder sa marge
        for (int i = 0; i < 4; i++)
            big.Journal.Apply(DestructionEngine.BuildBatch(big.Buffer, new DestructionOptions { Seed = i, Density = 1, BlockSize = 64, FixedValue = (byte)i, Operator = DestructionOperator.Fixed }));
        await big.Journal.WaitForMergesAsync();
        Assert.True(big.Journal.MergedPasses > 0);

        // une passe d'octets isolés (estimée à deux fois le fichier) ne tient plus dans ce qu'elle laisse
        var scattered = new DestructionOptions { Seed = 2, Density = 1, BlockSize = 1, Operator = DestructionOperator.Fixed, FixedValue = 5 };
        var refused = Assert.Throws<DiskBudgetExceededException>(() => DestructionEngine.BuildBatch(small.Buffer, scattered));
        Assert.Contains("Rien n'a été modifié", refused.Message);
        // ce qu'il faudrait : la session, la passe estimée, et la place de réécrire une base
        long needed = small.Journal.DiskBytes + DestructionEngine.EstimateSegmentBytes(small.Buffer, scattered, DestructionMask.WholeBuffer)
                    + BaseLayer.DenseBytes(small.Buffer);
        Assert.Equal(needed, refused.NeededBytes);
        Assert.Equal(store.BudgetFor(small.Id), refused.BudgetBytes);
        Assert.True(refused.BudgetBytes < needed);
        Assert.Single(small.Journal.History);
    }

    [Fact]
    public async Task Two_Sessions_Applying_Passes_At_Once_Under_A_Global_Budget_Do_Not_Deadlock()
    {
        var data = new byte[64 * 1024];
        using var store = new SessionStore<BufferSession>(StoreOptions.Defaults(_root) with { DiskBudgetBytes = 100 * data.Length }, _time);
        var a = AddBuffer(store, data, maxUndo: 1);
        var b = AddBuffer(store, data, maxUndo: 1);

        async Task Run(BufferSession s)
        {
            await Task.Yield();
            for (int i = 0; i < 30; i++)
            {
                var options = new DestructionOptions { Seed = i, Density = 0.3, BlockSize = 16, Operator = DestructionOperator.Xor, XorMask = 0x5A };
                s.Journal.Apply(DestructionEngine.BuildBatch(s.Buffer, options));
            }
            await s.Journal.WaitForMergesAsync();
        }

        await Task.WhenAll(Task.Run(() => Run(a)), Task.Run(() => Run(b))).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(29, a.Journal.MergedPasses);
        Assert.Equal(29, b.Journal.MergedPasses);
    }

    private BufferSession AddBuffer(SessionStore<BufferSession> store, byte[] data, int maxUndo = 20)
    {
        var (id, dir) = store.CreateDirectory();
        string source = Path.Combine(dir, "source.bin");
        File.WriteAllBytes(source, data);
        var buffer = FileBuffer.Open(source, dir, new FileBufferOptions { ChunkShift = 12, Durable = false });
        var session = new BufferSession(id, dir, buffer);
        session.Journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = maxUndo, DiskBudget = () => store.BudgetFor(id) });
        return store.Add(session);
    }

    // ---- doublures ----

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class FakeSession(string id, string directory) : IStoredSession
    {
        public string Id { get; } = id;
        public string Directory { get; } = directory;
        public long LastUse { get; set; }
        public long Bytes { get; set; }
        public long Cache { get; set; }
        public bool Disposed { get; private set; }
        public long DiskBytes => Bytes + Cache;

        public long DropCaches()
        {
            long freed = Cache;
            Cache = 0;
            return freed;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class BufferSession(string id, string directory, FileBuffer buffer) : IStoredSession
    {
        public string Id { get; } = id;
        public string Directory { get; } = directory;
        public long LastUse { get; set; }
        public FileBuffer Buffer { get; } = buffer;
        public OperationJournal Journal { get; set; } = null!;
        public long DiskBytes => Journal.DiskBytes;
        public long DropCaches() => 0;
        public void Dispose() => Buffer.Dispose();
    }
}

public sealed class SessionSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Settings_Survive_A_Restart()
    {
        string path = Path.Combine(_dir, "prefs", "settings.json");
        var settings = new SessionSettings(1L << 30, 5_000_000_000, 7, TimeSpan.FromDays(3));
        await settings.SaveAsync(path);
        Assert.Equal(settings, SessionSettings.Load(path, SessionSettings.Defaults(1)));
    }

    [Fact]
    public void Missing_Corrupt_Or_Invalid_Settings_Fall_Back_To_The_Defaults()
    {
        var defaults = SessionSettings.Defaults(4L << 30);
        Assert.Equal(StoreOptions.DefaultDiskBudgetBytes, defaults.DiskBudgetBytes);
        Assert.Equal(20, defaults.MaxUndoSteps);
        Assert.Equal(TimeSpan.FromDays(30), defaults.Retention);

        string path = Path.Combine(_dir, "settings.json");
        Assert.Equal(defaults, SessionSettings.Load(path, defaults));
        File.WriteAllText(path, "{ pas du json");
        Assert.Equal(defaults, SessionSettings.Load(path, defaults));
        File.WriteAllText(path, """{ "maxFileBytes": 100, "diskBudgetBytes": 100, "maxUndoSteps": 0, "retention": "1.00:00:00" }""");
        Assert.Equal(defaults, SessionSettings.Load(path, defaults));
    }

    [Fact]
    public void Out_Of_Range_Settings_Are_Refused_With_A_Reason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings(0, 1, 1, TimeSpan.Zero).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings(1, -1, 1, TimeSpan.Zero).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings(1, 1, 0, TimeSpan.Zero).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings(1, 1, 1, TimeSpan.FromDays(-1)).Validate());
    }
}
