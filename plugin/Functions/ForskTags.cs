using System;
using System.Globalization;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    private sealed class ForskStamp
    {
        public string Kind;
        public string Level = "0";
        public string Host;
        public string OpeningKind;
        public double? Sill;
        public double? Head;
        public double? Width;
        public string SourceLayer;
    }

    private static void StampForskTags(ObjectAttributes attr, ForskStamp stamp)
    {
        if (attr == null || stamp == null || string.IsNullOrEmpty(stamp.Kind))
            return;

        attr.SetUserString("forsk:kind", stamp.Kind);
        attr.SetUserString("forsk:level", string.IsNullOrEmpty(stamp.Level) ? "0" : stamp.Level);
        attr.SetUserString("forsk:generated", "1");

        if (!string.IsNullOrEmpty(stamp.Host))
            attr.SetUserString("forsk:host", stamp.Host);
        if (!string.IsNullOrEmpty(stamp.OpeningKind))
            attr.SetUserString("forsk:opening_kind", stamp.OpeningKind);
        if (stamp.Sill.HasValue)
            attr.SetUserString("forsk:sill", FormatMm(stamp.Sill.Value));
        if (stamp.Head.HasValue)
            attr.SetUserString("forsk:head", FormatMm(stamp.Head.Value));
        if (stamp.Width.HasValue)
            attr.SetUserString("forsk:width", FormatMm(stamp.Width.Value));
        if (!string.IsNullOrEmpty(stamp.SourceLayer))
            attr.SetUserString("forsk:source_layer", stamp.SourceLayer);
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
