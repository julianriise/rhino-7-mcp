using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Exclusive milliseconds for one Generate 3D pass. A nested phase takes its
/// own time out of the phase it sits in, so the numbers sum to the work.
/// The clock returns milliseconds. The real pass uses a stopwatch.
/// </summary>
public sealed class BakePhases
{
    /// <summary>Yield the UI thread once a slice is this old. Under the 250 ms shimmer freeze.</summary>
    public const int SliceMs = 120;

    public const string Walls = "wall solids";
    public const string Openings = "openings and blocks";
    public const string Rooms = "rooms";
    public const string Redraws = "redraws";
    public const string Undo = "undo recording";
    public const string Attributes = "attribute writes";
    public const string Layers = "layer lookups";

    public static readonly string[] Names =
    {
        Walls, Openings, Rooms, Redraws, Undo, Attributes, Layers
    };

    readonly long[] _ms = new long[Names.Length];
    readonly Func<long> _millis;
    readonly Stack<int> _stack = new Stack<int>();
    int _open = -1;
    long _mark;
    bool _paused;

    public BakePhases()
        : this(null)
    {
    }

    public BakePhases(Func<long> millis)
    {
        if (millis == null)
        {
            var clock = Stopwatch.StartNew();
            millis = () => clock.ElapsedMilliseconds;
        }
        _millis = millis;
        _mark = _millis();
    }

    public long Milliseconds(string name)
    {
        Flush();
        var index = Index(name);
        return index < 0 ? 0 : _ms[index];
    }

    public IDisposable Time(string name)
    {
        var index = Index(name);
        if (index < 0 || _open == index) return Pause.Empty;
        Enter(index);
        return new Pop(this);
    }

    public void Note(string name, long ms)
    {
        if (ms <= 0) return;
        var index = Index(name);
        if (index >= 0) _ms[index] += ms;
    }

    public void PauseClock()
    {
        Flush();
        _paused = true;
    }

    public void ResumeClock()
    {
        _mark = _millis();
        _paused = false;
    }

    public string Line()
    {
        Flush();
        long total = 0;
        var parts = new string[Names.Length];
        for (var i = 0; i < Names.Length; i++)
        {
            total += _ms[i];
            parts[i] = Names[i] + " " + _ms[i].ToString(CultureInfo.InvariantCulture) + " ms";
        }
        return "bake · " + string.Join(" · ", parts)
            + " · total " + total.ToString(CultureInfo.InvariantCulture) + " ms";
    }

    void Enter(int index)
    {
        Flush();
        _stack.Push(_open);
        _open = index;
    }

    void Leave()
    {
        Flush();
        _open = _stack.Count > 0 ? _stack.Pop() : -1;
    }

    void Flush()
    {
        var now = _millis();
        if (!_paused && _open >= 0)
            _ms[_open] += now - _mark;
        _mark = now;
    }

    static int Index(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (var i = 0; i < Names.Length; i++)
            if (Names[i] == name) return i;
        return -1;
    }

    sealed class Pop : IDisposable
    {
        readonly BakePhases _phases;
        public Pop(BakePhases phases) { _phases = phases; }
        public void Dispose() { _phases.Leave(); }
    }

    sealed class Pause : IDisposable
    {
        public static readonly Pause Empty = new Pause();
        public void Dispose() { }
    }
}
