using System;
using System.IO;
using System.Reflection;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Imports Forsk White from a patched Shaded export, then assigns it to
/// model views; a plan or an elevation takes Forsk Technical instead
/// (ForskTechnicalHost). An import from an earlier revision is replaced on
/// load. A later UpdateDisplayMode is not called. Layout pages and their
/// details are skipped.
/// </summary>
internal static class ForskWhiteHost
{
    const string SettingKey = "ForskWhite";
    const string RevisionKey = "ForskWhiteRevision";
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
    static int _sessionRevision;

    internal static void Start()
    {
        if (_hooked) return;
        _hooked = true;
        ForskPlanCutHost.Start();
        ForskTechnicalHost.Start();
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
        ForskTechnicalHost.Stop();
        ForskPlanCutHost.Stop();
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
            var white = on ? Ensure() : Shaded();
            var technical = on && ForskTechnicalHost.Enabled() ? ForskTechnicalHost.Ensure(doc) : null;
            var changed = false;
            foreach (var view in doc.Views)
            {
                if (view == null || view is RhinoPageView) continue;
                var viewport = view.MainViewport;
                if (viewport == null) continue;
                var target = technical != null && ForskTechnicalHost.LookOf(viewport) != ForskTechnical.Look.Model
                    ? technical
                    : white;
                if (target == null) continue;
                var mode = viewport.DisplayMode;
                var current = mode?.EnglishName;
                var typeName = view.GetType().Name;
                var same = mode != null && mode.Id == target.Id;
                if (!ForskWhite.NeedsAssign(on, current, target.EnglishName, typeName)
                    && !ForskWhite.NeedsReassign(on, current, target.EnglishName, typeName, same))
                    continue;
                viewport.DisplayMode = target;
                changed = true;
            }
            if (changed) doc.Views.Redraw();
            ForskPlanCutHost.Apply(doc, on);
            // A file from before the roof lock gets it when it opens: the roof is never a click.
            RhinoMCPFunctions.RoofLayerLocked(doc, true);
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

    /// <summary>
    /// The mode a view picked in the view picker takes: Forsk Technical for a
    /// plan or an elevation, Forsk White for the rest; Shaded with Forsk White off.
    /// </summary>
    internal static DisplayModeDescription ModeFor(RhinoDoc doc, ForskTechnical.Look look)
    {
        if (!Enabled()) return Shaded();
        if (look != ForskTechnical.Look.Model && ForskTechnicalHost.Enabled())
            return ForskTechnicalHost.Ensure(doc) ?? Ensure();
        return Ensure();
    }

    static DisplayModeDescription Shaded()
    {
        return DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
    }

    /// <summary>The imported Forsk White, without importing it. Null when it is not loaded.</summary>
    internal static DisplayModeDescription Loaded()
    {
        return Find(ForskWhite.ModeName);
    }

    /// <summary>Find the imported mode. Replace it when its revision is behind.</summary>
    static DisplayModeDescription Ensure()
    {
        var existing = Find(ForskWhite.ModeName);
        if (existing != null && !ForskWhite.NeedsReimport(StoredRevision()))
            return existing;
        if (Import(ForskWhite.ModeName, ForskWhite.Patch))
            StoreRevision();
        return Find(ForskWhite.ModeName);
    }

    static int StoredRevision()
    {
        if (_sessionRevision >= ForskWhite.ModeRevision) return _sessionRevision;
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return 0;
        try { return settings.GetInteger(RevisionKey, 0); }
        catch (Exception) { return 0; }
    }

    static void StoreRevision()
    {
        _sessionRevision = ForskWhite.ModeRevision;
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        try { settings.SetInteger(RevisionKey, ForskWhite.ModeRevision); }
        catch (Exception) { }
    }

    /// <summary>
    /// Replace the mode named <paramref name="name"/> with a patched Shaded
    /// export: the live one, else the embedded copy. True when it loaded.
    /// The earlier import is dropped first, so its name is free for the copy.
    /// </summary>
    internal static bool Import(string name, Func<string, string> patch)
    {
        return Import(name, patch, DisplayModeDescription.ShadedId, true);
    }

    /// <summary>
    /// Same import, copied from <paramref name="parent"/> (Wireframe for
    /// Forsk Technical, Rendered for Forsk Interior). No shaded embed: a failed copy must not install Shaded.
    /// </summary>
    internal static bool Import(string name, Func<string, string> patch, Guid parent)
    {
        return Import(name, patch, parent, false);
    }

    static bool Import(string name, Func<string, string> patch, Guid parent, bool embedded)
    {
        DropCopies(name);
        if (TryImport(name, patch, () => LiveExport(name, parent))) return true;
        return embedded && TryImport(name, patch, EmbeddedExport);
    }

    static bool TryImport(string name, Func<string, string> patch, Func<string> source)
    {
        try
        {
            var exported = source();
            if (string.IsNullOrEmpty(exported)) return false;
            var path = Path.Combine(Path.GetTempPath(), FileStem(name) + ".ini");
            File.WriteAllText(path, patch(exported), new UTF8Encoding(false));
            DropCopies(name);
            DisplayModeDescription.ImportFromFile(path);
            return Find(name) != null;
        }
        catch (Exception ex)
        {
            Report(ex, name);
            return false;
        }
    }

    static string LiveExport(string name, Guid parent)
    {
        var copied = DisplayModeDescription.CopyDisplayMode(parent, name);
        var mode = copied == Guid.Empty ? null : DisplayModeDescription.GetDisplayMode(copied);
        if (mode == null || !Named(mode, name))
        {
            DropCopy(copied, name);
            return null;
        }
        var path = Path.Combine(Path.GetTempPath(), FileStem(name) + "-export.ini");
        var wrote = false;
        try
        {
            wrote = DisplayModeDescription.ExportToFile(mode, path) && File.Exists(path);
            return wrote ? File.ReadAllText(path) : null;
        }
        finally
        {
            // The copy is the unpatched source. The import replaces it.
            // A taken name makes Rhino rename the copy; still drop that id.
            DropCopy(copied, name);
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

    internal static DisplayModeDescription Find(string name)
    {
        var named = DisplayModeDescription.FindByName(name);
        if (named == null || !Named(named, name)) return null;
        if (Kept(named.Id)) return null;
        return DisplayModeDescription.GetDisplayMode(named.Id) ?? named;
    }

    static void DropCopies(string name)
    {
        var named = DisplayModeDescription.FindByName(name);
        if (named != null) DropId(named.Id, name);
    }

    static void DropId(Guid id, string name)
    {
        if (Kept(id)) return;
        var mode = DisplayModeDescription.GetDisplayMode(id);
        if (!Named(mode, name)) return;
        try { DisplayModeDescription.DeleteDisplayMode(id); }
        catch (Exception) { }
    }

    static void DropCopy(Guid id, string name)
    {
        if (Kept(id)) return;
        var copyName = DisplayModeDescription.GetDisplayMode(id)?.EnglishName ?? "";
        if (!copyName.StartsWith(name, StringComparison.Ordinal)) return;
        try { DisplayModeDescription.DeleteDisplayMode(id); }
        catch (Exception) { }
    }

    /// <summary>Rhino's own modes. A copy must never delete or replace these.</summary>
    static bool Kept(Guid id)
    {
        return id == Guid.Empty
            || id == DisplayModeDescription.ShadedId
            || id == DisplayModeDescription.WireframeId
            || id == DisplayModeDescription.RenderedId;
    }

    static bool Named(DisplayModeDescription mode, string name)
    {
        var englishName = mode?.EnglishName;
        return englishName != null && englishName.Equals(name, StringComparison.Ordinal);
    }

    /// <summary>"forsk-white", "forsk-technical".</summary>
    static string FileStem(string name)
    {
        return name.ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>One line in Rhino's command history, naming the mode that failed (Forsk White, Technical or Interior).</summary>
    static void Report(Exception ex, string mode = ForskWhite.ModeName)
    {
        RhinoApp.WriteLine(mode + " did not load: " + (ex?.Message ?? "unknown error"));
    }
}
