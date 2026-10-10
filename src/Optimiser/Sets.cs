using System.Text.Json;

namespace Optimiser;

// A named group of optimisations, like Gaming or Everyday, that switches on and off together.
public record OptimisationSet(string Name, List<string> Items);

// A set as the Optimisations page shows it: its optimisations that are on this PC, with their current state.
public record SetRow(string Name, List<Row> Rows)
{
    public bool On => Rows.Count > 0 && Rows.All(r => r.On);
    public string Summary => Rows.Count == 0 ? "None of its optimisations are on this PC."
                           : $"{Rows.Count} optimisation{(Rows.Count == 1 ? "" : "s")}: {string.Join(", ", Rows.Select(r => r.Name))}";
}

public static class Sets
{
    const string Setting = "OptimisationSets";

    public static List<OptimisationSet> Load()
    {
        try { return JsonSerializer.Deserialize<List<OptimisationSet>>(Settings.GetText(Setting) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    // Saving under a name that's already there updates that set, keeping its place.
    public static void Save(string name, List<string> items)
    {
        var sets = Load();
        var i = sets.FindIndex(s => Same(s.Name, name));
        if (i >= 0) sets[i] = new(name, items);
        else sets.Add(new(name, items));
        Store(sets);
    }

    public static void Delete(string name) => Store([.. Load().Where(s => !Same(s.Name, name))]);

    // Switching a set on turns on whatever in it is off. Switching it off turns off what's on, except what another
    // set that's on still has, and what was already set that way in Windows (there's no original to put back).
    public static List<Row> ToFlip(SetRow set, IEnumerable<SetRow> all, bool on)
    {
        if (on) return [.. set.Rows.Where(r => !r.On && r.CanSwitch)];
        var kept = all.Where(s => s.Name != set.Name && s.On).SelectMany(s => s.Rows).Select(r => r.Name).ToHashSet();
        return [.. set.Rows.Where(r => r.On && r.CanSwitch && !kept.Contains(r.Name))];
    }

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    static void Store(List<OptimisationSet> sets) => Settings.SetText(Setting, JsonSerializer.Serialize(sets));
}
