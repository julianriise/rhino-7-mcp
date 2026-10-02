using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Render;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Simple door and window frames in the opening void.
/// forsk:kind=opening so clear, sheets, and print already treat them as clay
/// and skip them when hatching wall poché. Markers stay opening_marker on A-OPEN.
/// Glass and the door leaf stay separate from the wood frame so each can
/// carry its own material. Wood members may still union with each other.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string OpeningBlockLayerPath = "A-OPEN::Block";
    private const string OpeningBlockDefDescription = "forsk opening";
    private const string ForskWoodName = "Forsk Wood";
    private const string ForskGlassName = "Forsk Glass";
    private const double ForskGlassTransparency = 0.8;
    private const double OpeningFrameFaceMm = OpeningElement.FrameFaceMm;
    private const double OpeningFrameInsetMm = OpeningElement.FrameInsetMm;
    private const double OpeningLeafMm = OpeningElement.LeafMm;
    private const double OpeningGlazeMm = OpeningElement.GlazeMm;
    private const double OpeningSillNoseMm = 16.0;
    private const double OpeningThresholdMm = OpeningElement.ThresholdMm;

    private sealed class OpeningPart
    {
        public Brep Geometry;
        public string Part;
        public bool Glass;
    }

    /// <summary>
    /// A-OPEN and A-OPEN::Block stay on. Frames, leaves and glass are the block
    /// on the child layer, and a hidden parent hides that child. Markers stay
    /// object-hidden on A-OPEN so shaded view does not draw the blue solid in
    /// front of the frame.
    /// </summary>
    private Layer EnsureOpeningBlockLayer(RhinoDoc doc)
    {
        var layer = EnsureOpeningBlockLayerCore(doc);
        HideOpeningMarkers(doc);
        return layer;
    }

    private Layer EnsureOpeningBlockLayerCore(RhinoDoc doc)
    {
        var parent = EnsureLayer(doc, "A-OPEN", Color.FromArgb(120, 160, 200));
        ShowLayer(doc, parent);
        var existing = FindLayerCaseInsensitive(doc, OpeningBlockLayerPath);
        if (existing != null)
        {
            var changed = StampPrintInk(existing);
            if (!existing.IsVisible)
            {
                existing.IsVisible = true;
                changed = true;
            }
            if (changed)
                doc.Layers.Modify(existing, existing.Index, true);
            return existing;
        }

        var created = new Layer
        {
            Name = "Block",
            Color = Color.FromArgb(186, 164, 140),
            IsVisible = true,
            ParentLayerId = parent.Id
        };
        var index = doc.Layers.Add(created);
        if (index < 0) return parent;
        var layer = doc.Layers.FindIndex(index);
        if (layer != null)
        {
            var changed = StampPrintInk(layer);
            if (!layer.IsVisible)
            {
                layer.IsVisible = true;
                changed = true;
            }
            if (changed)
                doc.Layers.Modify(layer, layer.Index, true);
        }
        return layer ?? parent;
    }

    /// <summary>
    /// Every opening marker is object-hidden. The A-OPEN layer stays on so the
    /// frame, leaf and glass on A-OPEN::Block stay visible. Outside an open
    /// undo record this writes no undo of its own.
    /// </summary>
    internal static void HideOpeningMarkers(RhinoDoc doc)
    {
        if (doc == null) return;
        var shown = new List<Guid>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            if (!obj.IsHidden) shown.Add(obj.Id);
        }
        if (shown.Count == 0) return;

        var record = doc.UndoRecordingEnabled;
        var active = doc.UndoRecordingIsActive;
        if (!active) doc.UndoRecordingEnabled = false;
        try
        {
            foreach (var id in shown)
                doc.Objects.Hide(id, true);
        }
        finally
        {
            if (!active) doc.UndoRecordingEnabled = record;
        }
    }

    /// <summary>Turn a window or door layer on. A layer that is already on is left alone.</summary>
    private static void ShowLayer(RhinoDoc doc, Layer layer)
    {
        if (doc == null || layer == null || layer.IsVisible) return;
        layer.IsVisible = true;
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private static void HideOpeningMarker(RhinoDoc doc, Guid id)
    {
        if (doc == null || id == Guid.Empty) return;
        var obj = doc.Objects.FindId(id);
        if (obj == null || obj.IsHidden) return;
        if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase)) return;
        doc.Objects.Hide(id, true);
    }

    private Guid AddOpeningBlock(
        RhinoDoc doc,
        Guid markerId,
        string markerName,
        Guid hostId,
        string openingKind,
        OpeningFootprint foot,
        double sill,
        double head,
        double width,
        double pad,
        string sourceLayer,
        Brep hostBrep,
        WallSegment segment)
    {
        if (doc == null || markerId == Guid.Empty || foot == null) return Guid.Empty;
        if (width <= 1 || head <= sill) return Guid.Empty;

        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        if (!ResolveOpeningAxes(
                hostBrep, foot, segment, sill, head, width, pad, tol,
                out var center, out var widthDir, out var thickDir, out var thickness))
            return Guid.Empty;

        var kindTag = string.Equals(openingKind, "window", StringComparison.OrdinalIgnoreCase)
            ? "window"
            : "door";
        var style = ResolvedOpeningStyle(doc, markerId, kindTag);
        WriteOpeningStyle(doc, markerId, style);
        if (!TryOpeningPlane(center, widthDir, thickDir, out var wall))
            return Guid.Empty;
        var inward = segment != null ? segment.Inward : thickDir;
        OpeningFacing(wall, inward, out var yInward, out var xLeft);
        // The definition is the left-hand, plane-relative swing. Hand and the
        // wall's X mirror live on the instance transform.
        var built = DefinitionStyle(style, yInward);
        var key = OpeningBlockShare.Key.From(
            kindTag, width, sill, head, thickness, pad, built.TypeId, built.Swing);
        if (key.HeightMm <= 0 || key.WidthMm <= 0 || key.FrameMm <= 0) return Guid.Empty;
        var canonical = new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis);
        var parts = BuildOpeningBlockParts(
            canonical, 1, 1, key.WidthMm, key.FrameMm, key.SillMm, key.SillMm + key.HeightMm, key.PadMm, built, tol);
        if (parts.Count == 0) return Guid.Empty;

        var layer = EnsureOpeningBlockLayer(doc);
        var name = string.IsNullOrWhiteSpace(markerName) ? "opening-block" : markerName + "-block";
        var attr = new ObjectAttributes
        {
            Name = name,
            LayerIndex = layer.Index,
            ColorSource = ObjectColorSource.ColorFromLayer,
            MaterialSource = ObjectMaterialSource.MaterialFromLayer
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "opening",
            Level = "0",
            Host = hostId.ToString(),
            HostId = ReadForskUserString(doc, hostId, "forsk:id"),
            OpeningKind = kindTag,
            Sill = sill,
            Head = head,
            Width = width,
            SourceLayer = sourceLayer,
            MarkerId = markerId.ToString()
        });
        StampOpeningStyle(attr, style);
        attr.SetUserString("forsk:parts", FormatOpeningParts(parts));

        var mirror = OpeningTypes.HandSign(style.Hand, xLeft) < 0;
        var placed = OpeningBlockShare.Placement.On(
            wall.OriginX, wall.OriginY, wall.XAxis.X, wall.XAxis.Y, wall.YAxis.X, wall.YAxis.Y, mirror);
        var id = CommitOpeningBlock(doc, key, placed, attr, parts);
        return id;
    }

    /// <summary>
    /// Left hand, and the swing relative to the wall plane's +Y, so one
    /// definition serves every wall. The stored style on the marker is unchanged.
    /// </summary>
    private static OpeningTypes.Record DefinitionStyle(OpeningTypes.Record style, int yInward)
    {
        if (style == null) return null;
        var swing = style.Swing;
        if (!string.IsNullOrEmpty(swing))
            swing = OpeningTypes.SwingSign(style, yInward) < 0 ? "out" : "in";
        var hand = string.IsNullOrEmpty(style.Hand) ? null : "L";
        OpeningTypes.Record built;
        if (OpeningTypes.TryRead(style.Kind, style.TypeId, hand, swing, out built, out _))
            return built;
        return style;
    }

    private void DeleteOpeningBlocks(RhinoDoc doc, Guid markerId)
    {
        if (doc == null || markerId == Guid.Empty) return;
        var key = markerId.ToString();
        var doomed = new List<RhinoObject>();
        var seen = new HashSet<Guid>();
        foreach (var obj in OpeningLayerObjects(doc))
        {
            if (obj == null || !seen.Add(obj.Id)) continue;
            if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            var mid = obj.Attributes?.GetUserString("forsk:marker_id");
            if (!string.Equals(mid, key, StringComparison.OrdinalIgnoreCase))
                continue;
            doomed.Add(obj);
        }

        foreach (var obj in doomed)
        {
            // The definition stays while another opening still instances it.
            var defIndex = OpeningBlockDefIndex(obj);
            doc.Objects.Delete(obj.Id, true);
            DeleteOpeningDefinitionIfUnused(doc, defIndex);
        }
    }

    /// <summary>
    /// Point every opening that still has its own block at the shared definition
    /// for its size, and delete the private definitions afterwards. An opening
    /// that already instances a shared definition is left as it is.
    /// </summary>
    public int CollapseOpeningBlocks(RhinoDoc doc)
    {
        if (doc == null) return 0;
        var markers = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase))
                markers.Add(obj);
        }
        var rebuilt = 0;
        foreach (var marker in markers)
        {
            var blockId = FindOpeningBlock(doc, marker.Id);
            var block = blockId == Guid.Empty ? null : doc.Objects.FindId(blockId);
            var desc = (block as InstanceObject)?.InstanceDefinition?.Description ?? "";
            if (desc.StartsWith(OpeningBlockShare.TokenPrefix, StringComparison.Ordinal))
                continue;
            if (block == null) continue;
            try
            {
                var rec = ReadOpeningRecord(marker);
                var host = ReadHostWall(doc, rec.HostId, false);
                var kept = block.Attributes?.GetUserString("forsk:id");
                var along = block.Attributes?.GetUserString("forsk:t");
                var offset = block.Attributes?.GetUserString("forsk:offset");
                var oldId = block.Id;
                var oldDef = OpeningBlockDefIndex(block);
                // The new instance is added first. A miss leaves the private block in place.
                var id = AddOpeningBlock(
                    doc,
                    marker.Id,
                    marker.Name,
                    host.Id,
                    KindToTag(rec.Kind),
                    new OpeningFootprint { Bbox = rec.MarkerBbox },
                    rec.Sill,
                    rec.Head,
                    rec.Width,
                    FacadeConst.Pad,
                    marker.Attributes?.GetUserString("forsk:source_layer"),
                    host.Brep,
                    null);
                if (id == Guid.Empty) continue;
                var freshDef = OpeningBlockDefIndex(doc.Objects.FindId(id));
                if (!doc.Objects.Delete(oldId, true))
                {
                    doc.Objects.Delete(id, true);
                    DeleteOpeningDefinitionIfUnused(doc, freshDef);
                    continue;
                }
                DeleteOpeningDefinitionIfUnused(doc, oldDef);
                WriteOpeningBlockId(doc, id, kept);
                CopyOpeningString(doc, id, "forsk:t", along);
                CopyOpeningString(doc, id, "forsk:offset", offset);
                rebuilt++;
            }
            catch (Exception)
            {
                // A miss before the old instance is deleted leaves that opening as it was.
            }
        }
        PurgeOpeningBlockDefinitions(doc);
        return rebuilt;
    }

    static void CopyOpeningString(RhinoDoc doc, Guid id, string key, string value)
    {
        if (doc == null || id == Guid.Empty || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        obj.Attributes.SetUserString(key, value);
        obj.CommitChanges();
    }

    /// <summary>
    /// The block's forsk:id, read before it is deleted and written onto the
    /// replacement so a resize keeps the opening's id.
    /// </summary>
    private static string ReadOpeningBlockId(RhinoDoc doc, Guid markerId)
    {
        if (doc == null || markerId == Guid.Empty) return null;
        var key = markerId.ToString();
        foreach (var obj in OpeningLayerObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(obj.Attributes.GetUserString("forsk:marker_id"), key, StringComparison.OrdinalIgnoreCase))
                continue;
            var id = obj.Attributes.GetUserString("forsk:id");
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }

    private static void WriteOpeningBlockId(RhinoDoc doc, Guid blockId, string forskId)
    {
        if (doc == null || blockId == Guid.Empty || string.IsNullOrEmpty(forskId)) return;
        var block = doc.Objects.FindId(blockId);
        if (block?.Attributes == null) return;
        block.Attributes.SetUserString("forsk:id", forskId);
        block.CommitChanges();
    }

    /// <summary>
    /// Markers and blocks, including ones on a layer that is off.
    /// GetObjectList skips that layer. FindByLayer does not.
    /// </summary>
    private static IEnumerable<RhinoObject> OpeningLayerObjects(RhinoDoc doc)
    {
        if (doc == null) yield break;
        foreach (var obj in EnumerateDocObjects(doc))
            if (obj != null) yield return obj;
        foreach (var obj in ObjectsOnLayer(doc, "A-OPEN"))
            yield return obj;
        foreach (var obj in ObjectsOnLayer(doc, "A-OPEN::Block"))
            yield return obj;
    }

    private static IEnumerable<RhinoObject> ObjectsOnLayer(RhinoDoc doc, string name)
    {
        if (doc == null || string.IsNullOrEmpty(name)) yield break;
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            var full = layer.FullPath ?? "";
            if (!layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            RhinoObject[] found = null;
            try { found = doc.Objects.FindByLayer(layer); }
            catch (Exception) { found = null; }
            if (found == null) continue;
            foreach (var obj in found)
                if (obj != null) yield return obj;
        }
    }

    private static void PurgeOpeningBlockDefinitions(RhinoDoc doc)
    {
        if (doc == null) return;
        for (var i = doc.InstanceDefinitions.Count - 1; i >= 0; i--)
        {
            var idef = doc.InstanceDefinitions[i];
            if (idef == null || idef.IsDeleted) continue;
            if (!IsOpeningBlockDefinition(idef))
                continue;
            if (OpeningDefinitionInUse(doc, i)) continue;
            doc.InstanceDefinitions.Delete(i, true, true);
        }
    }

    private static RhinoObject ResolveOpeningHandle(RhinoObject obj)
    {
        if (obj == null) return null;
        var doc = obj.Document ?? RhinoDoc.ActiveDoc;
        var id = OpeningResolve.MarkerOf(OpeningParts(doc, obj), ToOpeningPart(obj));
        if (string.IsNullOrEmpty(id) || !Guid.TryParse(id, out var guid) || guid == obj.Id)
            return obj;
        // Find skips a hidden object. Markers stay object-hidden on A-OPEN.
        var marker = doc?.Objects.FindId(guid);
        return marker ?? obj;
    }

    /// <summary>Markers for this selection, one per opening. Does not call ResolveOpeningHandle.</summary>
    private static List<RhinoObject> MarkersOfSelection(RhinoDoc doc, IList<RhinoObject> selected)
    {
        var markers = new List<RhinoObject>();
        if (doc == null || selected == null || selected.Count == 0) return markers;
        var picked = new List<OpeningResolve.Part>();
        foreach (var obj in selected)
            if (obj != null) picked.Add(ToOpeningPart(obj));
        foreach (var id in OpeningResolve.Selected(OpeningParts(doc, null), picked))
        {
            if (!Guid.TryParse(id, out var guid)) continue;
            var marker = doc.Objects.FindId(guid);
            if (marker != null) markers.Add(marker);
        }
        return markers;
    }

    private static List<OpeningResolve.Part> OpeningParts(RhinoDoc doc, RhinoObject extra)
    {
        var parts = new List<OpeningResolve.Part>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc != null)
        {
            foreach (var obj in EnumerateDocObjects(doc))
            {
                var part = ToOpeningPart(obj);
                if (part == null || string.IsNullOrEmpty(part.Id) || !seen.Add(part.Id)) continue;
                parts.Add(part);
            }
        }
        var extraPart = ToOpeningPart(extra);
        if (extraPart != null && !string.IsNullOrEmpty(extraPart.Id) && seen.Add(extraPart.Id))
            parts.Add(extraPart);
        return parts;
    }

    private static OpeningResolve.Part ToOpeningPart(RhinoObject obj)
    {
        if (obj == null) return null;
        var part = new OpeningResolve.Part
        {
            Id = obj.Id.ToString(),
            Kind = GetForskKind(obj),
            Member = obj.Attributes?.GetUserString("forsk:part"),
            Marker = obj.Attributes?.GetUserString("forsk:marker_id")
                ?? obj.Attributes?.GetUserString("forsk:marker"),
            ForskId = obj.Attributes?.GetUserString("forsk:id"),
            Group = GroupKey(obj)
        };
        FillMarkerFromDefinition(obj, part);
        return part;
    }

    /// <summary>
    /// A legacy block can carry no user strings of its own, so the marker is
    /// read off its private definition. A shared definition has no marker.
    /// </summary>
    private static void FillMarkerFromDefinition(RhinoObject obj, OpeningResolve.Part part)
    {
        if (part == null || !string.IsNullOrEmpty(part.Marker)) return;
        var inst = obj as InstanceObject;
        if (inst?.InstanceDefinition == null) return;
        RhinoObject[] members;
        try { members = inst.InstanceDefinition.GetObjects(); }
        catch (Exception) { return; }
        if (members == null) return;
        foreach (var member in members)
        {
            var marker = member?.Attributes?.GetUserString("forsk:marker_id");
            if (string.IsNullOrWhiteSpace(marker)) continue;
            part.Marker = marker;
            if (string.IsNullOrEmpty(part.Kind)) part.Kind = "opening";
            if (string.IsNullOrEmpty(part.Member))
                part.Member = member.Attributes.GetUserString("forsk:part");
            break;
        }
    }

    private static int OpeningBlockDefIndex(RhinoObject obj)
    {
        var idef = (obj as InstanceObject)?.InstanceDefinition;
        if (idef == null || !IsOpeningBlockDefinition(idef))
            return -1;
        return idef.Index;
    }

    static bool IsOpeningBlockDefinition(InstanceDefinition idef)
    {
        var desc = idef?.Description ?? "";
        return desc.Equals(OpeningBlockDefDescription, StringComparison.Ordinal)
            || desc.StartsWith(OpeningBlockShare.TokenPrefix, StringComparison.Ordinal);
    }

    /// <summary>True when a live instance still points at this definition.</summary>
    static bool OpeningDefinitionInUse(RhinoDoc doc, int index)
    {
        if (doc == null || index < 0 || index >= doc.InstanceDefinitions.Count) return false;
        var idef = doc.InstanceDefinitions[index];
        if (idef == null || idef.IsDeleted) return false;
        InstanceObject[] refs = null;
        try { refs = idef.GetReferences(-1); }
        catch (Exception) { return true; }
        if (refs == null) return false;
        foreach (var refer in refs)
            if (refer != null && !refer.IsDeleted) return true;
        return false;
    }

    /// <summary>Drop a definition only after its last instance is gone. A shared one stays.</summary>
    static bool DeleteOpeningDefinitionIfUnused(RhinoDoc doc, int index)
    {
        if (OpeningDefinitionInUse(doc, index)) return false;
        try { return doc.InstanceDefinitions.Delete(index, true, true); }
        catch (Exception) { return false; }
    }

    int FindOpeningDefinition(RhinoDoc doc, OpeningBlockShare.Key key)
    {
        if (doc == null || key == null) return -1;
        var token = key.Token;
        for (var i = 0; i < doc.InstanceDefinitions.Count; i++)
        {
            var idef = doc.InstanceDefinitions[i];
            if (idef == null || idef.IsDeleted) continue;
            if (string.Equals(idef.Description, token, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    static string PickDefinitionName(RhinoDoc doc, OpeningBlockShare.Key key)
    {
        var readable = OpeningBlockShare.Readable(key);
        if (!DefinitionNameTaken(doc, readable)) return readable;
        var suffixed = readable + " · " + OpeningBlockShare.Suffix(key);
        if (!DefinitionNameTaken(doc, suffixed)) return suffixed;
        for (var n = 2; ; n++)
        {
            var candidate = suffixed + " " + n.ToString(CultureInfo.InvariantCulture);
            if (!DefinitionNameTaken(doc, candidate)) return candidate;
        }
    }

    static bool DefinitionNameTaken(RhinoDoc doc, string name)
    {
        if (doc == null || string.IsNullOrEmpty(name)) return false;
        for (var i = 0; i < doc.InstanceDefinitions.Count; i++)
        {
            var idef = doc.InstanceDefinitions[i];
            if (idef == null || idef.IsDeleted) continue;
            if (string.Equals(idef.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static Transform OpeningInstanceTransform(OpeningBlockShare.Placement placed)
    {
        var transform = Transform.Identity;
        transform.M00 = placed.Xx;
        transform.M10 = placed.Xy;
        transform.M01 = placed.Yx;
        transform.M11 = placed.Yy;
        transform.M22 = 1;
        transform.M03 = placed.Ox;
        transform.M13 = placed.Oy;
        return transform;
    }

    private Guid CommitOpeningBlock(
        RhinoDoc doc, OpeningBlockShare.Key key, OpeningBlockShare.Placement placed, ObjectAttributes attr, List<OpeningPart> parts)
    {
        var index = FindOpeningDefinition(doc, key);
        var created = false;
        if (index < 0)
        {
            var geom = new List<GeometryBase>();
            var attrs = new List<ObjectAttributes>();
            foreach (var part in parts)
            {
                if (part?.Geometry == null || !part.Geometry.IsValid) continue;
                if (string.IsNullOrEmpty(part.Part)) continue;
                // Part geometry only. The marker and forsk:id stay on the instance.
                var partAttr = new ObjectAttributes
                {
                    Name = part.Part,
                    LayerIndex = attr.LayerIndex,
                    ColorSource = ObjectColorSource.ColorFromLayer
                };
                partAttr.SetUserString("forsk:part", part.Part);
                ApplyOpeningPartMaterial(doc, partAttr, part.Glass);
                geom.Add(part.Geometry);
                attrs.Add(partAttr);
            }
            if (geom.Count == 0) return Guid.Empty;

            var defName = PickDefinitionName(doc, key);
            index = doc.InstanceDefinitions.Add(
                defName, key.Token, Point3d.Origin, geom, attrs);
            if (index < 0)
            {
                var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                index = doc.InstanceDefinitions.Add(
                    defName + " " + suffix, key.Token, Point3d.Origin, geom, attrs);
            }
            // No block, no parts: loose parts would pick one at a time (selection S2).
            if (index < 0) return Guid.Empty;
            created = true;
            BindOpeningPartAttributes(doc, index, attrs);
        }

        // The instance keeps the opening identity. It does not carry one
        // material, so definition objects keep wood and glass.
        attr.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
        attr.MaterialIndex = -1;
        attr.ColorSource = ObjectColorSource.ColorFromLayer;

        var id = doc.Objects.AddInstanceObject(index, OpeningInstanceTransform(placed), attr);
        if (id != Guid.Empty) return id;
        if (created) DeleteOpeningDefinitionIfUnused(doc, index);
        return Guid.Empty;
    }

    private static void BindOpeningPartAttributes(
        RhinoDoc doc, int defIndex, IList<ObjectAttributes> attrs)
    {
        var idef = doc.InstanceDefinitions[defIndex];
        var members = idef?.GetObjects();
        if (members == null) return;
        var count = Math.Min(members.Length, attrs.Count);
        for (var i = 0; i < count; i++)
        {
            if (members[i] == null || attrs[i] == null) continue;
            try { doc.Objects.ModifyAttributes(members[i], attrs[i], true); }
            catch (Exception) { }
        }
    }

    private void ApplyOpeningPartMaterial(RhinoDoc doc, ObjectAttributes attr, bool glass)
    {
        var rm = glass ? EnsureForskGlass(doc) : EnsureForskWood(doc);
        if (rm != null)
        {
            try
            {
                attr.RenderMaterial = rm;
            }
            catch (Exception)
            {
                var index = doc.Materials.Find(glass ? ForskGlassName : ForskWoodName, true);
                if (index >= 0)
                {
                    attr.MaterialIndex = index;
                    attr.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                }
            }
        }

        if (glass)
        {
            attr.ColorSource = ObjectColorSource.ColorFromObject;
            attr.ObjectColor = Color.FromArgb(180, 196, 216, 220);
            attr.PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer;
        }
        else
        {
            attr.ColorSource = ObjectColorSource.ColorFromLayer;
        }
    }

    /// <summary>
    /// Create once, then reuse by name. Rendered, Arctic, and Raytraced read
    /// the render material. Legacy transparency covers Rendered if PBR is absent.
    /// </summary>
    private static RenderMaterial EnsureForskWood(RhinoDoc doc)
    {
        return EnsureOpeningRenderMaterial(doc, ForskWoodName, glass: false);
    }

    private static RenderMaterial EnsureForskGlass(RhinoDoc doc)
    {
        return EnsureOpeningRenderMaterial(doc, ForskGlassName, glass: true);
    }

    private static RenderMaterial EnsureOpeningRenderMaterial(RhinoDoc doc, string name, bool glass)
    {
        var existing = FindRenderMaterialByName(doc, name);
        if (existing != null) return existing;

        var index = doc.Materials.Find(name, true);
        if (index >= 0)
        {
            var linked = doc.Materials[index]?.RenderMaterial;
            if (linked != null)
            {
                if (FindRenderMaterialByName(doc, name) == null)
                    doc.RenderMaterials.Add(linked);
                return FindRenderMaterialByName(doc, name) ?? linked;
            }
        }

        var mat = glass ? NewForskGlass() : NewForskWood();
        index = doc.Materials.Add(mat);
        if (index < 0) return null;
        var created = doc.Materials[index]?.RenderMaterial;
        if (created == null) return null;
        if (!string.Equals(created.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            created.BeginChange(RenderContent.ChangeContexts.Program);
            created.Name = name;
            created.EndChange();
        }
        if (FindRenderMaterialByName(doc, name) == null)
            doc.RenderMaterials.Add(created);
        return FindRenderMaterialByName(doc, name) ?? created;
    }

    private static Material NewForskWood()
    {
        var color = Color.FromArgb(150, 98, 58);
        var mat = new Material
        {
            Name = ForskWoodName,
            DiffuseColor = color,
            AmbientColor = Color.Black,
            SpecularColor = Color.FromArgb(40, 28, 18),
            EmissionColor = Color.Black,
            Reflectivity = 0.03,
            Shine = 6,
            Transparency = 0.0,
            ReflectionGlossiness = 0.12,
            FresnelReflections = false
        };
        TryApplyPbr(mat, color, 1.0, 0.72, 1.5);
        return mat;
    }

    private static Material NewForskGlass()
    {
        var tint = Color.FromArgb(198, 216, 220);
        var mat = new Material
        {
            Name = ForskGlassName,
            DiffuseColor = tint,
            AmbientColor = Color.FromArgb(16, 20, 22),
            SpecularColor = Color.FromArgb(210, 210, 210),
            EmissionColor = Color.Black,
            Reflectivity = 0.12,
            Shine = 140,
            Transparency = ForskGlassTransparency,
            IndexOfRefraction = 1.52,
            ReflectionGlossiness = 0.95,
            FresnelReflections = true
        };
        TryApplyPbr(mat, tint, 1.0 - ForskGlassTransparency, 0.04, 1.52);
        return mat;
    }

    private static void TryApplyPbr(
        Material mat, Color color, double opacity, double roughness, double ior)
    {
        try
        {
            mat.ToPhysicallyBased();
            var pbr = mat.PhysicallyBased;
            if (pbr == null) return;
            pbr.BaseColor = new Color4f(color);
            pbr.Opacity = opacity;
            pbr.Roughness = roughness;
            pbr.Metallic = 0.0;
            pbr.OpacityIOR = ior;
            pbr.SynchronizeLegacyMaterial();
        }
        catch (Exception)
        {
        }
    }

    private static OpeningTypes.Record ResolvedOpeningStyle(RhinoDoc doc, Guid markerId, string kind)
    {
        var marker = doc?.Objects.FindId(markerId);
        var type = marker?.Attributes?.GetUserString(OpeningTypes.TypeKey);
        var hand = marker?.Attributes?.GetUserString(OpeningTypes.HandKey);
        var swing = marker?.Attributes?.GetUserString(OpeningTypes.SwingKey);
        var glazed = marker?.Attributes?.GetUserString(OpeningTypes.GlazedKey);
        if (OpeningTypes.TryRead(kind, type, hand, swing, glazed, out var record, out _))
            return record;
        return OpeningTypes.DefaultRecord(kind);
    }

    private static void WriteOpeningStyle(RhinoDoc doc, Guid id, OpeningTypes.Record style)
    {
        if (doc == null || id == Guid.Empty || style == null) return;
        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        StampOpeningStyle(obj.Attributes, style);
        obj.CommitChanges();
    }

    private static string FormatOpeningParts(List<OpeningPart> parts)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (parts != null)
        {
            foreach (var part in parts)
            {
                if (part == null || string.IsNullOrEmpty(part.Part)) continue;
                names.Add(part.Part);
            }
        }
        return string.Join(",", names);
    }

    private static List<OpeningPart> BuildOpeningBlockParts(
        Plane plane,
        int yInward,
        int xLeft,
        double width,
        double thickness,
        double sill,
        double head,
        double pad,
        OpeningTypes.Record style,
        double tol)
    {
        var parts = new List<OpeningPart>();
        if (style == null || !plane.IsValid) return parts;

        var window = string.Equals(style.Kind, "window", StringComparison.OrdinalIgnoreCase);
        if (!OpeningElement.TryLayout(window, width, sill, head, thickness, pad, out var layout))
            return parts;
        // Face and depth come from the layout, so a resize lengthens members
        // and does not thicken the profile. Depth is the host thickness.
        var z0 = layout.Z0;
        var z1 = layout.Z1;
        var outerHalf = layout.OuterHalf;
        var halfThick = layout.HalfThick;
        var face = layout.Face;
        var innerHalf = layout.InnerHalf;

        var boolTol = Math.Max(tol, 0.1);
        // Window jambs start on the sill. The sill itself is a separate part.
        var frameZ0 = window ? z0 + face - 0.2 : z0;
        var frame = BuildFrameSolid(
            plane, outerHalf, innerHalf, halfThick, face, frameZ0, z1, boolTol);
        if (frame != null)
            parts.Add(new OpeningPart { Geometry = frame, Part = "frame", Glass = false });
        else
            AddWoodParts(parts, FrameMembers(
                plane, outerHalf, innerHalf, halfThick, face, frameZ0, z1), "frame", boolTol);
        if (parts.Count == 0) return parts;

        if (window)
            AddWindowContents(
                parts, plane, style, innerHalf, outerHalf, halfThick, face, z0, z1,
                yInward, xLeft, boolTol);
        else
            AddDoorContents(
                parts, plane, style, innerHalf, outerHalf, halfThick, face, z0, z1,
                yInward, xLeft, boolTol);
        return parts;
    }

    /// <summary>
    /// Interior left is Inward × Z. +Y of the frame plane may be flipped so Z stays up.
    /// </summary>
    private static void OpeningFacing(Plane plane, Vector3d inward, out int yInward, out int xLeft)
    {
        var inn = inward;
        inn.Z = 0;
        if (!inn.Unitize())
            inn = plane.YAxis;
        yInward = plane.YAxis * inn >= 0 ? 1 : -1;
        var left = Vector3d.CrossProduct(inn, Vector3d.ZAxis);
        left.Z = 0;
        if (!left.Unitize())
            left = plane.XAxis;
        xLeft = plane.XAxis * left >= 0 ? 1 : -1;
    }

    private static void FaceSpan(
        int sign, double halfThick, double thick, double inset, out double y0, out double y1)
    {
        var outer = Math.Max(4.0, halfThick - inset);
        var depth = Math.Min(Math.Max(4.0, thick), outer);
        if (sign >= 0)
        {
            y1 = outer;
            y0 = outer - depth;
        }
        else
        {
            y0 = -outer;
            y1 = -outer + depth;
        }
    }

    private static void AddWindowContents(
        List<OpeningPart> parts,
        Plane plane,
        OpeningTypes.Record style,
        double innerHalf,
        double outerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        int yInward,
        int xLeft,
        double tol)
    {
        var sillParts = new List<Brep>();
        var rail = BuildSillRail(plane, outerHalf, halfThick, face, z0);
        var nose = BuildSillNose(plane, outerHalf, halfThick, face, z0);
        if (rail != null) sillParts.Add(rail);
        if (nose != null) sillParts.Add(nose);
        AddWoodParts(parts, sillParts, "sill", tol);

        if (string.Equals(style.TypeId, "window.fixed", StringComparison.Ordinal))
        {
            var glass = BuildPanel(plane, innerHalf, halfThick, face, z0, z1, true, 0);
            if (glass != null)
                parts.Add(new OpeningPart { Geometry = glass, Part = "glass", Glass = true });
            return;
        }

        var topHung = string.Equals(style.TypeId, "window.top_hung", StringComparison.Ordinal);
        var swingY = OpeningTypes.SwingSign(style, yInward);
        var hingeX = topHung ? 0 : OpeningTypes.HandSign(style, xLeft);
        AddHungSash(parts, plane, innerHalf, halfThick, face, z0, z1, swingY, hingeX, topHung, tol);
    }

    private static void AddHungSash(
        List<OpeningPart> parts,
        Plane plane,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        int swingY,
        int hingeX,
        bool topHung,
        double tol)
    {
        var sashThick = Math.Min(26.0, Math.Max(12.0, halfThick * 0.34));
        FaceSpan(swingY, halfThick, sashThick, 1.0, out var y0, out var y1);
        var sashFace = Math.Min(30.0, Math.Max(16.0, face * 0.6));
        var outer = Math.Max(24.0, innerHalf - 8);
        var inner = outer - sashFace;
        if (inner < 12) return;

        var sz0 = z0 + Math.Max(8.0, face * 0.4);
        var sz1 = z1 - Math.Max(8.0, face * 0.35);
        if (topHung) sz0 += 12.0;
        if (sz1 - sz0 < sashFace * 2 + 20) return;

        var leftInner = -inner;
        var rightInner = inner;
        if (!topHung && hingeX != 0)
        {
            var extra = Math.Min(8.0, sashFace * 0.35);
            if (hingeX > 0) rightInner = inner - extra;
            else leftInner = -(inner - extra);
        }

        var members = new List<Brep>();
        var left = FrameBox(plane, -outer, leftInner, y0, y1, sz0, sz1);
        var right = FrameBox(plane, rightInner, outer, y0, y1, sz0, sz1);
        var head = FrameBox(plane, -outer, outer, y0, y1, sz1 - sashFace, sz1);
        var bottomH = topHung ? sashFace * 0.65 : sashFace;
        var bottom = FrameBox(plane, -outer, outer, y0, y1, sz0, sz0 + bottomH);
        if (left != null) members.Add(left);
        if (right != null) members.Add(right);
        if (head != null) members.Add(head);
        if (bottom != null) members.Add(bottom);
        AddWoodParts(parts, members, "sash", tol);

        var gThick = Math.Min(OpeningGlazeMm, Math.Max(4.0, (y1 - y0) * 0.45));
        var mid = (y0 + y1) * 0.5;
        OpeningElement.GlassSpan(innerHalf, z0, z1, face, out var gx0, out var gx1, out var gz0, out var gz1);
        var glass = FrameBox(
            plane,
            gx0,
            gx1,
            mid - gThick * 0.5,
            mid + gThick * 0.5,
            gz0,
            gz1);
        if (glass != null)
            parts.Add(new OpeningPart { Geometry = glass, Part = "glass", Glass = true });
    }

    private static void AddDoorContents(
        List<OpeningPart> parts,
        Plane plane,
        OpeningTypes.Record style,
        double innerHalf,
        double outerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        int yInward,
        int xLeft,
        double tol)
    {
        var threshold = BuildThreshold(plane, innerHalf, halfThick, z0);
        var leafClear = threshold != null ? OpeningThresholdMm + 0.5 : 0.5;
        OpeningElement.LeafSpan(
            innerHalf, z0, z1, face, threshold != null,
            out var leafX0, out var leafX1, out var zLeaf0, out var zLeaf1);
        var id = style.TypeId ?? "";

        if (string.Equals(id, "door.sliding", StringComparison.Ordinal))
        {
            AddSlidingDoor(
                parts, plane, innerHalf, outerHalf, halfThick, face, zLeaf0, z1,
                yInward, xLeft, style.Hand);
        }
        else if (string.Equals(id, "door.hinged_double", StringComparison.Ordinal))
        {
            var swingY = OpeningTypes.SwingSign(style, yInward);
            const double gap = 3.0;
            AddFacedLeaf(
                parts, plane, -(innerHalf - 1), -gap, swingY, halfThick, zLeaf0, zLeaf1,
                -1, -(innerHalf - 1));
            AddFacedLeaf(
                parts, plane, gap, innerHalf - 1, swingY, halfThick, zLeaf0, zLeaf1,
                1, innerHalf - 1);
        }
        else if (string.Equals(id, "door.pocket", StringComparison.Ordinal))
        {
            var leaf = BuildPanel(plane, innerHalf, halfThick, face, z0, z1, false, leafClear);
            if (leaf != null)
                parts.Add(new OpeningPart { Geometry = leaf, Part = "leaf", Glass = false });
        }
        else
        {
            var swingY = OpeningTypes.SwingSign(style, yInward);
            var hingeSign = OpeningTypes.HandSign(style, xLeft);
            var hingeAt = hingeSign > 0 ? leafX1 : leafX0;
            AddFacedLeaf(
                parts, plane, leafX0, leafX1, swingY, halfThick, zLeaf0, zLeaf1,
                hingeSign, hingeAt);
        }

        if (threshold != null)
            parts.Add(new OpeningPart { Geometry = threshold, Part = "threshold", Glass = false });
    }

    private static void AddFacedLeaf(
        List<OpeningPart> parts,
        Plane plane,
        double x0,
        double x1,
        int swingY,
        double halfThick,
        double z0,
        double z1,
        int hingeSign,
        double hingeAt)
    {
        var leafThick = Math.Min(OpeningLeafMm, Math.Max(16.0, halfThick * 0.5));
        FaceSpan(swingY, halfThick, leafThick, 0.8, out var y0, out var y1);
        var leaf = FrameBox(plane, x0, x1, y0, y1, z0, z1);
        if (leaf != null)
            parts.Add(new OpeningPart { Geometry = leaf, Part = "leaf", Glass = false });
        AddHingeLeaves(parts, plane, hingeSign, hingeAt, y0, y1, z0, z1, halfThick);
    }

    private static void AddHingeLeaves(
        List<OpeningPart> parts,
        Plane plane,
        int hingeSign,
        double hingeAt,
        double y0,
        double y1,
        double z0,
        double z1,
        double halfThick)
    {
        if (hingeSign == 0 || z1 - z0 < 80) return;
        const double bulge = 8.0;
        double ky0;
        double ky1;
        if (y0 >= 0)
        {
            ky1 = y0;
            ky0 = y0 - bulge;
        }
        else
        {
            ky0 = y1;
            ky1 = y1 + bulge;
        }
        if (ky0 < -halfThick + 0.5 || ky1 > halfThick - 0.5) return;

        const double width = 22.0;
        double x0;
        double x1;
        if (hingeSign > 0)
        {
            x1 = hingeAt;
            x0 = hingeAt - width;
        }
        else
        {
            x0 = hingeAt;
            x1 = hingeAt + width;
        }

        var span = z1 - z0;
        var h = Math.Min(40.0, span * 0.12);
        double[] at =
        {
            z0 + span * 0.18,
            (z0 + z1) * 0.5 - h * 0.5,
            z1 - span * 0.18 - h
        };
        foreach (var z in at)
        {
            var box = FrameBox(plane, x0, x1, ky0, ky1, z, z + h);
            if (box == null) continue;
            parts.Add(new OpeningPart { Geometry = box, Part = "leaf", Glass = false });
        }
    }

    private static void AddSlidingDoor(
        List<OpeningPart> parts,
        Plane plane,
        double innerHalf,
        double outerHalf,
        double halfThick,
        double face,
        double zLeaf0,
        double z1,
        int yInward,
        int xLeft,
        string hand)
    {
        var faceY = OpeningTypes.TrackSign(yInward);
        var leafThick = Math.Min(OpeningLeafMm, Math.Max(16.0, halfThick * 0.45));
        FaceSpan(faceY, halfThick, leafThick, 1.2, out var y0, out var y1);
        var zLeaf1 = z1 - face - 6;
        if (zLeaf1 - zLeaf0 < 20) zLeaf1 = z1 - 8;
        var leaf = FrameBox(plane, -(innerHalf - 1), innerHalf - 1, y0, y1, zLeaf0, zLeaf1);
        if (leaf != null)
            parts.Add(new OpeningPart { Geometry = leaf, Part = "leaf", Glass = false });

        var park = OpeningTypes.HandSign(hand, xLeft);
        const double stile = 28.0;
        double sx0;
        double sx1;
        if (park > 0)
        {
            sx1 = innerHalf - 1;
            sx0 = sx1 - stile;
        }
        else
        {
            sx0 = -(innerHalf - 1);
            sx1 = sx0 + stile;
        }
        var parkStile = FrameBox(plane, sx0, sx1, y0, y1, zLeaf0, zLeaf1);
        if (parkStile != null)
            parts.Add(new OpeningPart { Geometry = parkStile, Part = "leaf", Glass = false });

        var trackThick = Math.Min(12.0, leafThick);
        FaceSpan(faceY, halfThick, trackThick, 0.6, out var ty0, out var ty1);
        var track = FrameBox(plane, -(outerHalf - 2), outerHalf - 2, ty0, ty1, z1 - face, z1 - 2);
        if (track != null)
            parts.Add(new OpeningPart { Geometry = track, Part = "track", Glass = false });
    }

    private static void AddWoodParts(
        List<OpeningPart> parts, IList<Brep> members, string part, double tol)
    {
        foreach (var brep in UnionWood(members, tol))
            parts.Add(new OpeningPart { Geometry = brep, Part = part, Glass = false });
    }

    /// <summary>
    /// Union wood with wood. On failure, keep the separate members.
    /// Never pass glass or a door leaf into this.
    /// </summary>
    private static List<Brep> UnionWood(IList<Brep> members, double tol)
    {
        var valid = new List<Brep>();
        if (members != null)
        {
            foreach (var brep in members)
            {
                if (brep != null && brep.IsValid)
                    valid.Add(brep);
            }
        }
        if (valid.Count <= 1) return valid;
        try
        {
            var united = Brep.CreateBooleanUnion(valid, tol);
            if (united == null) return valid;
            var ok = united.Where(b => b != null && b.IsValid).ToList();
            return ok.Count == 0 ? valid : ok;
        }
        catch (Exception)
        {
            return valid;
        }
    }

    private static Brep BuildFrameSolid(
        Plane plane,
        double outerHalf,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        double tol)
    {
        var outer = FrameBox(plane, -outerHalf, outerHalf, -halfThick, halfThick, z0, z1);
        if (outer == null) return null;

        // Cut through the bottom so the sill or threshold is not part of this solid.
        var innerZ0 = z0 - 8;
        var innerZ1 = z1 - face;
        var inner = FrameBox(
            plane, -innerHalf, innerHalf, -(halfThick + 8), halfThick + 8, innerZ0, innerZ1);
        if (inner == null) return null;

        try
        {
            var diff = Brep.CreateBooleanDifference(outer, inner, tol);
            if (diff == null) return null;
            var valid = diff.Where(b => b != null && b.IsValid).ToList();
            if (valid.Count == 1) return valid[0];
            if (valid.Count > 1)
            {
                var joined = Brep.JoinBreps(valid, tol);
                if (joined != null && joined.Length == 1 && joined[0] != null && joined[0].IsValid)
                    return joined[0];
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Jambs and head overlap in volume. The head is slightly shallower so the
    /// shared faces are not coplanar, which is where box unions fail.
    /// </summary>
    private static List<Brep> FrameMembers(
        Plane plane,
        double outerHalf,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1)
    {
        var members = new List<Brep>();
        var jambY0 = -halfThick;
        var jambY1 = halfThick;
        var railY0 = -(halfThick - 0.4);
        var railY1 = halfThick - 0.4;

        var left = FrameBox(plane, -outerHalf, -innerHalf, jambY0, jambY1, z0, z1);
        var right = FrameBox(plane, innerHalf, outerHalf, jambY0, jambY1, z0, z1);
        var head = FrameBox(plane, -outerHalf, outerHalf, railY0, railY1, z1 - face, z1);
        if (left != null) members.Add(left);
        if (right != null) members.Add(right);
        if (head != null) members.Add(head);
        return members;
    }

    private static Brep BuildPanel(
        Plane plane,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        bool window,
        double floorClear)
    {
        var thick = window ? OpeningGlazeMm : OpeningLeafMm;
        var half = Math.Min(thick * 0.5, Math.Max(3, halfThick * 0.45));
        double x0;
        double x1;
        double panelZ0;
        double panelZ1;
        if (window)
            OpeningElement.GlassSpan(innerHalf, z0, z1, face, out x0, out x1, out panelZ0, out panelZ1);
        else
        {
            const double bite = 2.0;
            x0 = -(innerHalf + bite);
            x1 = innerHalf + bite;
            panelZ0 = z0 + floorClear;
            panelZ1 = z1 - face + bite;
        }
        if (panelZ1 - panelZ0 < 10) return null;
        return FrameBox(plane, x0, x1, -half, half, panelZ0, panelZ1);
    }

    private static Brep BuildSillRail(
        Plane plane, double outerHalf, double halfThick, double face, double z0)
    {
        var y0 = -(halfThick - 0.4);
        var y1 = halfThick - 0.4;
        var z1 = z0 + face;
        if (z1 - z0 < 8) return null;
        return FrameBox(plane, -(outerHalf - 0.5), outerHalf - 0.5, y0, y1, z0, z1);
    }

    private static Brep BuildSillNose(Plane plane, double outerHalf, double halfThick, double face, double z0)
    {
        var y0 = halfThick - 2;
        var y1 = halfThick + OpeningSillNoseMm;
        var zNose0 = z0 + 1;
        var zNose1 = z0 + face - 1;
        if (zNose1 - zNose0 < 4) return null;
        return FrameBox(plane, -(outerHalf - 1), outerHalf - 1, y0, y1, zNose0, zNose1);
    }

    private static Brep BuildThreshold(Plane plane, double innerHalf, double halfThick, double z0)
    {
        if (innerHalf < 20 || halfThick < 10) return null;
        var x = Math.Max(8.0, innerHalf - 2.0);
        var y = Math.Max(6.0, halfThick - 3.0);
        return FrameBox(plane, -x, x, -y, y, z0, z0 + OpeningThresholdMm);
    }

    private static Brep FrameBox(
        Plane plane, double x0, double x1, double y0, double y1, double z0, double z1)
    {
        if (x1 - x0 < 0.5 || y1 - y0 < 0.5 || z1 - z0 < 0.5) return null;
        var box = new Box(
            plane,
            new Interval(x0, x1),
            new Interval(y0, y1),
            new Interval(z0, z1));
        if (!box.IsValid) return null;
        var brep = Brep.CreateFromBox(box);
        if (brep == null || !brep.IsValid) return null;
        return brep;
    }

    private static bool TryOpeningPlane(
        Point3d center, Vector3d widthDir, Vector3d thickDir, out Plane plane)
    {
        plane = Plane.Unset;
        widthDir.Z = 0;
        if (!widthDir.Unitize()) return false;
        thickDir.Z = 0;
        thickDir -= widthDir * (thickDir * widthDir);
        if (!thickDir.Unitize()) return false;
        plane = new Plane(new Point3d(center.X, center.Y, 0), widthDir, thickDir);
        // width × thickness can point down. The frame's Z must stay upright.
        if (plane.IsValid && plane.ZAxis * Vector3d.ZAxis < 0)
            plane = new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
        return plane.IsValid;
    }

    private static bool ResolveOpeningAxes(
        Brep host,
        OpeningFootprint foot,
        WallSegment segment,
        double sill,
        double head,
        double width,
        double pad,
        double tol,
        out Point3d center,
        out Vector3d widthDir,
        out Vector3d thickDir,
        out double thickness)
    {
        var bb = foot.Bbox;
        center = bb.IsValid ? bb.Center : Point3d.Origin;
        center.Z = 0;
        widthDir = Vector3d.XAxis;
        thickDir = Vector3d.YAxis;
        thickness = 200;

        if (segment != null && segment.Length > 1 && segment.Thickness > 40)
        {
            widthDir = segment.Tangent;
            widthDir.Z = 0;
            if (!widthDir.Unitize()) widthDir = Vector3d.XAxis;
            thickDir = segment.Inward;
            thickDir.Z = 0;
            thickDir -= widthDir * (thickDir * widthDir);
            if (!thickDir.Unitize())
                thickDir = new Vector3d(-widthDir.Y, widthDir.X, 0);
            thickness = segment.Thickness;
            return true;
        }

        if (!TryFootprintWidthDir(foot, tol, out widthDir))
            widthDir = Vector3d.XAxis;
        thickDir = new Vector3d(-widthDir.Y, widthDir.X, 0);
        if (!thickDir.Unitize()) thickDir = Vector3d.YAxis;

        if (host != null && TryMeasureWallThickness(
                host, center, widthDir, thickDir, sill, head, width, pad, tol,
                out var measured, out var axis))
        {
            thickness = measured;
            var shift = (axis - center) * thickDir;
            center += thickDir * shift;
            center.Z = 0;
            return true;
        }

        if (bb.IsValid)
        {
            var thin = Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            if (thin >= 80 && thin <= 600)
                thickness = thin;
        }
        return thickness >= 40;
    }

    private static bool TryFootprintWidthDir(OpeningFootprint foot, double tol, out Vector3d dir)
    {
        dir = Vector3d.XAxis;
        if (foot == null) return false;
        var bb = foot.Bbox;
        if (foot.Curve != null)
        {
            var best = 0.0;
            Line? longest = null;
            foreach (var line in ExplodeLineSegments(foot.Curve, tol))
            {
                var len = line.From.DistanceTo(line.To);
                if (len <= best) continue;
                best = len;
                longest = line;
            }
            if (longest.HasValue && best > 1)
            {
                dir = longest.Value.To - longest.Value.From;
                dir.Z = 0;
                if (dir.Unitize()) return true;
            }
        }

        if (!bb.IsValid) return false;
        var dx = bb.Max.X - bb.Min.X;
        var dy = bb.Max.Y - bb.Min.Y;
        dir = dx >= dy ? Vector3d.XAxis : Vector3d.YAxis;
        return true;
    }

    private static bool TryMeasureWallThickness(
        Brep host,
        Point3d center,
        Vector3d widthDir,
        Vector3d thickDir,
        double sill,
        double head,
        double width,
        double pad,
        double tol,
        out double thickness,
        out Point3d axisPoint)
    {
        thickness = 0;
        axisPoint = center;
        var wallBox = host.GetBoundingBox(true);
        if (!wallBox.IsValid) return false;

        var probes = new List<Point3d>();
        void Add(double z, double along)
        {
            if (z < wallBox.Min.Z + 5 || z > wallBox.Max.Z - 5) return;
            probes.Add(new Point3d(center.X, center.Y, 0) + widthDir * along + Vector3d.ZAxis * z);
        }

        var above = Math.Min(head + 40, wallBox.Max.Z - 20);
        var beside = width * 0.5 + Math.Max(pad, 0) + 80;
        Add(above, 0);
        Add(wallBox.Max.Z - 20, 0);
        Add(above, beside);
        Add(above, -beside);
        Add(Math.Min(Math.Max(sill - 30, wallBox.Min.Z + 20), wallBox.Max.Z - 20), beside);

        foreach (var probe in probes)
        {
            if (!TryWallSpan(host, probe, thickDir, tol, out var span, out var mid))
                continue;
            if (span < 60 || span > 900) continue;
            thickness = span;
            axisPoint = mid;
            axisPoint.Z = 0;
            return true;
        }
        return false;
    }

    private static bool TryWallSpan(
        Brep host, Point3d probe, Vector3d thickDir, double tol,
        out double thickness, out Point3d mid)
    {
        thickness = 0;
        mid = probe;
        var a = probe - thickDir * 8000;
        var b = probe + thickDir * 8000;
        var curve = new LineCurve(new Point3d(a.X, a.Y, probe.Z), new Point3d(b.X, b.Y, probe.Z));
        Point3d[] pts = null;
        try
        {
            if (!Intersection.CurveBrep(curve, host, Math.Max(tol, 0.1), out _, out pts))
                return false;
        }
        catch (Exception)
        {
            return false;
        }
        if (pts == null || pts.Length < 2) return false;

        var ts = new List<double>();
        foreach (var p in pts)
            ts.Add((p - probe) * thickDir);
        ts.Sort();
        var uniq = new List<double>();
        foreach (var t in ts)
        {
            if (uniq.Count == 0 || Math.Abs(t - uniq[uniq.Count - 1]) > 1.0)
                uniq.Add(t);
        }
        if (uniq.Count < 2) return false;

        var bestDist = double.MaxValue;
        var bestThick = 0.0;
        var bestMid = 0.0;
        for (var i = 0; i + 1 < uniq.Count; i += 2)
        {
            var t0 = uniq[i];
            var t1 = uniq[i + 1];
            var span = Math.Abs(t1 - t0);
            if (span < 60) continue;
            double dist;
            if (t0 <= 0 && t1 >= 0) dist = 0;
            else dist = Math.Min(Math.Abs(t0), Math.Abs(t1));
            if (dist > 500) continue;
            if (dist < bestDist - 1 || (Math.Abs(dist - bestDist) <= 1 && span < bestThick))
            {
                bestDist = dist;
                bestThick = span;
                bestMid = 0.5 * (t0 + t1);
            }
        }
        if (bestThick < 60) return false;
        thickness = bestThick;
        mid = new Point3d(probe.X, probe.Y, 0) + thickDir * bestMid;
        return true;
    }
}
