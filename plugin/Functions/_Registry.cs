using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Reflection-based dispatch registry for RhinoMCPFunctions.
/// Scans methods decorated with [McpCommand] once and caches the lookup table.
/// </summary>
public partial class RhinoMCPFunctions
{
    public readonly struct DispatchEntry
    {
        public readonly Func<JObject, JObject> Handler;
        public readonly bool ReadOnly;

        public DispatchEntry(Func<JObject, JObject> handler, bool readOnly)
        {
            Handler = handler;
            ReadOnly = readOnly;
        }
    }

    private IReadOnlyDictionary<string, DispatchEntry> _dispatchTable;

    /// <summary>
    /// Returns a dispatch table built by reflecting over methods on this instance
    /// decorated with [McpCommand]. Built lazily on first access, then cached.
    /// </summary>
    public IReadOnlyDictionary<string, DispatchEntry> GetDispatchTable()
    {
        if (_dispatchTable != null) return _dispatchTable;

        var table = new Dictionary<string, DispatchEntry>(StringComparer.Ordinal);
        var methods = typeof(RhinoMCPFunctions).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<McpCommandAttribute>();
            if (attr == null) continue;

            var handler = (Func<JObject, JObject>)Delegate.CreateDelegate(
                typeof(Func<JObject, JObject>), this, method);
            if (attr.ModelView)
            {
                var inner = handler;
                handler = parameters =>
                {
                    UseModelView(RhinoDoc.ActiveDoc);
                    return inner(parameters);
                };
            }

            if (attr.Map != Forsk.MapEdit.None)
            {
                var edit = attr.Map;
                var inner = handler;
                handler = parameters =>
                {
                    // AN.2: with an analysis live, the edit's result says what it did to the rooms.
                    var live = BeginLive(RhinoDoc.ActiveDoc);
                    var result = inner(parameters);
                    MarkMapAfterEdit(RhinoDoc.ActiveDoc, edit);
                    FinishLive(RhinoDoc.ActiveDoc, live, result);
                    return result;
                };
            }

            if (!attr.ReadOnly)
            {
                var inner = handler;
                handler = parameters =>
                {
                    // The roof cannot be picked with the cursor (Julian, 2026-10-07): its layer stays
                    // locked, and only a Forsk write opens it, to replace or clear the roof.
                    RoofLayerLocked(RhinoDoc.ActiveDoc, false);
                    try { return inner(parameters); }
                    finally { RoofLayerLocked(RhinoDoc.ActiveDoc, true); }
                };
            }

            if (table.ContainsKey(attr.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate [McpCommand(\"{attr.Name}\")] on {method.Name}.");
            }
            table[attr.Name] = new DispatchEntry(handler, attr.ReadOnly);
        }

        _dispatchTable = table;
        return _dispatchTable;
    }
}
