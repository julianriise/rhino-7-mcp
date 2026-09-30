using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Plan import (F7) in the document. plan_import places the plan image as a
/// locked, faded underlay on X-PLAN, shown in every view's display mode and
/// kept off Print, and draws the cleaned
/// detection on the 2D layers the bake reads: a closed outline per connected
/// run of walls on wall, with the holes it closes as loops of their own,
/// opening footprints on door and window, room outlines on A-ROOM and
/// their names on label. Nothing is 3D until the user bakes. plan_scale sets
/// the scale from two points and a known length, moving the underlay and the
/// plan together about the plan's top-left corner.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string PlanUnderlayLayerName = "X-PLAN";
    private const string ImportKey = "forsk:import";
    private const string ImportKindKey = "forsk:import_kind";
    private const string ImportScaleKey = "forsk:import_scale";
    private const string ImportScaleStatusKey = "forsk:import_scale_status";
    private const string ImportRatioKey = "forsk:import_ratio";
    /// <summary>forsk:import_kind of a hole in a wall outline: the bake reads it with the outline around it, never as a wall.</summary>
    private const string ImportWallHoleKind = "wall-hole";
    private const string ImportReviewKey = "forsk:import_review";

    /// <summary>The image lies just under the plan curves, so they draw on top of it.</summary>
    private const double PlanUnderlayZ = -1.0;
    private const double PlanUnderlayFade = 0.5;
    private const double PlanLabelHeight = 200.0;

    /// <summary>The 2D layers a plan is reviewed on. The scale step moves what is on them.</summary>
    private static readonly string[] PlanReviewLayers = { "wall", "door", "window", "A-ROOM", "room", RoomLabelLayerName, DividerLayerName };

    [McpCommand("plan_import")]
    public JObject ImportPlan(JObject parameters)
    {
        var imagePath = parameters["image_path"]?.ToString();
        var planPath = parameters["plan_path"]?.ToString();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new ArgumentException("image_path must be an existing image file.");
        if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
            throw new ArgumentException("plan_path must be an existing forsk.plan_import.v0 file.");

        var doc = RhinoDoc.ActiveDoc;
        if (doc.ModelUnitSystem != UnitSystem.Millimeters)
            throw new InvalidOperationException("Document units must be millimetres. Switch the .3dm to millimetres.");

        var plan = PlanImport.Parse(File.ReadAllText(planPath));
        var hint = parameters["scale_hint"]?.ToString();
        if (!string.IsNullOrWhiteSpace(hint) && !PlanImport.TryRatio(hint, out _))
            throw new ArgumentException("scale_hint must read like 1:100.");
        var detected = string.Equals(plan.ScaleStatus, "detected", StringComparison.OrdinalIgnoreCase);
        var ratio = detected && !string.IsNullOrWhiteSpace(plan.ScaleRatio) ? plan.ScaleRatio
            : !string.IsNullOrWhiteSpace(hint) ? hint.Trim()
            : plan.ScaleRatio;
        var status = detected ? "detected" : "unconfirmed";

        var size = PlanImageSize(imagePath, parameters, plan.ImageWidthMm, ratio);

        var earlier = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
            if (!string.IsNullOrEmpty(obj.Attributes.GetUserString(ImportKey))) earlier.Add(obj);
        if (earlier.Count > 0 && parameters["replace"]?.ToObject<bool?>() != true)
            throw new InvalidOperationException(
                "This document already has an imported plan. Undo that import, or pass replace to swap it.");
        foreach (var obj in earlier)
            doc.Objects.Delete(obj, true, true);

        var cleaned = PlanImport.Clean(plan);

        var underlayLayer = EnsureLayer(doc, PlanUnderlayLayerName, Color.FromArgb(140, 140, 140));
        if (underlayLayer.IsLocked)
        {
            underlayLayer.IsLocked = false;
            doc.Layers.Modify(underlayLayer, underlayLayer.Index, true);
        }
        var warnings = new JArray();
        var plane = new Plane(new Point3d(0, -size.HeightMm, PlanUnderlayZ), Vector3d.XAxis, Vector3d.YAxis);
        var underlayId = doc.Objects.AddPictureFrame(plane, imagePath, false, size.WidthMm, size.HeightMm, true, true);
        var picture = underlayId == Guid.Empty ? null : doc.Objects.FindId(underlayId);
        if (picture == null)
            throw new InvalidOperationException("Rhino could not place the plan image " + Path.GetFileName(imagePath) + ".");
        FadeUnderlay(doc, picture, warnings);
        var underlayAttr = picture.Attributes.Duplicate();
        // A picture draws its image in a rendered mode only: without this the Top view, in wireframe, shows nothing of it.
        var display = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.RenderedId);
        if (display != null) underlayAttr.SetDisplayModeOverride(display);
        else warnings.Add("The plan image shows in rendered views only: the Rendered display mode was not found.");
        underlayAttr.Name = "plan-underlay";
        underlayAttr.LayerIndex = underlayLayer.Index;
        underlayAttr.SetUserString(ImportKey, "plan");
        underlayAttr.SetUserString(ImportKindKey, "underlay");
        underlayAttr.SetUserString(ImportScaleKey, "1");
        underlayAttr.SetUserString(ImportScaleStatusKey, status);
        if (!string.IsNullOrWhiteSpace(ratio)) underlayAttr.SetUserString(ImportRatioKey, ratio);
        underlayAttr.SetUserString("forsk:import_plan", Path.GetFileName(planPath));
        if (!string.IsNullOrWhiteSpace(plan.Vendor)) underlayAttr.SetUserString("forsk:import_vendor", plan.Vendor);
        doc.Objects.ModifyAttributes(underlayId, underlayAttr, true);
        KeepLayerOffPrint(doc, underlayLayer);
        underlayLayer.IsLocked = true;
        doc.Layers.Modify(underlayLayer, underlayLayer.Index, true);

        var wallLayer = EnsureLayer(doc, "wall", Color.FromArgb(30, 30, 30));
        var doorLayer = EnsureLayer(doc, "door", Color.FromArgb(190, 90, 30));
        var windowLayer = EnsureLayer(doc, "window", Color.FromArgb(30, 110, 190));
        var roomLayer = EnsureLayer(doc, ResolveRoomSourceLayer(doc, "A-ROOM")?.Name ?? "A-ROOM", Color.FromArgb(200, 180, 120));
        var labelLayer = EnsureLayer(doc, RoomLabelLayerName, Color.FromArgb(90, 90, 90));

        var objects = 1;
        var wallNames = new List<string>();
        for (var i = 0; i < cleaned.Networks.Count; i++)
        {
            var outline = cleaned.Networks[i];
            var name = "import-wall-" + (i + 1).ToString("D2", CultureInfo.InvariantCulture);
            wallNames.Add(name);
            var attr = ImportAttributes(wallLayer, name, "wall");
            attr.SetUserString("forsk:import_pieces", outline.Pieces.ToString(CultureInfo.InvariantCulture));
            if (doc.Objects.AddCurve(RoomOutline(outline.Outer, 0), attr) != Guid.Empty) objects++;
            for (var h = 0; h < outline.Holes.Count; h++)
            {
                var hole = ImportAttributes(wallLayer, name + "-hole-" + (h + 1).ToString("D2", CultureInfo.InvariantCulture), ImportWallHoleKind);
                if (doc.Objects.AddCurve(RoomOutline(outline.Holes[h], 0), hole) != Guid.Empty) objects++;
            }
        }

        int doors = 0, windows = 0;
        foreach (var opening in cleaned.Openings)
        {
            var window = opening.Kind == "window";
            var name = "import-" + opening.Kind + "-" + (window ? ++windows : ++doors).ToString("D2", CultureInfo.InvariantCulture);
            var attr = ImportAttributes(window ? windowLayer : doorLayer, name, opening.Kind);
            attr.SetUserString("forsk:import_width", FormatMm(opening.Width));
            if (opening.Host >= 0 && cleaned.Walls[opening.Host].Network >= 0)
                attr.SetUserString("forsk:import_host", wallNames[cleaned.Walls[opening.Host].Network]);
            if (opening.Note != null) attr.SetUserString(ImportReviewKey, opening.Note);
            if (opening.Host < 0)
            {
                // No wall under it: red, so it is seen before the bake skips it.
                attr.ColorSource = ObjectColorSource.ColorFromObject;
                attr.ObjectColor = Color.FromArgb(220, 40, 40);
            }
            if (doc.Objects.AddCurve(RoomOutline(PlanImport.Ring(opening), 0), attr) != Guid.Empty) objects++;
        }

        for (var i = 0; i < cleaned.Rooms.Count; i++)
        {
            var room = cleaned.Rooms[i];
            var name = "import-room-" + (i + 1).ToString("D2", CultureInfo.InvariantCulture);
            var attr = ImportAttributes(roomLayer, name, "room");
            if (room.Outside) attr.SetUserString(ImportReviewKey, "the walls do not close around it");
            if (doc.Objects.AddCurve(RoomOutline(room.Ring, 0), attr) != Guid.Empty) objects++;
            // A room the detection could not name gets the name rooms_detect would give it, as a text to edit.
            var label = room.Label ?? RoomDetect.DefaultRoomName;
            var at = Plane.WorldXY;
            at.Origin = new Point3d(room.At.X, room.At.Y, 0);
            using (var text = PlanAnnotation(doc, label, at, PlanLabelHeight))
            {
                if (text == null)
                {
                    warnings.Add("Label " + label + " was not drawn.");
                    continue;
                }
                if (doc.Objects.AddText(text, ImportAttributes(labelLayer, name + "-label", "label")) != Guid.Empty) objects++;
            }
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["walls"] = cleaned.Walls.Count,
            ["walls_detected"] = cleaned.Detected,
            ["merged"] = cleaned.Merged,
            ["squared"] = cleaned.Snapped,
            ["diagonal"] = cleaned.Diagonal,
            ["joined"] = cleaned.Joined,
            ["gaps_closed"] = cleaned.Closed,
            ["extended"] = cleaned.Extended,
            ["doors"] = cleaned.Doors,
            ["windows"] = cleaned.Windows,
            ["loose"] = cleaned.Loose,
            ["uncut"] = cleaned.Uncut,
            ["outlines"] = cleaned.Outlines,
            ["outline_holes"] = cleaned.Networks.Sum(outline => outline.Holes.Count),
            ["wall_pieces"] = cleaned.Networks.Sum(outline => outline.Pieces),
            ["overlaps"] = cleaned.Overlaps,
            ["free_walls"] = cleaned.Blocks,
            ["blocks_skipped"] = cleaned.Skipped,
            ["rooms"] = cleaned.Rooms.Count,
            ["unlabelled"] = cleaned.Unlabelled,
            ["outside"] = cleaned.Outside,
            ["dropped"] = new JArray(cleaned.Dropped),
            ["review"] = new JArray(cleaned.Review),
            ["scale"] = new JObject
            {
                ["status"] = status,
                ["ratio"] = ratio,
                ["factor"] = 1.0
            },
            ["underlay"] = new JObject
            {
                ["id"] = underlayId.ToString(),
                ["layer"] = underlayLayer.Name,
                ["image"] = Path.GetFileName(imagePath),
                ["display"] = display?.EnglishName,
                ["width_mm"] = Math.Round(size.WidthMm, 1),
                ["height_mm"] = Math.Round(size.HeightMm, 1)
            },
            ["objects"] = objects,
            ["replaced"] = earlier.Count,
            ["warnings"] = warnings,
            ["message"] = PlanImport.Message(cleaned, PlanImport.ScaleLine(status, ratio))
        };
    }

    private sealed class PlanImage
    {
        public double WidthMm;
        public double HeightMm;
    }

    /// <summary>
    /// The image's size on the plan: image_width_mm as given, else the width
    /// the plan file states, else its pixels at its dpi (image_dpi, or the
    /// PNG's own) times the drawing's scale.
    /// </summary>
    private static PlanImage PlanImageSize(string imagePath, JObject parameters, double statedWidthMm, string ratio)
    {
        int width, height;
        double dpi;
        if (!PlanImport.TryPngSize(File.ReadAllBytes(imagePath), out width, out height, out dpi))
        {
            try
            {
                using (var bitmap = new Bitmap(imagePath))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    dpi = bitmap.HorizontalResolution;
                }
            }
            catch (Exception)
            {
                throw new ArgumentException("image_path is not an image Rhino can read: " + Path.GetFileName(imagePath) + ".");
            }
        }

        var widthMm = parameters["image_width_mm"]?.ToObject<double?>() ?? 0;
        if (widthMm <= 0) widthMm = statedWidthMm;
        if (widthMm <= 0)
        {
            dpi = parameters["image_dpi"]?.ToObject<double?>() ?? dpi;
            if (dpi <= 0 || !PlanImport.TryRatio(ratio, out var denominator))
                throw new ArgumentException(
                    "The plan image's size is not known. Pass image_width_mm (its width in mm on the plan), or image_dpi with scale_hint such as 1:100.");
            widthMm = PlanImport.ImageWidthMm(width, dpi, denominator);
        }
        return new PlanImage { WidthMm = widthMm, HeightMm = widthMm * height / width };
    }

    private static void FadeUnderlay(RhinoDoc doc, RhinoObject picture, JArray warnings)
    {
        try
        {
            var index = picture.Attributes.MaterialIndex;
            if (picture.Attributes.MaterialSource != ObjectMaterialSource.MaterialFromObject || index < 0)
            {
                warnings.Add("The plan image is not faded: it has no material of its own.");
                return;
            }
            var material = doc.Materials[index];
            material.Transparency = PlanUnderlayFade;
            material.CommitChanges();
        }
        catch (Exception e)
        {
            warnings.Add("The plan image is not faded: " + e.Message);
        }
    }

    private static ObjectAttributes ImportAttributes(Layer layer, string name, string kind)
    {
        var attr = new ObjectAttributes { Name = name, LayerIndex = layer.Index };
        attr.SetUserString(ImportKey, "plan");
        attr.SetUserString(ImportKindKey, kind);
        return attr;
    }

    private static RhinoObject FindPlanUnderlay(RhinoDoc doc)
    {
        foreach (var obj in EnumerateDocObjects(doc))
            if (obj.Attributes.GetUserString(ImportKindKey) == "underlay") return obj;
        return null;
    }

    [McpCommand("plan_scale")]
    public JObject ScalePlan(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var underlay = FindPlanUnderlay(doc);
        if (underlay == null)
            throw new InvalidOperationException("No imported plan in this document. Run plan_import first.");
        if (!TryPlanPoint(parameters["p1"], out var p1) || !TryPlanPoint(parameters["p2"], out var p2))
            throw new ArgumentException("p1 and p2 must each be [x, y] in mm.");
        var frame = parameters["frame"]?.ToString() ?? "model";
        if (frame != "model" && frame != "source")
            throw new ArgumentException("frame must be model or source.");
        var source = frame == "source";

        var box = underlay.Geometry.GetBoundingBox(true);
        var now = new PlanImport.Scale { Origin = new RoomDetect.Pt(box.Min.X, box.Max.Y) };
        if (double.TryParse(underlay.Attributes.GetUserString(ImportScaleKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var was) && was > 0)
            now.Factor = was;
        var status = underlay.Attributes.GetUserString(ImportScaleStatusKey) ?? "unconfirmed";
        var measured = PlanImport.Measure(now, p1, p2, source);

        var length = parameters["length_mm"]?.ToObject<double?>();
        if (length == null)
        {
            return new JObject
            {
                ["measured_mm"] = Math.Round(measured, 1),
                ["factor"] = now.Factor,
                ["status"] = status,
                ["scaled"] = 0,
                ["message"] = "The two points are " + FormatMm(Math.Round(measured)) + " mm apart at the scale the plan has now. "
                    + "Give the real length between them as length_mm to set the scale."
            };
        }

        var generated = 0;
        foreach (var obj in EnumerateDocObjects(doc))
            if (IsForskGenerated(obj)) generated++;
        if (generated > 0)
            throw new InvalidOperationException(
                "The 3D model is already generated from this plan. Clear it (clear_generated), set the scale, then generate again.");

        var factor = PlanImport.FactorFor(now, p1, p2, length.Value, source);
        var relative = factor / now.Factor;
        // Uniform, about the corner at plan level: texts scale with it and the plan curves stay at Z 0.
        var xform = Transform.Scale(new Point3d(now.Origin.X, now.Origin.Y, 0), relative);

        var layers = new List<Layer>();
        foreach (var name in PlanReviewLayers)
        {
            var layer = FindLayerCaseInsensitive(doc, name);
            if (layer != null) layers.Add(layer);
        }
        var targets = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj.Id == underlay.Id) continue;
            var imported = !string.IsNullOrEmpty(obj.Attributes.GetUserString(ImportKey));
            // What the user has drawn on the plan layers since goes with the plan.
            if (imported || layers.Exists(layer => ObjectOnLayer(doc, obj, layer))) targets.Add(obj);
        }

        // A wall outline scales as drawn, like the rest: nothing is rounded again, so nothing can drift.
        var scaled = 0;
        foreach (var obj in targets)
            if (doc.Objects.Transform(obj, xform, true) != Guid.Empty) scaled++;

        // The underlay sits on a locked layer: unlocked for the move, locked again after.
        var underlayLayer = doc.Layers[underlay.Attributes.LayerIndex];
        var locked = underlayLayer != null && underlayLayer.IsLocked;
        if (locked)
        {
            underlayLayer.IsLocked = false;
            doc.Layers.Modify(underlayLayer, underlayLayer.Index, true);
        }
        var movedId = doc.Objects.Transform(underlay, xform, true);
        var moved = movedId == Guid.Empty ? null : doc.Objects.FindId(movedId);
        if (moved == null)
            throw new InvalidOperationException("Rhino could not scale the plan image.");
        var attr = moved.Attributes.Duplicate();
        attr.SetUserString(ImportScaleKey, factor.ToString("R", CultureInfo.InvariantCulture));
        attr.SetUserString(ImportScaleStatusKey, "user");
        doc.Objects.ModifyAttributes(movedId, attr, true);
        if (locked)
        {
            underlayLayer.IsLocked = true;
            doc.Layers.Modify(underlayLayer, underlayLayer.Index, true);
        }

        doc.Views.Redraw();
        var times = relative.ToString("0.####", CultureInfo.InvariantCulture);
        return new JObject
        {
            ["measured_mm"] = Math.Round(measured, 1),
            ["length_mm"] = length.Value,
            ["factor"] = factor,
            ["previous_factor"] = now.Factor,
            ["relative"] = relative,
            ["status"] = "user",
            ["scaled"] = scaled + 1,
            ["message"] = Math.Abs(relative - 1.0) < 1e-9
                ? "Scale confirmed: the two points are " + FormatMm(Math.Round(length.Value)) + " mm apart. Nothing moved."
                : "Scale set: " + FormatMm(Math.Round(length.Value)) + " mm between the two points (was "
                    + FormatMm(Math.Round(measured)) + " mm, x" + times + "). The underlay and "
                    + scaled + " plan object" + (scaled == 1 ? "" : "s") + " moved together about the plan's top-left corner."
        };
    }

    private static bool TryPlanPoint(JToken token, out RoomDetect.Pt point)
    {
        point = default;
        if (!(token is JArray pair) || pair.Count < 2) return false;
        var x = pair[0].ToObject<double?>();
        var y = pair[1].ToObject<double?>();
        if (x == null || y == null) return false;
        point = new RoomDetect.Pt(x.Value, y.Value);
        return true;
    }
}
