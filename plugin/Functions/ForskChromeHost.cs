using System;
using System.Collections.Generic;
using System.Reflection;
using Rhino;
using Rhino.ApplicationSettings;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Hides the tool palettes, the right sidebar, and the top toolbar while the
/// demo flag is on, and puts that snapshot back on a clean exit. The left
/// sidebar stays open so the command field stays. State is read from the live
/// Mac window. A piece that does not answer is skipped, never toggled.
/// A crash leaves the flag and the snapshot; the next launch restores first.
/// </summary>
internal static class ForskChromeHost
{
    const string FlagKey = "ForskDemo";
    const string OwedKey = "ForskChromeOwed";
    const string SnapshotKey = "ForskChromeSnapshot";
    const int FrameWaits = 50;

    static readonly EventHandler Idle = OnIdle;
    static int _waits;
    static bool _settled;
    static bool _waiting;

    internal static void Start()
    {
        if (!Enabled() && !Owed()) return;
        WaitForFrame();
    }

    internal static void Stop()
    {
        if (_waiting) RhinoApp.Idle -= Idle;
        _waiting = false;
        if (!Owed()) return;
        if (Restore()) ClearOwed();
    }

    internal static bool Enabled()
    {
        return SettingsBool(FlagKey, false);
    }

    internal static void SetEnabled(bool enabled)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        settings.SetBool(FlagKey, enabled);
        if (!enabled)
        {
            if (Owed() && Restore()) ClearOwed();
            return;
        }
        if (!FrameReady())
        {
            WaitForFrame();
            return;
        }
        // An owed snapshot is the pre-hide state. Do not replace it with the hidden one.
        if (Owed())
        {
            _settled = true;
            Apply(ForskChrome.HidePlan(Read()));
            return;
        }
        HideFromCleanState();
    }

    static void OnIdle(object sender, EventArgs args)
    {
        if (!FrameReady())
        {
            if (++_waits < FrameWaits) return;
            RhinoApp.Idle -= Idle;
            _waiting = false;
            RhinoApp.WriteLine("Forsk demo chrome was not changed: the document frame was not ready.");
            return;
        }
        RhinoApp.Idle -= Idle;
        _waiting = false;
        Settle();
    }

    static void WaitForFrame()
    {
        if (_waiting) return;
        _waits = 0;
        _waiting = true;
        RhinoApp.Idle += Idle;
    }

    /// <summary>Startup. An owed snapshot is restored before a new hide, and kept.</summary>
    static void Settle()
    {
        if (_settled) return;
        _settled = true;
        if (Owed()) Restore();
        if (!Enabled())
        {
            if (Owed()) ClearOwed();
            return;
        }
        if (!Owed()) Save(Read());
        Apply(ForskChrome.HidePlan(Read()));
        SetOwed(true);
    }

    static void HideFromCleanState()
    {
        _settled = true;
        Save(Read());
        Apply(ForskChrome.HidePlan(Read()));
        SetOwed(true);
    }

    static bool Restore()
    {
        var snapshot = ForskChrome.Parse(Snapshot());
        var steps = ForskChrome.RestorePlan(snapshot, Read());
        return Apply(steps);
    }

    static bool Apply(IReadOnlyList<ForskChrome.Piece> steps)
    {
        var ok = true;
        foreach (var step in steps)
        {
            if (!TrySet(step.Id, step.Open)) ok = false;
        }
        return ok;
    }

    static List<ForskChrome.Piece> Read()
    {
        var list = new List<ForskChrome.Piece>();
        bool? palettes = MacChrome.PalettesOpen();
        bool? right = MacChrome.RightOpen();
        bool? toolbar = MacChrome.ToolbarOpen();
        bool? status = StatusOpen();
        if (palettes.HasValue) list.Add(new ForskChrome.Piece(ForskChrome.Palettes, palettes.Value));
        if (right.HasValue) list.Add(new ForskChrome.Piece(ForskChrome.RightSidebar, right.Value));
        if (toolbar.HasValue) list.Add(new ForskChrome.Piece(ForskChrome.Toolbar, toolbar.Value));
        if (status.HasValue) list.Add(new ForskChrome.Piece(ForskChrome.StatusBar, status.Value));
        return list;
    }

    static bool? StatusOpen()
    {
        try { return AppearanceSettings.ShowStatusBar; }
        catch (Exception) { return null; }
    }

    static bool TrySet(string id, bool open)
    {
        try
        {
            if (id == ForskChrome.Palettes) return MacChrome.SetPalettesOpen(open);
            if (id == ForskChrome.RightSidebar) return MacChrome.SetRightOpen(open);
            if (id == ForskChrome.Toolbar) return MacChrome.SetToolbarOpen(open);
            if (id == ForskChrome.StatusBar)
            {
                AppearanceSettings.ShowStatusBar = open;
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool FrameReady()
    {
        return MacChrome.FrameReady();
    }

    static bool Owed()
    {
        return SettingsBool(OwedKey, false);
    }

    static void SetOwed(bool owed)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        settings.SetBool(OwedKey, owed);
    }

    static void ClearOwed()
    {
        SetOwed(false);
    }

    static string Snapshot()
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return "";
        try { return settings.GetString(SnapshotKey, "") ?? ""; }
        catch (Exception) { return ""; }
    }

    static void Save(IEnumerable<ForskChrome.Piece> pieces)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        settings.SetString(SnapshotKey, ForskChrome.Format(pieces));
    }

    static bool SettingsBool(string key, bool fallback)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return fallback;
        try { return settings.GetBool(key, fallback); }
        catch (Exception) { return fallback; }
    }
}

