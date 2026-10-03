using Rhino;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// One undo record per pill or chat answer, named "Forsk: label or turn".
    /// Tool calls made while it is open skip their own record: Dispatch checks
    /// UndoRecordingIsActive first. When a record is already open this one
    /// does not nest; the open record holds the change, and the log says so.
    /// Begin and End run on the UI thread.
    /// </summary>
    public sealed class ForskUndo
    {
        readonly RhinoDoc _doc;

        ForskUndo(RhinoDoc doc, string name)
        {
            _doc = doc;
            Name = name;
        }

        public string Name { get; }
        /// <summary>The record's serial number, or 0 when this scope did not open one.</summary>
        public uint Serial { get; private set; }

        public static ForskUndo Begin(RhinoDoc doc, string label)
        {
            var text = (label ?? "").Replace('\n', ' ').Trim();
            if (text.Length > 60) text = text.Substring(0, 57) + "...";
            var undo = new ForskUndo(doc, "Forsk: " + text);
            if (doc == null) return undo;
            if (doc.UndoRecordingIsActive)
            {
                ForskWindow.Log("undo: a record was already open; " + undo.Name + " did not nest");
                return undo;
            }
            using (BakePace.Time(BakePhases.Undo))
                undo.Serial = doc.BeginUndoRecord(undo.Name);
            ForskWindow.Log("undo: began " + undo.Serial + " " + undo.Name + " · recording " + doc.UndoRecordingIsActive);
            return undo;
        }

        public void End()
        {
            if (Serial == 0 || _doc == null) return;
            bool ended;
            using (BakePace.Time(BakePhases.Undo))
                ended = _doc.EndUndoRecord(Serial);
            ForskWindow.Log("undo: ended " + Serial + " · " + ended);
            Serial = 0;
        }
    }

    /// <summary>
    /// Forsk's own tool calls in progress on the UI thread. An object added,
    /// changed or deleted while none is running came from the user, and clears
    /// the last action.
    /// </summary>
    public static class ForskCalls
    {
        public static int Depth { get; private set; }

        public static void Enter()
        {
            Depth++;
        }

        public static void Exit()
        {
            if (Depth > 0) Depth--;
        }
    }
}
