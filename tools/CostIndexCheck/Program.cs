using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  Cost index check
//
//  Checks which system cost index a job is priced with, on a throwaway database.
//
//  WHY. ESI publishes no index for wormholes, so those come from values entered by hand. The
//  rule is small and nothing shows when it is wrong: a hand-entered value that beat ESI, or a
//  missing index read as a real zero, is a job fee that looks finished either way.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠️ SQLite, pinned, before anything reads the engine: never a developer machine's PostgreSQL.
DbEngine.Pin(DbBackend.Sqlite);

var failures = new List<string>();
var checks   = 0;

void Check(string name, bool ok, string detail = "")
{
    checks++;
    if (!ok) failures.Add($"  FAIL  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
}

const int Jita     = 30000142;   // known space: ESI publishes its indices
const int Wormhole = 31000100;   // J214534: ESI publishes none
var entered = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

var dbPath = Path.Combine(Path.GetTempPath(), $"eveconsole-costindex-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={dbPath};Pooling=False").Options;
    AppDbContext Open() => new(options);

    await using (var db = Open())
    {
        db.Database.EnsureCreated();
        db.IndustryCostIndices.AddRange(
            new IndustryCostIndex { SolarSystemId = Jita, Activity = "manufacturing", CostIndex = 0.05 },
            new IndustryCostIndex { SolarSystemId = Jita, Activity = "reaction",      CostIndex = 0.0 });
        db.ManualCostIndices.AddRange(
            new ManualCostIndex { SolarSystemId = Jita,     Activity = "manufacturing", CostIndex = 0.09,   UpdatedAt = entered },
            new ManualCostIndex { SolarSystemId = Jita,     Activity = "reaction",      CostIndex = 0.07,   UpdatedAt = entered },
            new ManualCostIndex { SolarSystemId = Wormhole, Activity = "manufacturing", CostIndex = 0.0125, UpdatedAt = entered },
            new ManualCostIndex { SolarSystemId = Wormhole, Activity = "reaction",      CostIndex = 0.0,    UpdatedAt = entered });
        await db.SaveChangesAsync();
    }

    async Task<EffectiveCostIndices> Load(IReadOnlyCollection<int>? systems = null)
    {
        await using var db = Open();
        return await CostIndices.LoadAsync(db, systems);
    }

    // ── Which source a pair is priced from ──

    var all = await Load();
    Check("ESI wins over a hand-entered value",
        all.Values.GetValueOrDefault((Jita, "manufacturing")) == 0.05 && !all.Manual.ContainsKey((Jita, "manufacturing")),
        all.Values.GetValueOrDefault((Jita, "manufacturing")).ToString());
    Check("ESI wins at zero too",
        all.Values.TryGetValue((Jita, "reaction"), out var esiZero) && esiZero == 0 && !all.Manual.ContainsKey((Jita, "reaction")),
        esiZero.ToString());
    Check("no ESI pair: the hand-entered value",
        all.Values.GetValueOrDefault((Wormhole, "manufacturing")) == 0.0125 && all.Manual.ContainsKey((Wormhole, "manufacturing")));
    Check("a hand-entered zero is a value, and says where it came from",
        all.Values.TryGetValue((Wormhole, "reaction"), out var manualZero) && manualZero == 0
        && all.Manual.GetValueOrDefault((Wormhole, "reaction")) == entered);
    Check("neither source: no index at all, not a zero", !all.Values.ContainsKey((Wormhole, "invention")));

    var one = await Load([Wormhole]);
    Check("only the systems asked for",
        one.Values.Count == 2 && one.Values.Keys.All(k => k.SystemId == Wormhole) && one.Manual.Keys.All(k => k.SystemId == Wormhole),
        string.Join(", ", one.Values.Keys));

    // ── What a plan had no index for ──

    var lookup = new Dictionary<int, Dictionary<string, double>> { [Wormhole] = new() { ["reaction"] = 0.0 } };
    PlanJob Job(int system, string activity) => new()
    {
        CostIndexSystemId = system, CostIndexActivity = activity, SystemName = system == Wormhole ? "J214534" : "Jita",
    };
    var missing = CostIndices.Missing(
    [
        Job(0, "manufacturing"),      // the structure's system is not known: not a missing index
        Job(Wormhole, "reaction"),    // a zero on record is an index
        Job(Wormhole, "invention"),
        Job(Wormhole, "invention"),   // two jobs, one pair
        Job(Jita, "copying"),
    ], lookup);
    Check("missing: only the pairs no source has, each once, with the system's name",
        missing.Count == 2
        && missing.Any(m => m.SystemId == Wormhole && m.Activity == "invention" && m.SystemName == "J214534")
        && missing.Any(m => m.SystemId == Jita && m.Activity == "copying"),
        string.Join(", ", missing));

    // ── Entering values by hand ──

    async Task Save(Dictionary<string, double?> fractions)
    {
        await using var db = Open();
        await CostIndices.SaveAsync(db, Wormhole, fractions);
    }

    var beforeSave = DateTimeOffset.UtcNow;
    await Save(new() { ["manufacturing"] = 0.0125, ["invention"] = 0.02, ["reaction"] = null });
    var saved = await Load([Wormhole]);
    Check("saving: an unchanged value keeps the date it was entered",
        saved.Values.GetValueOrDefault((Wormhole, "manufacturing")) == 0.0125
        && saved.Manual.GetValueOrDefault((Wormhole, "manufacturing")) == entered,
        saved.Manual.GetValueOrDefault((Wormhole, "manufacturing")).ToString("O"));
    Check("saving: a new value is kept as given, dated now",
        saved.Values.GetValueOrDefault((Wormhole, "invention")) == 0.02
        && saved.Manual.GetValueOrDefault((Wormhole, "invention")) >= beforeSave);
    Check("saving: a blank removes the value", !saved.Values.ContainsKey((Wormhole, "reaction")));

    (string Name, Dictionary<string, double?> Fractions)[] refused =
    [
        ("above 100%",                      new() { ["invention"] = 1.5 }),
        ("below zero",                      new() { ["invention"] = -0.01 }),
        ("not a number",                    new() { ["invention"] = double.NaN }),
        ("an activity ESI has no index for", new() { ["duplicating"] = 0.01 }),
        ("one bad value among good ones",    new() { ["copying"] = 0.01, ["invention"] = 2.0 }),
    ];
    foreach (var (name, fractions) in refused)
    {
        var threw = false;
        try { await Save(fractions); }
        catch (ArgumentException) { threw = true; }
        var after = await Load([Wormhole]);
        Check($"saving refuses {name}, and writes nothing",
            threw && after.Values.GetValueOrDefault((Wormhole, "invention")) == 0.02
                  && !after.Values.ContainsKey((Wormhole, "copying")) && after.Values.Count == 2,
            $"threw={threw}, {string.Join(", ", after.Values)}");
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(dbPath); } catch (IOException) { /* a temp file, and the run's result is what matters */ }
}

// ── Result ────────────────────────────────────────────────────────────────────

if (failures.Count > 0)
{
    Console.WriteLine($"Cost index check: {failures.Count} of {checks} FAILED");
    foreach (var f in failures) Console.WriteLine(f);
    return 1;
}

Console.WriteLine($"Cost index check: all {checks} passed.");
return 0;
