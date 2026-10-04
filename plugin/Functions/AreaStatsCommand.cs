using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// area_stats reads the tagged rooms the Romliste reads, and BRA/BTA from the
/// wall records' outer loops. It does not change the model.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("area_stats", ReadOnly = true)]
    public JObject AreaStatsCommand(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
        {
            return new JObject
            {
                ["summary"] = "No file open.",
                ["message"] = "No file open.",
                ["floors"] = new JArray(),
                ["uses"] = new JArray(),
                ["rooms"] = new JArray(),
                ["more"] = 0,
                ["omitted"] = new JArray()
            };
        }
        return AreaStats.ToJson(ReadAreaStats(doc));
    }

    /// <summary>Tagged rooms, as the Romliste lists them, plus BRA/BTA where the walls give them.</summary>
    private AreaStats.Result ReadAreaStats(RhinoDoc doc)
    {
        var rooms = new List<AreaStats.Room>();
        foreach (var room in PlanRooms(doc))
        {
            if (!room.Tagged) continue;
            rooms.Add(new AreaStats.Room
            {
                Id = room.ScheduleId,
                Name = room.Name,
                Level = room.Level,
                AreaMm2 = room.Area,
                PerimeterMm = RoomDetect.Perimeter(room.Outline)
            });
        }
        var result = AreaStats.Compute(rooms);
        var walls = new List<AreaStats.Wall>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null || !IsForskGenerated(obj) || IsExistingUnderlay(doc, obj)) continue;
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings == null) continue;
            walls.Add(new AreaStats.Wall
            {
                Level = obj.Attributes.GetUserString("forsk:level"),
                ThicknessMm = ParseMm(obj.Attributes.GetUserString("forsk:thickness")),
                Rings = rings
            });
        }
        AreaStats.ApplyGross(result, walls, Math.Max(doc.ModelAbsoluteTolerance, 1.0));
        return result;
    }
}