/// <summary>
/// Reads and sets the live Mac window through Xamarin.Mac's msgSend wrappers,
/// which are loaded inside Rhino. The plist is not written from here.
/// The left sidebar is never set: that would hide the command field.
/// </summary>
internal static class MacChrome
{
    internal static bool FrameReady()
    {
        var controller = WindowController();
        return controller != IntPtr.Zero && Responds(controller, "rightSidebarIsOpen");
    }

    internal static bool? PalettesOpen()
    {
        var manager = PaletteManager();
        if (manager == IntPtr.Zero || !Responds(manager, "activeToolPalettesAreVisible")) return null;
        return SendBool(manager, "activeToolPalettesAreVisible");
    }

    internal static bool SetPalettesOpen(bool open)
    {
        var manager = PaletteManager();
        if (manager == IntPtr.Zero) return false;
        if (!open)
        {
            if (!Responds(manager, "hideActiveToolPalettes")) return false;
            Send(manager, "hideActiveToolPalettes");
            return true;
        }
        if (!Responds(manager, "showActiveToolPalettes:")) return false;
        SendInt(manager, "showActiveToolPalettes:", IntPtr.Zero);
        return true;
    }

    internal static bool? RightOpen()
    {
        var controller = WindowController();
        if (controller == IntPtr.Zero || !Responds(controller, "rightSidebarIsOpen")) return null;
        return SendBool(controller, "rightSidebarIsOpen");
    }

    internal static bool SetRightOpen(bool open)
    {
        var controller = WindowController();
        if (controller == IntPtr.Zero || !Responds(controller, "setRightSidebarIsOpen:")) return false;
        SendBoolArg(controller, "setRightSidebarIsOpen:", open);
        return true;
    }

    internal static bool? ToolbarOpen()
    {
        var toolbar = Toolbar();
        if (toolbar == IntPtr.Zero || !Responds(toolbar, "isVisible")) return null;
        return SendBool(toolbar, "isVisible");
    }

