using System;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Every contour Forsk asks Rhino for goes through here. Rhino's contour code
/// aborted the whole app twice on 2026-10-09 (MakeRhinoContours, a C++
/// exception no try in C# can catch), and the crash report names no Forsk
/// call. An invalid brep is skipped, and the caller is written to the Forsk
/// log before Rhino starts, at most once a second per caller, so the last log
/// line before a crash names the path that hit it.
/// </summary>
internal static class ContourCall
{
    static string _lastCaller;
    static DateTime _lastLogged;

    public static Curve[] Brep(Rhino.Geometry.Brep brep, Plane plane, string caller)
    {
        if (brep == null || !brep.IsValid) return null;
        Note(caller);
        try { return Rhino.Geometry.Brep.CreateContourCurves(brep, plane); }
        catch (Exception) { return null; }
    }

    public static Curve[] Mesh(Rhino.Geometry.Mesh mesh, Plane plane, double tolerance, string caller)
    {
        if (mesh == null || !mesh.IsValid) return null;
        Note(caller);
        try { return Rhino.Geometry.Mesh.CreateContourCurves(mesh, plane, tolerance); }
        catch (Exception) { return null; }
    }

    static void Note(string caller)
    {
        var now = DateTime.UtcNow;
        if (caller == _lastCaller && (now - _lastLogged).TotalSeconds < 1) return;
        _lastCaller = caller;
        _lastLogged = now;
        Forsk.ForskWindow.Log("contour · " + caller);
    }
}
