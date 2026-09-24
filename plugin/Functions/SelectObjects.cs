using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("select_objects")]
    public JObject SelectObjects(JObject parameters)
    {
        JObject filters = (JObject)parameters["filters"];
        if (filters == null)
            throw new InvalidOperationException("filters are required.");

        var doc = RhinoDoc.ActiveDoc;
        var filtersType = (string)parameters["filters_type"] ?? "and";
        if (filtersType != "and" && filtersType != "or")
            throw new InvalidOperationException($"Invalid filters_type '{filtersType}': expected 'and' or 'or'.");

        List<string> nameValues = null;
        int[] color = null;
        var customAttributes = new Dictionary<string, IList<string>>();
        foreach (JProperty field in filters.Properties())
        {
            if (field.Name == "name") nameValues = castToStringList(field.Value);
            else if (field.Name == "color") color = castToIntArray(field.Value);
            else customAttributes[field.Name] = castToStringList(field.Value);
        }

        var rows = new List<SelectPlan.Row>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            rows.Add(RowFor(obj));
        }

        var hits = SelectPlan.Match(
            rows,
            nameValues,
            color != null && color.Length >= 3,
            color != null && color.Length >= 3 ? color[0] : 0,
            color != null && color.Length >= 3 ? color[1] : 0,
            color != null && color.Length >= 3 ? color[2] : 0,
            customAttributes,
            filtersType == "or");

        try
        {
            doc.Objects.UnselectAll();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("UnselectAll: " + ex.Message);
        }

        var count = 0;
        foreach (var id in hits)
        {
            if (!Guid.TryParse(id, out var guid)) continue;
            var obj = doc.Objects.FindId(guid);
            if (obj == null) continue;
            try
            {
                // Persistent, and ignore a hidden layer, so a frame on A-OPEN
                // stays selected for the next command.
                if (obj.Select(true, true, true, true, true, true) > 0)
                    count++;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Select " + id + ": " + ex.Message);
            }
        }

        try { doc.Views.Redraw(); }
        catch (Exception ex) { RhinoApp.WriteLine("Redraw: " + ex.Message); }

        return new JObject { ["count"] = count };
    }

    private static SelectPlan.Row RowFor(RhinoObject obj)
    {
        if (obj == null || obj.Attributes == null)
        {
            return new SelectPlan.Row
            {
                Id = obj == null ? null : obj.Id.ToString(),
                HasAttributes = false
            };
        }

        string name = null;
        var red = 0;
        var green = 0;
        var blue = 0;
        Dictionary<string, string> strings = null;
        try { name = obj.Name; }
        catch (NullReferenceException) { name = null; }
        try
        {
            var color = obj.Attributes.ObjectColor;
            red = color.R;
            green = color.G;
            blue = color.B;
        }
        catch (NullReferenceException)
        {
            // A block instance can report color before its attributes exist.
        }
        try
        {
            var user = obj.Attributes.GetUserStrings();
            if (user != null)
            {
                strings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string key in user.AllKeys)
                {
                    if (!string.IsNullOrEmpty(key))
                        strings[key] = user[key];
                }
            }
        }
        catch (NullReferenceException)
        {
            strings = null;
        }

        return new SelectPlan.Row
        {
            Id = obj.Id.ToString(),
            Name = name,
            HasAttributes = true,
            R = red,
            G = green,
            B = blue,
            Strings = strings
        };
    }
}
