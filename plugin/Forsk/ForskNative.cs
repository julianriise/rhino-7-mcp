using System;
using System.IO;
using System.Reflection;
using System.Text;
using Eto.Forms;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Mac tweaks Eto does not expose. Never clear a view's background here:
    /// a transparent layer on Rhino's dark host paints the whole panel black.
    /// </summary>
    static class ForskNative
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";
        const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "sel_registerName")]
        static extern IntPtr Sel(string name);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "object_getClass")]
        static extern IntPtr ObjectClass(IntPtr obj);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr recv, IntPtr sel);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern byte SendByte(IntPtr recv, IntPtr sel, IntPtr arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendBool(IntPtr recv, IntPtr sel, byte arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendLong(IntPtr recv, IntPtr sel, long arg);

        [System.Runtime.InteropServices.DllImport(CoreFoundation)]
        static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, long length, byte isDirectory);

        [System.Runtime.InteropServices.DllImport(CoreFoundation)]
        static extern void CFRelease(IntPtr value);

        [System.Runtime.InteropServices.DllImport(CoreText)]
        static extern byte CTFontManagerRegisterFontsForURL(IntPtr fontUrl, int scope, IntPtr error);

        public static void RegisterFontFile(string fileName, byte[] bytes)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "forsk-geist");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    File.WriteAllBytes(path, bytes);
                var buffer = Encoding.UTF8.GetBytes(path + "\0");
                var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, buffer, buffer.Length - 1, 0);
                if (url == IntPtr.Zero) return;
                try
                {
                    CTFontManagerRegisterFontsForURL(url, 1, IntPtr.Zero);
                }
                finally
                {
                    CFRelease(url);
                }
            }
            catch
            {
                // Painted type still comes from the embedded faces.
            }
        }

        public static void QuietField(Control control)
        {
            try
            {
                var view = Handle(control);
                if (!Alive(view)) return;
                SetBool(view, "setBezeled:", 0);
                SetBool(view, "setBordered:", 0);
                SetLong(view, "setFocusRingType:", 1);
                var cell = Send(view, Sel("cell"));
                if (Alive(cell))
                {
                    SetBool(cell, "setBezeled:", 0);
                    SetBool(cell, "setBordered:", 0);
                    SetLong(cell, "setFocusRingType:", 1);
                }
                var editor = Send(view, Sel("currentEditor"));
                if (Alive(editor))
                    SetLong(editor, "setFocusRingType:", 1);
            }
            catch
            {
                // The Eto field still works if a selector is refused.
            }
        }

        public static void QuietScroll(Control control)
        {
            try
            {
                var view = Handle(control);
                if (!Alive(view)) return;
                SetBool(view, "setHasHorizontalScroller:", 0);
                SetBool(view, "setAutohidesScrollers:", 1);
                SetLong(view, "setScrollerStyle:", 1);
                var horizontal = Send(view, Sel("horizontalScroller"));
                if (Alive(horizontal))
                    SetBool(horizontal, "setHidden:", 1);
                var vertical = Send(view, Sel("verticalScroller"));
                if (Alive(vertical))
                    SetLong(vertical, "setScrollerStyle:", 1);
            }
            catch
            {
                // Relayout still keeps the thread narrower than the view.
            }
        }

        static void SetBool(IntPtr obj, string selector, byte value)
        {
            var sel = Sel(selector);
            if (!Responds(obj, sel)) return;
            SendBool(obj, sel, value);
        }

        static void SetLong(IntPtr obj, string selector, long value)
        {
            var sel = Sel(selector);
            if (!Responds(obj, sel)) return;
            SendLong(obj, sel, value);
        }

        static bool Responds(IntPtr obj, IntPtr sel)
        {
            if (!Alive(obj) || sel == IntPtr.Zero) return false;
            return SendByte(obj, Sel("respondsToSelector:"), sel) != 0;
        }

        static bool Alive(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return false;
            try
            {
                return ObjectClass(obj) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        static IntPtr Handle(Control control)
        {
            if (control == null) return IntPtr.Zero;
            var native = control.ControlObject;
            if (native == null) return IntPtr.Zero;
            var name = native.GetType().FullName ?? "";
            if (name.IndexOf("AppKit", StringComparison.Ordinal) < 0 && name.IndexOf(".NS", StringComparison.Ordinal) < 0)
                return IntPtr.Zero;
            var prop = native.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
            if (prop == null) return IntPtr.Zero;
            var value = prop.GetValue(native, null);
            return value is IntPtr ? (IntPtr)value : IntPtr.Zero;
        }
    }
}
