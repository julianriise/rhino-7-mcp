using System;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    private static class ForskDefaults
    {
        public const double WallHeight = 3000.0;
        public const double FloorThickness = 400.0;
        public const double RoofThickness = 200.0;
        public const double DoorSill = 0.0;
        public const double DoorHead = 2100.0;
        public const double DoorWidth = 900.0;
        public const double WindowSill = 900.0;
        public const double WindowHead = 2100.0;
        public const double WindowWidth = 1200.0;
    }

    private sealed class ForskStamp
    {
        public string Kind;
        public string Level = "0";
        public string Id;
        public string Host;
        public string HostId;
        public string OpeningKind;
        public double? Sill;
        public double? Head;
        public double? Width;
        public double? Height;
        public double? Area;
        public string SourceLayer;
    }

    private static void StampForskTags(ObjectAttributes attr, ForskStamp stamp)
    {
        if (attr == null || stamp == null || string.IsNullOrEmpty(stamp.Kind))
            return;

        attr.SetUserString("forsk:kind", stamp.Kind);
        attr.SetUserString("forsk:level", string.IsNullOrEmpty(stamp.Level) ? "0" : stamp.Level);
        attr.SetUserString("forsk:generated", "1");

        if (!string.IsNullOrEmpty(stamp.Id))
            attr.SetUserString("forsk:id", stamp.Id);
        if (!string.IsNullOrEmpty(stamp.Host))
            attr.SetUserString("forsk:host", stamp.Host);
        if (!string.IsNullOrEmpty(stamp.HostId))
            attr.SetUserString("forsk:host_id", stamp.HostId);
        if (!string.IsNullOrEmpty(stamp.OpeningKind))
            attr.SetUserString("forsk:opening_kind", stamp.OpeningKind);
        if (stamp.Sill.HasValue)
            attr.SetUserString("forsk:sill", FormatMm(stamp.Sill.Value));
        if (stamp.Head.HasValue)
            attr.SetUserString("forsk:head", FormatMm(stamp.Head.Value));
        if (stamp.Width.HasValue)
            attr.SetUserString("forsk:width", FormatMm(stamp.Width.Value));
        if (stamp.Height.HasValue)
            attr.SetUserString("forsk:height", FormatMm(stamp.Height.Value));
        if (stamp.Area.HasValue)
            attr.SetUserString("forsk:area", FormatMm(stamp.Area.Value));
        if (!string.IsNullOrEmpty(stamp.SourceLayer))
            attr.SetUserString("forsk:source_layer", stamp.SourceLayer);
    }

    /// <summary>Bake-order id: w01 matches wall-01, r01 matches room-01.</summary>
    private static string FormatStableId(string prefix, int index)
    {
        return prefix + index.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static string ReadForskUserString(RhinoDoc doc, Guid objectId, string key)
    {
        if (doc == null || objectId == Guid.Empty || string.IsNullOrEmpty(key))
            return null;
        var obj = doc.Objects.FindId(objectId);
        return obj?.Attributes?.GetUserString(key);
    }

    private static void StampOpeningHostId(RhinoDoc doc, Guid markerId, Guid wallId)
    {
        var stable = ReadForskUserString(doc, wallId, "forsk:id");
        if (string.IsNullOrEmpty(stable)) return;
        var marker = doc.Objects.FindId(markerId);
        if (marker?.Attributes == null) return;
        marker.Attributes.SetUserString("forsk:host_id", stable);
        marker.CommitChanges();
    }

    private static bool IsForskGenerated(RhinoObject obj)
    {
        if (obj?.Attributes == null) return false;
        return obj.Attributes.GetUserString("forsk:generated") == "1";
    }

    private static string GetForskKind(RhinoObject obj)
    {
        return obj?.Attributes?.GetUserString("forsk:kind");
    }

    private static string FormatMm(double mm)
    {
        var rounded = Math.Round(mm);
        if (Math.Abs(mm - rounded) < 1e-9)
            return ((long)rounded).ToString(CultureInfo.InvariantCulture);
        return mm.ToString(CultureInfo.InvariantCulture);
    }
}
