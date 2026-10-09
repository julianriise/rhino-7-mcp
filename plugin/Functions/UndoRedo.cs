using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    /// <summary>
    /// Undo the last operation in the Rhino document.
    /// </summary>
    [McpCommand("undo", ReadOnly = true)]
    public JObject Undo(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;

        // Get number of undo steps (default 1)
        int steps = parameters["steps"]?.ToObject<int>() ?? 1;

        int undoneCount = 0;
        for (int i = 0; i < steps; i++)
        {
            if (RunUndoCommand(redo: false))
            {
                undoneCount++;
            }
            else
            {
                break; // No more undo records available
            }
        }

        doc.Views.Redraw();

        return new JObject
        {
            ["undone_steps"] = undoneCount,
            ["requested_steps"] = steps,
            ["message"] = undoneCount > 0
                ? $"Undid {undoneCount} operation(s)"
                : "Nothing to undo"
        };
    }

    /// <summary>
    /// Redo the last undone operation in the Rhino document.
    /// </summary>
    [McpCommand("redo", ReadOnly = true)]
    public JObject Redo(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;

        // Get number of redo steps (default 1)
        int steps = parameters["steps"]?.ToObject<int>() ?? 1;

        int redoneCount = 0;
        for (int i = 0; i < steps; i++)
        {
            if (RunUndoCommand(redo: true))
            {
                redoneCount++;
            }
            else
            {
                break; // No more redo records available
            }
        }

        doc.Views.Redraw();

        return new JObject
        {
            ["redone_steps"] = redoneCount,
            ["requested_steps"] = steps,
            ["message"] = redoneCount > 0
                ? $"Redid {redoneCount} operation(s)"
                : "Nothing to redo"
        };
    }

    /// <summary>
    /// Undo or redo one step with Rhino's own command, as Cmd+Z does. RhinoDoc.Undo
    /// outside a command left an undo record open, and after it Cmd+Z undid nothing.
    /// True when Rhino undid (or redid) a record.
    /// </summary>
    public static bool RunUndoCommand(bool redo)
    {
        var done = false;
        EventHandler<UndoRedoEventArgs> watch = (_, e) =>
        {
            if (redo ? e.IsBeginRedo : e.IsBeginUndo) done = true;
        };
        Command.UndoRedo += watch;
        try
        {
            RhinoApp.RunScript(redo ? "_Redo" : "_Undo", false);
        }
        finally
        {
            Command.UndoRedo -= watch;
        }
        return done;
    }
}
