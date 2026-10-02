using System;

namespace RhinoMCPPlugin;

/// <summary>
/// Marks a method on RhinoMCPFunctions as the handler for a JSON command type.
/// The reflection-based registry in RhinoMCPFunctions.GetDispatchTable() uses
/// these attributes to build the dispatch table at startup. To add a new command,
/// add a new method with this attribute — no other wiring needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpCommandAttribute : Attribute
{
    /// <summary>The JSON command type, e.g. "create_object". Conventionally snake_case.</summary>
    public string Name { get; }

    /// <summary>
    /// If true, the dispatcher does not wrap the handler in a Rhino undo record.
    /// Use for purely introspective commands (get_*, undo, redo, capture_viewport).
    /// Settable so call sites can use named-argument syntax: [McpCommand("foo", ReadOnly = true)].
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// If true, the dispatcher makes a model view active before the handler runs.
    /// Set it on every command that adds objects to the model: while a layout is
    /// the active view (export_pdf leaves one), Rhino puts a new object in that
    /// page's space whatever its attributes say, and clear_layouts deletes it with
    /// the page. contracts/test_schemas.py fails a write command that neither sets
    /// it nor is listed as keeping the view.
    /// </summary>
    public bool ModelView { get; set; }

    /// <summary>
    /// What a successful call does to the daylight map. Opening: the map is
    /// marked out of date. Wall: marked out of date and hidden. The dispatcher
    /// writes it after the handler returns, inside the same undo record.
    /// </summary>
    public Forsk.MapEdit Map { get; set; }

    public McpCommandAttribute(string name)
    {
        Name = name;
    }
}
