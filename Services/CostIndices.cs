using System.Globalization;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>The cost indices jobs are priced with, and which of them were entered by hand.</summary>
/// <param name="Values">Per system and activity: ESI's index, else the hand-entered one. A pair
/// neither source knows is absent — not zero, which is a real index.</param>
/// <param name="Manual">The pairs <paramref name="Values"/> took from a hand-entered value, and
/// when it was entered.</param>
public sealed record EffectiveCostIndices(
    Dictionary<(int SystemId, string Activity), double> Values,
    Dictionary<(int SystemId, string Activity), DateTimeOffset> Manual);

/// <summary>
/// System cost indices as everything prices with them: ESI's, and a hand-entered value only where
/// ESI has none — every wormhole, since ESI publishes no index for one.
///
/// <para>⚠️ One rule, read here by the build costs, the Production Calculator (and through its
/// context the science fees), and the System Index tool and its reports. Two copies of a pricing
/// rule is how the rig matching came to disagree with itself.</para>
/// </summary>
public static class CostIndices
{
    /// <param name="systems">Only these systems; null for all of them.</param>
    public static async Task<EffectiveCostIndices> LoadAsync(
        AppDbContext db, IReadOnlyCollection<int>? systems = null, CancellationToken ct = default)
    {
        var esi    = db.IndustryCostIndices.AsNoTracking();
        var manual = db.ManualCostIndices.AsNoTracking();
        if (systems is not null)
        {
            esi    = esi.Where(i => systems.Contains(i.SolarSystemId));
            manual = manual.Where(m => systems.Contains(m.SolarSystemId));
        }

        var values = new Dictionary<(int SystemId, string Activity), double>();
        foreach (var i in await esi.ToListAsync(ct))
            values[(i.SolarSystemId, i.Activity)] = i.CostIndex;

        var entered = new Dictionary<(int SystemId, string Activity), DateTimeOffset>();
        foreach (var m in await manual.ToListAsync(ct))
            if (values.TryAdd((m.SolarSystemId, m.Activity), m.CostIndex))
                entered[(m.SolarSystemId, m.Activity)] = m.UpdatedAt;

        return new EffectiveCostIndices(values, entered);
    }

    /// <summary>
    /// Writes one system's hand-entered values: a fraction to keep, null to remove. Every value is
    /// checked first, so one bad value writes nothing; an unchanged value keeps the date it was
    /// entered, which is how the user tells an old figure from a fresh one.
    /// </summary>
    /// <param name="fractions">Activity (as ESI names it) → index as a fraction, 0.0125 for 1.25%.</param>
    /// <exception cref="ArgumentException">An activity ESI has no index for, or a value outside 0–1.</exception>
    public static async Task SaveAsync(
        AppDbContext db, int systemId, IReadOnlyDictionary<string, double?> fractions, CancellationToken ct = default)
    {
        foreach (var (activity, value) in fractions)
        {
            if (!CostIndexHistoryService.Activities.Contains(activity))
                throw new ArgumentException($"ESI has no cost index for the activity \"{activity}\".", nameof(fractions));
            if (value is { } v && !(v >= 0 && v <= 1))
                throw new ArgumentOutOfRangeException(nameof(fractions), v, $"A cost index is a fraction from 0 to 1; {activity} was {v}.");
        }

        var activities = fractions.Keys.ToList();
        var rows = await db.ManualCostIndices
            .Where(m => m.SolarSystemId == systemId && activities.Contains(m.Activity))
            .ToDictionaryAsync(m => m.Activity, ct);
        var now = DateTimeOffset.UtcNow;   // UTC: PostgreSQL stores timestamptz only at offset 0

        foreach (var (activity, value) in fractions)
        {
            rows.TryGetValue(activity, out var row);
            if (value is not { } v)
            {
                if (row is not null) db.ManualCostIndices.Remove(row);
            }
            else if (row is null)
                db.ManualCostIndices.Add(new ManualCostIndex { SolarSystemId = systemId, Activity = activity, CostIndex = v, UpdatedAt = now });
            else if (row.CostIndex != v)
            {
                row.CostIndex = v;
                row.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads a percentage as a person typed it, in the interface's number format: "1.25", or
    /// "1,25" where decimals take a comma, is 0.0125. Blank reads as null — no value, which is
    /// not zero. False for anything that is not a number from 0 to 100.
    /// </summary>
    public static bool TryReadPercent(string? text, out double? fraction)
    {
        fraction = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!NumberText.TryParse(text, out double percent) || !(percent >= 0 && percent <= 100)) return false;
        fraction = percent / 100;
        return true;
    }

    /// <summary>A fraction as an editable percentage in the interface's number format: 0.0125 is
    /// "1.25". <see cref="TryReadPercent"/> reads it back to the same value.</summary>
    public static string PercentText(double fraction) =>
        (fraction * 100).ToString("0.####", CultureInfo.CurrentCulture);

    /// <summary>
    /// The pairs these jobs needed an index for and no source had, once each. A job whose
    /// structure's system is not known (system 0) is not counted: that is a park to fix, not an
    /// index to enter. A zero on record is an index, and not missing.
    /// </summary>
    /// <param name="lookup">System → activity → index, as a plan's context holds them.</param>
    public static List<MissingCostIndex> Missing(
        IEnumerable<PlanJob> jobs, Dictionary<int, Dictionary<string, double>> lookup) =>
        [.. jobs.Where(j => j.CostIndexSystemId > 0
                         && !(lookup.TryGetValue(j.CostIndexSystemId, out var byActivity)
                              && byActivity.ContainsKey(j.CostIndexActivity)))
                .DistinctBy(j => (j.CostIndexSystemId, j.CostIndexActivity))
                .Select(j => new MissingCostIndex(j.CostIndexSystemId, j.SystemName, j.CostIndexActivity))];
}
