using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The plan linework, one entry per opening marker or stair, rebuilt only
/// for what changed. The document events say which object changed; this
/// says what has to be drawn again:
/// a marker or a stair is itself; a wall is every opening it hosts (the
/// symbol spans the wall's real faces); a floor moves the plan cut, so it is
/// everything; a wall, floor or roof moves the elevation ground line.
/// Openings and stairs are also the solids a plan view does not draw: their
/// symbols stand in for them, as on the sheet.
/// </summary>
public sealed class ForskTechnicalCache<T>
{
    sealed class Entry
    {
        public Guid Host;
        public T Value;
    }

    readonly Dictionary<Guid, Entry> _entries = new Dictionary<Guid, Entry>();
    readonly HashSet<Guid> _dirty = new HashSet<Guid>();
    readonly HashSet<Guid> _hidden = new HashSet<Guid>();

    /// <summary>Every entry is stale: a new document, a floor, another profile.</summary>
    public bool AllDirty { get; private set; } = true;
    /// <summary>The elevation ground line and the model extent are stale.</summary>
    public bool GroundDirty { get; private set; } = true;
    /// <summary>Bumped on every put or remove, so a view knows to redraw.</summary>
    public int Version { get; private set; }
    public string Context { get; private set; } = "";
    public int Count => _entries.Count;

    public static bool Draws(string kind)
    {
        return Is(kind, "opening_marker") || Is(kind, Stairs.Kind);
    }

    public static bool Hides(string kind)
    {
        return Is(kind, "opening") || Is(kind, Stairs.Kind);
    }

    public static bool Grounds(string kind)
    {
        return Is(kind, "wall") || Is(kind, "floor") || Is(kind, "roof");
    }

    /// <summary>A kind this cache does nothing for is skipped before its host is read.</summary>
    public static bool Matters(string kind)
    {
        return Draws(kind) || Hides(kind) || Grounds(kind);
    }

    /// <summary>One object added, changed, undeleted (gone false) or deleted (gone true).</summary>
    public void Changed(Guid id, string kind, Guid host, bool gone)
    {
        if (id == Guid.Empty) return;
        if (Hides(kind))
        {
            if (gone) _hidden.Remove(id);
            else _hidden.Add(id);
        }
        if (Draws(kind)) _dirty.Add(id);
        if (Is(kind, "wall"))
        {
            foreach (var pair in _entries)
                if (pair.Value.Host == id) _dirty.Add(pair.Key);
        }
        if (Is(kind, "floor")) AllDirty = true;
        if (Grounds(kind)) GroundDirty = true;
    }

    /// <summary>The profile (and anything else every piece is drawn with). A change makes every entry stale.</summary>
    public void SetContext(string context)
    {
        context = context ?? "";
        if (string.Equals(context, Context, StringComparison.Ordinal)) return;
        Context = context;
        AllDirty = true;
    }

    /// <summary>Forget everything: a new or another document.</summary>
    public void Reset()
    {
        _entries.Clear();
        _dirty.Clear();
        _hidden.Clear();
        AllDirty = true;
        GroundDirty = true;
        Context = "";
        Version++;
    }

    /// <summary>Start a full rebuild: entries and the hidden set are refilled from the document.</summary>
    public void BeginAll()
    {
        _entries.Clear();
        _dirty.Clear();
        _hidden.Clear();
        AllDirty = false;
        Version++;
    }

    /// <summary>The ids to build again, once. Empty while a full rebuild is due.</summary>
    public List<Guid> TakeDirty()
    {
        if (AllDirty) return new List<Guid>();
        var ids = _dirty.ToList();
        _dirty.Clear();
        return ids;
    }

    public bool HasDirty => AllDirty || _dirty.Count > 0;

    public void GroundDone()
    {
        GroundDirty = false;
    }

    public void Hide(Guid id)
    {
        if (id != Guid.Empty) _hidden.Add(id);
    }

    public bool IsHidden(Guid id)
    {
        return _hidden.Contains(id);
    }

    public void Put(Guid id, Guid host, T value)
    {
        if (id == Guid.Empty) return;
        _entries[id] = new Entry { Host = host, Value = value };
        Version++;
    }

    public void Remove(Guid id)
    {
        if (_entries.Remove(id)) Version++;
    }

    public bool TryGet(Guid id, out T value)
    {
        value = default;
        if (!_entries.TryGetValue(id, out var entry)) return false;
        value = entry.Value;
        return true;
    }

    public IEnumerable<T> Values
    {
        get
        {
            foreach (var entry in _entries.Values) yield return entry.Value;
        }
    }

    static bool Is(string kind, string want)
    {
        return string.Equals(kind, want, StringComparison.OrdinalIgnoreCase);
    }
}
