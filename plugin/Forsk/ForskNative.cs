using System;
using System.IO;
using System.Reflection;
using System.Text;
using Eto.Forms;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Mac AppKit tweaks Eto does not expose: the text field focus ring, and the thread's scroll bars.
    /// </summary>
    static class ForskNative
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";
        const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        static IntPtr _inputFont;

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "sel_registerName")]
        static extern IntPtr Sel(string name);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_getClass")]
        static extern IntPtr Class(string name);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr recv, IntPtr sel);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendPtr(IntPtr recv, IntPtr sel, IntPtr arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendPtrVoid(IntPtr recv, IntPtr sel, IntPtr arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendBool(IntPtr recv, IntPtr sel, byte arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendLong(IntPtr recv, IntPtr sel, long arg);

        [System.Runtime.InteropServices.DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendPtrDouble(IntPtr recv, IntPtr sel, IntPtr arg, double value);

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
                var buffer = Encoding.UTF8.GetBytes(path);
                var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, buffer, buffer.Length, 0);
                if (url == IntPtr.Zero) return;
                try
                {
                    // kCTFontManagerScopeProcess
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
            var view = Handle(control);
            if (view == IntPtr.Zero) return;
            try
            {
                SendBool(view, Sel("setBezeled:"), 0);
                SendBool(view, Sel("setBordered:"), 0);
                SendBool(view, Sel("setDrawsBackground:"), 0);
                SendLong(view, Sel("setFocusRingType:"), 1); // NSFocusRingTypeNone
                var cell = Send(view, Sel("cell"));
                if (cell != IntPtr.Zero)
                {
                    SendBool(cell, Sel("setBezeled:"), 0);
                    SendBool(cell, Sel("setBordered:"), 0);
                    SendBool(cell, Sel("setDrawsBackground:"), 0);
                    SendLong(cell, Sel("setFocusRingType:"), 1);
                }
                var font = InputFont();
                if (font != IntPtr.Zero)
                {
                    SendPtrVoid(view, Sel("setFont:"), font);
                    if (cell != IntPtr.Zero) SendPtrVoid(cell, Sel("setFont:"), font);
                }
                var editor = Send(view, Sel("currentEditor"));
                if (editor != IntPtr.Zero)
                {
                    SendLong(editor, Sel("setFocusRingType:"), 1);
                    SendBool(editor, Sel("setDrawsBackground:"), 0);
                    if (font != IntPtr.Zero) SendPtrVoid(editor, Sel("setFont:"), font);
                }
                SendBool(view, Sel("setNeedsDisplay:"), 1);
            }
            catch
            {
                // Leave the Eto field if AppKit rejects a selector.
            }
        }

        public static void QuietScroll(Control control)
        {
            var view = Handle(control);
            if (view == IntPtr.Zero) return;
            try
            {
                SendBool(view, Sel("setHasHorizontalScroller:"), 0);
                SendBool(view, Sel("setHasVerticalScroller:"), 1);
                SendBool(view, Sel("setAutohidesScrollers:"), 1);
                SendLong(view, Sel("setScrollerStyle:"), 1); // NSScrollerStyleOverlay
                var horizontal = Send(view, Sel("horizontalScroller"));
                if (horizontal != IntPtr.Zero) SendBool(horizontal, Sel("setHidden:"), 1);
                var vertical = Send(view, Sel("verticalScroller"));
                if (vertical != IntPtr.Zero)
                {
                    SendLong(vertical, Sel("setScrollerStyle:"), 1);
                    SendLong(vertical, Sel("setControlSize:"), 1);
                }
            }
            catch
            {
                // The width clamp in Relayout still prevents the horizontal bar.
            }
        }

        static IntPtr InputFont()
        {
            if (_inputFont != IntPtr.Zero) return _inputFont;
            var font = NamedFont("Geist-Regular", 16);
            if (font == IntPtr.Zero) font = NamedFont("Geist", 16);
            if (font == IntPtr.Zero) return IntPtr.Zero;
            _inputFont = Send(font, Sel("retain"));
            return _inputFont;
        }

        static IntPtr NamedFont(string name, double size)
        {
            var utf = Utf8(name);
            try
            {
                var ns = SendPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf);
                if (ns == IntPtr.Zero) return IntPtr.Zero;
                return SendPtrDouble(Class("NSFont"), Sel("fontWithName:size:"), ns, size);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(utf);
            }
        }

        static IntPtr Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        static IntPtr Handle(Control control)
        {
            if (control == null) return IntPtr.Zero;
            var native = control.ControlObject;
            if (native == null) return IntPtr.Zero;
            var prop = native.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
            if (prop == null) return IntPtr.Zero;
            var value = prop.GetValue(native, null);
            return value is IntPtr ? (IntPtr)value : IntPtr.Zero;
        }
    }
}
