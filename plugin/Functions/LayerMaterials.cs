using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Render;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Layer-level RenderMaterial assign, matte architectural presets, and
/// viewport display-mode polish. Objects stay MaterialFromLayer (By Layer).
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class MaterialPreset
    {
        public string Id;
        public string MaterialName;
        public Color Diffuse;
    }

    private static readonly MaterialPreset[] MaterialPresets =
    {
        new MaterialPreset { Id = "plaster",  MaterialName = "M-PLASTER",  Diffuse = Color.FromArgb(225, 217, 204) },
        new MaterialPreset { Id = "concrete", MaterialName = "M-CONCRETE", Diffuse = Color.FromArgb(140, 140, 133) },
        new MaterialPreset { Id = "clay",     MaterialName = "M-CLAY",     Diffuse = Color.FromArgb(174, 132, 112) },
        new MaterialPreset { Id = "wood",     MaterialName = "M-WOOD",     Diffuse = Color.FromArgb(191, 155, 107) },
        new MaterialPreset { Id = "white",    MaterialName = "M-WHITE",    Diffuse = Color.FromArgb(255, 255, 255) },
    };

    [McpCommand("set_layer_material")]
    public JObject SetLayerMaterial(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active Rhino document.");

        var layerName = parameters["layer_name"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName))
            throw new ArgumentException("layer_name is required.");

        var ensureObjects = parameters["ensure_objects_from_layer"]?.ToObject<bool?>() ?? true;
        var rm = ResolveRenderMaterial(doc, parameters);
        var result = ApplyLayerMaterial(doc, layerName.Trim(), rm, ensureObjects, createLayerIfMissing: true);
        result["message"] =
            $"Assigned {rm.Name} to layer {result["layer"]} (By Layer, {result["objects_updated"]} object(s)).";
        return result;
    }

    [McpCommand("set_display_mode")]
    public JObject SetDisplayMode(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active Rhino document.");

        var modeName = parameters["mode"]?.ToString();
        if (string.IsNullOrWhiteSpace(modeName))
            throw new ArgumentException("mode is required (Rendered, Arctic, Shaded, Wireframe).");

        var view = doc.Views.ActiveView;
        if (view == null)
            throw new InvalidOperationException("No active viewport.");

        var mode = FindDisplayMode(modeName.Trim());
        if (mode == null)
            throw new InvalidOperationException(
                $"Display mode '{modeName}' not found. Use Rendered, Arctic, Shaded, or Wireframe.");

        view.ActiveViewport.DisplayMode = mode;
        doc.Views.Redraw();

        return new JObject
        {
            ["mode"] = mode.EnglishName,
            ["viewport"] = view.ActiveViewport.Name,
            ["message"] = $"Set viewport '{view.ActiveViewport.Name}' to {mode.EnglishName}."
        };
    }

    internal JObject ApplyLayerMaterialPreset(
        RhinoDoc doc,
        string layerName,
        string presetId,
        bool ensureObjectsFromLayer,
        bool createLayerIfMissing)
    {
        var rm = EnsurePresetMaterial(doc, presetId);
        return ApplyLayerMaterial(doc, layerName, rm, ensureObjectsFromLayer, createLayerIfMissing);
    }

    internal void TryApplyDefaultMaterial(
        RhinoDoc doc,
        string layerName,
        string presetId,
        JArray warnings,
        JObject result)
    {
        try
        {
            var applied = ApplyLayerMaterialPreset(
                doc, layerName, presetId, ensureObjectsFromLayer: true, createLayerIfMissing: false);
            result["material_name"] = applied["material_name"];
            result["objects_updated"] = applied["objects_updated"];
            var msg = result["message"]?.ToString() ?? string.Empty;
            var materialName = applied["material_name"]?.ToString() ?? presetId;
            result["message"] = string.IsNullOrEmpty(msg)
                ? $"Material {materialName} (By Layer)."
                : msg.TrimEnd('.') + $". Material {materialName} (By Layer).";
        }
        catch (Exception ex)
        {
            warnings.Add($"Default material '{presetId}' not applied: {ex.Message}");
        }
    }

    private RenderMaterial ResolveRenderMaterial(RhinoDoc doc, JObject parameters)
    {
        var preset = CanonicalPreset(parameters["preset"]?.ToString());
        if (!string.IsNullOrWhiteSpace(preset))
            return EnsurePresetMaterial(doc, preset);

        var materialName = parameters["material_name"]?.ToString();
        if (!string.IsNullOrWhiteSpace(materialName))
        {
            var existing = FindRenderMaterialByName(doc, materialName.Trim());
            if (existing == null)
                throw new InvalidOperationException(
                    $"Material '{materialName}' not found. Pass preset or diffuse_rgb + name to create it.");
            return existing;
        }

        var name = parameters["name"]?.ToString();
        var rgb = parameters["diffuse_rgb"]?.ToObject<int[]>();
        if (string.IsNullOrWhiteSpace(name) || rgb == null || rgb.Length < 3)
            throw new ArgumentException("Provide preset, material_name, or diffuse_rgb + name.");

        for (var i = 0; i < 3; i++)
        {
            if (rgb[i] < 0 || rgb[i] > 255)
                throw new ArgumentException("diffuse_rgb values must be 0–255.");
        }

        return EnsureRenderMaterial(doc, name.Trim(), Color.FromArgb(rgb[0], rgb[1], rgb[2]));
    }

    private RenderMaterial EnsurePresetMaterial(RhinoDoc doc, string presetId)
    {
        var spec = FindPreset(presetId);
        if (spec == null)
            throw new ArgumentException(
                $"Unknown preset '{presetId}'. Use plaster, concrete, clay, wood, or white.");
        return EnsureRenderMaterial(doc, spec.MaterialName, spec.Diffuse);
    }

    private RenderMaterial EnsureRenderMaterial(RhinoDoc doc, string name, Color diffuse)
    {
        var existing = FindRenderMaterialByName(doc, name);
        if (existing != null) return existing;

        var basic = new Material
        {
            Name = name,
            DiffuseColor = diffuse,
            AmbientColor = Color.Black,
            SpecularColor = Color.FromArgb(24, 24, 24),
            EmissionColor = Color.Black,
            Reflectivity = 0.0,
            Shine = 0.0,
            Transparency = 0.0,
            ReflectionGlossiness = 0.0,
            FresnelReflections = false
        };

        var rm = RenderMaterial.CreateBasicMaterial(basic, doc);
        if (rm == null)
            throw new InvalidOperationException($"Failed to create render material '{name}'.");

        if (!string.Equals(rm.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            rm.BeginChange(RenderContent.ChangeContexts.Program);
            rm.Name = name;
            rm.EndChange();
        }

        if (FindRenderMaterialByName(doc, name) == null)
            doc.RenderMaterials.Add(rm);

        return FindRenderMaterialByName(doc, name) ?? rm;
    }

    private JObject ApplyLayerMaterial(
        RhinoDoc doc,
        string layerName,
        RenderMaterial rm,
        bool ensureObjectsFromLayer,
        bool createLayerIfMissing)
    {
        var layer = FindLayerCaseInsensitive(doc, layerName);
        if (layer == null)
        {
            if (!createLayerIfMissing)
                throw new InvalidOperationException($"Layer '{layerName}' not found.");
            var color = LayerColorForName(layerName);
            layer = EnsureLayer(doc, layerName, color);
        }

        layer.RenderMaterial = rm;
        doc.Layers.Modify(layer, layer.Index, true);
        layer = doc.Layers.FindIndex(layer.Index) ?? layer;

        var objectsUpdated = 0;
        if (ensureObjectsFromLayer)
        {
            foreach (var obj in doc.Objects)
            {
                if (!ObjectOnLayer(doc, obj, layer)) continue;
                var attrs = obj.Attributes.Duplicate();
                if (attrs.MaterialSource != ObjectMaterialSource.MaterialFromLayer ||
                    attrs.MaterialIndex != -1)
                {
                    attrs.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
                    attrs.MaterialIndex = -1;
                    doc.Objects.ModifyAttributes(obj, attrs, true);
                }
                objectsUpdated++;
            }
        }

        doc.Views.Redraw();

        return new JObject
        {
            ["layer"] = layer.Name,
            ["material_name"] = rm.Name,
            ["material_id"] = rm.Id.ToString(),
            ["objects_updated"] = objectsUpdated
        };
    }

    private static RenderMaterial FindRenderMaterialByName(RhinoDoc doc, string name)
    {
        foreach (var rm in doc.RenderMaterials)
        {
            if (rm == null) continue;
            if (string.Equals(rm.Name, name, StringComparison.OrdinalIgnoreCase))
                return rm;
        }
        return null;
    }

    private static MaterialPreset FindPreset(string presetId)
    {
        var canonical = CanonicalPreset(presetId);
        if (string.IsNullOrEmpty(canonical)) return null;
        foreach (var preset in MaterialPresets)
        {
            if (preset.Id.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                return preset;
        }
        return null;
    }

    private static string CanonicalPreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return null;
        var key = preset.Trim();
        if (key.Equals("oak", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (key.Equals("terracotta", StringComparison.OrdinalIgnoreCase)) return "clay";
        return key;
    }

    private static Color LayerColorForName(string layerName)
    {
        if (layerName.Equals("A-FLOR", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(150, 145, 138);
        if (layerName.Equals("A-WALL", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(180, 180, 180);
        if (layerName.Equals("A-ROOF", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(70, 72, 76);
        return Color.FromArgb(180, 180, 180);
    }

    private static DisplayModeDescription FindDisplayMode(string modeName)
    {
        string canonical = modeName;
        if (modeName.Equals("rendered", StringComparison.OrdinalIgnoreCase)) canonical = "Rendered";
        else if (modeName.Equals("arctic", StringComparison.OrdinalIgnoreCase)) canonical = "Arctic";
        else if (modeName.Equals("shaded", StringComparison.OrdinalIgnoreCase)) canonical = "Shaded";
        else if (modeName.Equals("wireframe", StringComparison.OrdinalIgnoreCase)) canonical = "Wireframe";

        var named = DisplayModeDescription.FindByName(canonical);
        if (named != null) return named;

        foreach (var candidate in DisplayModeDescription.GetDisplayModes())
        {
            if (candidate == null) continue;
            if (candidate.EnglishName.Equals(modeName, StringComparison.OrdinalIgnoreCase) ||
                candidate.EnglishName.Equals(canonical, StringComparison.OrdinalIgnoreCase) ||
                candidate.LocalName.Equals(modeName, StringComparison.OrdinalIgnoreCase) ||
                candidate.LocalName.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }
}
