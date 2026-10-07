using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.2: Live analyses after an edit. The dispatcher calls BeginLive before
/// a wall or opening edit and FinishLive after it; with areas live the rooms'
/// areas are compared, with daylight live it runs again on the Low grid and
/// each room's mean is compared. The line goes on the edit's result (live)
/// and its message. Every analysis run also keeps its last result on the file.
/// </summary>
public partial class RhinoMCPFunctions
{
    internal static Analysis.State ReadAnalysis(RhinoDoc doc) =>
        Analysis.Read(key => doc?.Strings.GetValue(Analysis.Section, key));

    internal static void WriteAnalysis(RhinoDoc doc, string key, string value)
    {
        if (doc == null) return;
        if (value == null) doc.Strings.Delete(Analysis.Section, key);
        else doc.Strings.SetString(Analysis.Section, key, value);
    }

    /// <summary>Every room with an area, by its record id.</summary>
    private static List<Analysis.Room> LiveRooms(RhinoDoc doc) =>
        PlanRooms(doc).Where(r => r.Area > 0)
            .Select(r => new Analysis.Room { Id = r.ScheduleId, Name = r.Name, AreaM2 = r.Area / 1e6 })
            .ToList();

    /// <summary>What a live edit compares against, or null when nothing is live.</summary>
    internal static LiveBefore BeginLive(RhinoDoc doc)
    {
        if (doc == null) return null;
        var state = ReadAnalysis(doc);
        if (!state.AnyLive) return null;
        return new LiveBefore { State = state, Rooms = LiveRooms(doc) };
    }

    internal sealed class LiveBefore
    {
        public Analysis.State State;
        public List<Analysis.Room> Rooms;
    }

    /// <summary>After the edit: the line, put on the result. A failed daylight run says so and leaves the areas.</summary>
    internal void FinishLive(RhinoDoc doc, LiveBefore before, JObject result)
    {
        if (doc == null || before == null || result == null) return;
        var after = before.State.IsLive(Analysis.Areas) ? LiveRooms(doc) : before.Rooms;
        IDictionary<string, double> dfAfter = null;
        string note = null;
        if (before.State.IsLive(Analysis.Daylight))
        {
            var run = Forsk.ForskDaylight.Run("floor", CallCommand, Analysis.LiveQuality);
            if (run?["status"]?.ToString() == "success") dfAfter = ReadAnalysis(doc).RoomDf;
            else note = "daylight did not run: " + (run?["message"]?.ToString() ?? "failed");
        }
        var line = Analysis.LiveLine(before.Rooms, after,
            dfAfter == null ? null : before.State.RoomDf, dfAfter);
        if (line == null && note == null) return;
        var text = line ?? "";
        if (note != null) text = (text.Length > 0 ? text + "; " : "") + note;
        result["live"] = text;
        var message = result["message"]?.ToString() ?? "";
        result["message"] = (message.Length > 0 ? message.TrimEnd() + " " : "") + "Live: " + text + ".";
    }

    /// <summary>One command by name, as the window's tools see it: {status, result} or {status: error, message}.</summary>
    internal JObject CallCommand(string name, JObject parameters)
    {
        try
        {
            if (!GetDispatchTable().TryGetValue(name, out var entry))
                return new JObject { ["status"] = "error", ["message"] = "No command " + name + "." };
            return new JObject { ["status"] = "success", ["result"] = entry.Handler(parameters ?? new JObject()) };
        }
        catch (Exception e)
        {
            return new JObject { ["status"] = "error", ["message"] = e.Message };
        }
    }

    /// <summary>
    /// A daylight run's result on the file: the last line for the menu, and
    /// each room's mean by its record id (the scene names rooms by their object id).
    /// </summary>
    internal static void NoteDaylight(RhinoDoc doc, JObject traced)
    {
        if (doc == null || traced == null) return;
        var spaces = traced["spaces"]?.Value<int>() ?? 0;
        var mean = traced["df_mean"]?.Value<double>() ?? 0;
        WriteAnalysis(doc, Analysis.LastKey(Analysis.Daylight), Analysis.DaylightLast(mean, spaces));
        var rooms = ReadAnalysis(doc).RoomDf;
        foreach (var room in traced["rooms"] as JArray ?? new JArray())
        {
            if (!Guid.TryParse(room["id"]?.ToString(), out var guid)) continue;
            var obj = doc.Objects.FindId(guid);
            var id = obj?.Attributes.GetUserString(RoomIdKey);
            if (string.IsNullOrEmpty(id)) id = obj?.Attributes.Name;
            if (string.IsNullOrEmpty(id)) continue;
            rooms[id] = room["df_mean"]?.Value<double>() ?? 0;
        }
        WriteAnalysis(doc, Analysis.RoomsKey, Analysis.WriteRoomDf(rooms));
    }

    internal static void NoteAreas(RhinoDoc doc, AreaStats.Result result)
    {
        if (doc == null || result == null) return;
        WriteAnalysis(doc, Analysis.LastKey(Analysis.Areas), Analysis.AreasLast(result.NetMm2 / 1e6, result.Rooms.Count));
    }
}
