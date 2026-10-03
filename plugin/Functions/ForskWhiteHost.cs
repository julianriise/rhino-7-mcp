using System;
using System.IO;
using System.Reflection;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Imports Forsk White once from a patched Shaded export, then assigns it to
/// model views. A later UpdateDisplayMode is not called. Layout pages and
/// their details are skipped.
/// </summary>
internal static class ForskWhiteHost
{
    const string SettingKey = "ForskWhite";
    const string EmbeddedName = "RhinoMCPPlugin.ForskWhite.shaded-export.ini";

    static readonly EventHandler<ViewEventArgs> ViewCreated = (_, args) => Apply(args?.View?.Document);
    static readonly EventHandler<DocumentEventArgs> DocCreated = (_, args) => Apply(args?.Document);
    static readonly EventHandler<DocumentOpenEventArgs> DocOpened = (_, args) => Apply(args?.Document);
    static readonly EventHandler<CommandEventArgs> CommandEnded = (_, args) =>
    {
        if (ForskWhite.ReassignsAfterCommand(args?.CommandEnglishName))
            Apply(args?.Document ?? RhinoDoc.ActiveDoc);
    };

    static bool _hooked;
    static bool _applying;

    internal static void Start()
    {
        if (_hooked) return;
        _hooked = true;
        RhinoView.Create += ViewCreated;
        RhinoDoc.NewDocument += DocCreated;
        RhinoDoc.EndOpenDocumentInitialViewUpdate += DocOpened;
        Command.EndCommand += CommandEnded;
        try { Apply(RhinoDoc.ActiveDoc); }
        catch (Exception ex) { Report(ex); }
    }

    internal static void Stop()
    {
        if (!_hooked) return;
        _hooked = false;
        RhinoView.Create -= ViewCreated;
        RhinoDoc.NewDocument -= DocCreated;
        RhinoDoc.EndOpenDocumentInitialViewUpdate -= DocOpened;
        Command.EndCommand -= CommandEnded;
    }

    internal static bool Enabled()
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return true;
        return settings.GetBool(SettingKey, true);
    }

    internal static void SetEnabled(bool enabled)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        settings.SetBool(SettingKey, enabled);
    }

    internal static void ApplyActive()
    {
        Apply(RhinoDoc.ActiveDoc);
    }

    static void Apply(RhinoDoc doc)
    {
        if (_applying || doc == null) return;
        _applying = true;
        try
        {
            var on = Enabled();
            var target = on ? Ensure() : Shaded();
            if (target == null) return;
            var changed = false;
            foreach (var view in doc.Views)
            {
                if (view == null || view is RhinoPageView) continue;
                var viewport = view.MainViewport;
                if (viewport == null) continue;
                var current = viewport.DisplayMode?.EnglishName;
                if (!ForskWhite.NeedsAssign(on, current, view.GetType().Name)) continue;
                viewport.DisplayMode = target;
                changed = true;
            }
            if (changed) doc.Views.Redraw();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            _applying = false;
        }
    }

    static DisplayModeDescription Shaded()
    {
        return DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
    }

    /// <summary>Find the imported mode. Never retune it.</summary>
    static DisplayModeDescription Ensure()
    {
        var existing = Find();
        if (existing != null) return existing;
        if (TryImport(LiveExport)) existing = Find();
        if (existing != null) return existing;
        if (TryImport(EmbeddedExport)) existing = Find();
        return existing;
    }

    static bool TryImport(Func<string> source)
    {
        try
        {
            var exported = source();
            if (string.IsNullOrEmpty(exported)) return false;
            var path = Path.Combine(Path.GetTempPath(), "forsk-white.ini");
            File.WriteAllText(path, ForskWhite.Patch(exported), new UTF8Encoding(false));
            DropCopies();
            DisplayModeDescription.ImportFromFile(path);
            return Find() != null;
        }
        catch (Exception ex)
        {
            Report(ex);
            return false;
        }
    }

    static string LiveExport()
    {
        var copied = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.ShadedId, ForskWhite.ModeName);
        var mode = copied == Guid.Empty ? null : DisplayModeDescription.GetDisplayMode(copied);
        if (mode == null || !NamedWhite(mode)) return null;
        var path = Path.Combine(Path.GetTempPath(), "forsk-white-export.ini");
        var wrote = false;
        try
        {
            wrote = DisplayModeDescription.ExportToFile(mode, path) && File.Exists(path);
            return wrote ? File.ReadAllText(path) : null;
        }
        finally
        {
            // The copy is the unpatched source. The import replaces it.
            DropId(copied);
        }
    }

    static string EmbeddedExport()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedName);
        if (stream == null) return null;
        using (stream)
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    static DisplayModeDescription Find()
    {
        var named = DisplayModeDescription.FindByName(ForskWhite.ModeName);
        if (named == null || !NamedWhite(named)) return null;
        if (named.Id == DisplayModeDescription.ShadedId) return null;
        return DisplayModeDescription.GetDisplayMode(named.Id) ?? named;
    }

    static void DropCopies()
    {
        var named = DisplayModeDescription.FindByName(ForskWhite.ModeName);
        if (named != null) DropId(named.Id);
    }

    static void DropId(Guid id)
    {
        if (id == Guid.Empty || id == DisplayModeDescription.ShadedId) return;
        var mode = DisplayModeDescription.GetDisplayMode(id);
        if (!NamedWhite(mode)) return;
        try { DisplayModeDescription.DeleteDisplayMode(id); }
        catch (Exception) { }
    }

    static bool NamedWhite(DisplayModeDescription mode)
    {
        var name = mode?.EnglishName;
        return name != null && name.Equals(ForskWhite.ModeName, StringComparison.Ordinal);
    }

    static void Report(Exception ex)
    {
        RhinoApp.WriteLine("Forsk White did not load: " + (ex?.Message ?? "unknown error"));
    }
}