    internal static bool SetToolbarOpen(bool open)
    {
        var toolbar = Toolbar();
        if (toolbar == IntPtr.Zero) return false;
        if (Responds(toolbar, "setMRDocumentToolbarVisible:"))
        {
            SendBoolArg(toolbar, "setMRDocumentToolbarVisible:", open);
            return true;
        }
        if (!Responds(toolbar, "setVisible:")) return false;
        SendBoolArg(toolbar, "setVisible:", open);
        return true;
    }

    static IntPtr PaletteManager()
    {
        var cls = ClassHandle("MRToolPaletteManager");
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        return Send(cls, "sharedManager");
    }

    static IntPtr WindowController()
    {
        var window = MainWindow();
        if (window == IntPtr.Zero || !Responds(window, "windowController")) return IntPtr.Zero;
        return Send(window, "windowController");
    }

    static IntPtr Toolbar()
    {
        var window = MainWindow();
        if (window == IntPtr.Zero || !Responds(window, "toolbar")) return IntPtr.Zero;
        return Send(window, "toolbar");
    }

    static IntPtr MainWindow()
    {
        var cls = ClassHandle("NSApplication");
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        var app = Send(cls, "sharedApplication");
        if (app == IntPtr.Zero || !Responds(app, "mainWindow")) return IntPtr.Zero;
        return Send(app, "mainWindow");
    }

    static IntPtr ClassHandle(string name)
    {
        try
        {
            var type = Type.GetType("ObjCRuntime.Class, Xamarin.Mac");
            var method = type?.GetMethod("GetHandle", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (method == null) return IntPtr.Zero;
            return (IntPtr)method.Invoke(null, new object[] { name });
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    static IntPtr Selector(string name)
    {
        var type = Type.GetType("ObjCRuntime.Selector, Xamarin.Mac");
        var method = type?.GetMethod("GetHandle", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (method == null) return IntPtr.Zero;
        return (IntPtr)method.Invoke(null, new object[] { name });
    }

    static bool Responds(IntPtr receiver, string selector)
    {
        if (receiver == IntPtr.Zero) return false;
        try
        {
            var sel = Selector(selector);
            if (sel == IntPtr.Zero) return false;
            var method = Msg("bool_objc_msgSend_IntPtr", typeof(IntPtr), typeof(IntPtr), typeof(IntPtr));
            if (method == null) return false;
            return (bool)method.Invoke(null, new object[] { receiver, Selector("respondsToSelector:"), sel });
        }
        catch (Exception)
        {
            return false;
        }
    }

    static IntPtr Send(IntPtr receiver, string selector)
    {
        if (receiver == IntPtr.Zero) return IntPtr.Zero;
        var method = Msg("IntPtr_objc_msgSend", typeof(IntPtr), typeof(IntPtr));
        if (method == null) return IntPtr.Zero;
        return (IntPtr)method.Invoke(null, new object[] { receiver, Selector(selector) });
    }

    static bool SendBool(IntPtr receiver, string selector)
    {
        var method = Msg("bool_objc_msgSend", typeof(IntPtr), typeof(IntPtr));
        if (method == null) return false;
        return (bool)method.Invoke(null, new object[] { receiver, Selector(selector) });
    }

    static void SendBoolArg(IntPtr receiver, string selector, bool value)
    {
        var method = Msg("void_objc_msgSend_bool", typeof(IntPtr), typeof(IntPtr), typeof(bool));
        if (method == null) return;
        method.Invoke(null, new object[] { receiver, Selector(selector), value });
    }

    static void SendInt(IntPtr receiver, string selector, IntPtr arg)
    {
        var method = Msg("void_objc_msgSend_IntPtr", typeof(IntPtr), typeof(IntPtr), typeof(IntPtr));
        if (method == null) return;
        method.Invoke(null, new object[] { receiver, Selector(selector), arg });
    }

    static MethodInfo Msg(string name, params Type[] args)
    {
        var type = Type.GetType("ObjCRuntime.Messaging, Xamarin.Mac");
        return type?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
    }
}
