using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R2: plan_dims. add stores a dimension across the picked walls (and
/// their doors and windows) by Forsk ids, in forsk/user_dims; remove drops
/// some or all; list reads them. Nothing is drawn here: Print resolves each
/// one from the live records (UserDims) and draws it on the plan.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string PlanDimsNeedsPick = "Pick two or more walls, or walls with their doors and windows, then Add dimensions.";

    [McpCommand("plan_dims")]
    public JObject PlanDimsTool(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var action = (parameters?["action"]?.ToString() ?? "list").Trim().ToLowerInvariant();
        var dims = UserDims.Read(doc.Strings.GetValue(UserDims.Section, UserDims.Entry));
        switch (action)
        {
            case "add":
                return AddUserDim(doc, dims, parameters?["refs"] as JArray);
            case "remove":
                return RemoveUserDims(doc, dims, parameters?["ids"] as JArray);
            case "list":
                return UserDimsResult(dims, dims.Count == 0
                    ? "No dimensions of your own yet."
                    : Count(dims.Count, "dimension") + ": " + string.Join(", ", dims.Select(d => d.Id)) + ".");
            default:
                throw new ArgumentException("action is add, remove or list.");
        }
    }

    private JObject AddUserDim(RhinoDoc doc, List<UserDims.Dim> dims, JArray given)
    {
        var refs = given != null && given.Count > 0 ? ReadUserDimRefs(given) : UserDimRefsFromSelection(doc);
        var wallRefs = refs.Where(r => r.Wall != null).ToList();
        if (refs.Count < 2 || wallRefs.Count == 0)
            throw new InvalidOperationException(PlanDimsNeedsPick);

        var walls = ModelWallRings(doc);
        var missing = wallRefs.FirstOrDefault(r => !walls.ContainsKey(r.Wall));
        if (missing != null)
            throw new InvalidOperationException("No wall " + missing.Wall + ".");
        var why = UserDims.Check(wallRefs.Select(r => walls[r.Wall]), 1.0);
        if (why != null)
            throw new InvalidOperationException(why);

        var dim = new UserDims.Dim { Id = UserDims.NextId(dims), Refs = refs };
        var chain = UserDims.Resolve(dim, walls, ModelOpenings(doc, walls), 1.0, out var gone);
        if (chain == null)
            throw new InvalidOperationException(PlanDimsNeedsPick);
        dims.Add(dim);
        doc.Strings.SetString(UserDims.Section, UserDims.Entry, UserDims.Write(dims));

        var total = (int)Math.Round(chain.Stops[chain.Stops.Count - 1] - chain.Stops[0]);
        var message = "✓ Dimension across " + Count(chain.Walls, "wall") + " added · it prints on the plan.";
        if (gone.Skipped > 0)
            message += " " + Count(gone.Skipped, "opening") + (gone.Skipped == 1 ? " is" : " are")
                + " left off: its wall runs across the dimension.";
        var result = UserDimsResult(dims, message);
        result["id"] = dim.Id;
        result["total"] = total;
        result["values"] = new JArray(chain.Stops.Zip(chain.Stops.Skip(1), (a, b) => (int)Math.Round(b - a)).ToArray());
        result["skipped"] = gone.Skipped;
        return result;
    }

    private static JObject RemoveUserDims(RhinoDoc doc, List<UserDims.Dim> dims, JArray ids)
    {
        if (dims.Count == 0)
            return UserDimsResult(dims, "No dimensions of your own to remove.");
        var wanted = ids == null
            ? new HashSet<string>(dims.Select(d => d.Id), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(ids.Select(t => t.ToString().Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = dims.Where(d => !wanted.Contains(d.Id)).ToList();
        var removed = dims.Count - kept.Count;
        if (removed == 0)
            return UserDimsResult(dims, "No dimension " + string.Join(", ", wanted) + ".");
        if (kept.Count == 0) doc.Strings.Delete(UserDims.Section, UserDims.Entry);
        else doc.Strings.SetString(UserDims.Section, UserDims.Entry, UserDims.Write(kept));
        return UserDimsResult(kept, "✓ Removed " + Count(removed, "dimension") + " · the plan drops it at the next Print.");
    }

    private static JObject UserDimsResult(List<UserDims.Dim> dims, string message)
    {
        var rows = new JArray();
        foreach (var dim in dims)
            rows.Add(new JObject
            {
                ["id"] = dim.Id,
                ["walls"] = dim.Refs.Count(r => r.Wall != null),
                ["openings"] = dim.Refs.Count(r => r.Opening != null)
            });
        return new JObject { ["dims"] = rows, ["count"] = dims.Count, ["message"] = message };
    }

    private static List<UserDims.Ref> ReadUserDimRefs(JArray given)
    {
        var refs = new List<UserDims.Ref>();
        foreach (var item in given.OfType<JObject>())
        {
            var wall = item["wall"]?.ToString();
            var opening = item["opening"]?.ToString();
            if (!string.IsNullOrWhiteSpace(wall)) refs.Add(new UserDims.Ref { Wall = wall.Trim() });
            else if (!string.IsNullOrWhiteSpace(opening)) refs.Add(new UserDims.Ref { Opening = opening.Trim() });
        }
        return refs;
    }

    /// <summary>The picked walls by forsk:id, and the picked doors and windows by their marker's forsk:id, each once.</summary>
    private static List<UserDims.Ref> UserDimRefsFromSelection(RhinoDoc doc)
    {
        var refs = new List<UserDims.Ref>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = ListSelected(doc);
        foreach (var obj in selected)
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var id = obj.Attributes.GetUserString("forsk:id");
            if (!string.IsNullOrEmpty(id) && seen.Add("w:" + id)) refs.Add(new UserDims.Ref { Wall = id });
        }
        foreach (var marker in MarkersOfSelection(doc, selected))
        {
            var id = marker.Attributes.GetUserString("forsk:id");
            if (string.IsNullOrEmpty(id)) id = marker.Id.ToString();
            if (seen.Add("o:" + id)) refs.Add(new UserDims.Ref { Opening = id });
        }
        return refs;
    }

    /// <summary>Every Forsk wall's rings by forsk:id, in model mm.</summary>
    private static Dictionary<string, List<List<Pt>>> ModelWallRings(RhinoDoc doc)
    {
        var walls = new Dictionary<string, List<List<Pt>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var id = obj.Attributes.GetUserString("forsk:id");
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (!string.IsNullOrEmpty(id) && rings != null) walls[id] = rings;
        }
        return walls;
    }

    /// <summary>
    /// Every opening marker by forsk:id (and object id): the marker's middle
    /// in plan, and its host wall's direction, in model mm.
    /// </summary>
    private static Dictionary<string, UserDims.OpeningAt> ModelOpenings(RhinoDoc doc, Dictionary<string, List<List<Pt>>> walls)
    {
        var openings = new Dictionary<string, UserDims.OpeningAt>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.Ordinal)) continue;
            var hostRaw = obj.Attributes.GetUserString("forsk:host");
            if (!Guid.TryParse(hostRaw, out var hostId)) continue;
            var hostWall = doc.Objects.FindId(hostId)?.Attributes?.GetUserString("forsk:id");
            if (string.IsNullOrEmpty(hostWall) || !walls.TryGetValue(hostWall, out var rings)) continue;
            var run = UserDims.MainRun(rings, 1.0);
            var box = obj.Geometry?.GetBoundingBox(true);
            if (run == null || box == null || !box.Value.IsValid) continue;
            var at = new UserDims.OpeningAt { Centre = new Pt(box.Value.Center.X, box.Value.Center.Y), Along = run.Dir };
            openings[obj.Id.ToString()] = at;
            var forskId = obj.Attributes.GetUserString("forsk:id");
            if (!string.IsNullOrEmpty(forskId)) openings[forskId] = at;
        }
        return openings;
    }

    private static string Count(int n, string noun) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
