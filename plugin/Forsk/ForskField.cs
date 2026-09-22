using System;
using System.IO;
using System.Reflection;
using System.Text;
using Eto.Forms;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Rhino's Mac panel is Eto on AppKit. A TextBox is an NSTextField.
    /// Eto can hide the bezel, but not the focus ring, and a face loaded from
    /// a file is not an NSFont, so the field falls back to a tiny size.
    /// These setters go through MonoMac's own properties, after the panel exists.
    /// The field stays opaque. A clear background paints the whole dock black.
    /// </summary>
    static class ForskField
    {
        static bool _registered;
        static object _font;
        static string _logged;

        public static void Style(TextBox box)
        {
            try
            {
                var view = box == null ? null : box.ControlObject;
                if (view == null) return;
                Set(view, "Bezeled", false);
                Set(view, "Bordered", false);
                SetEnum(view, "FocusRingType", "None");
                var font = InputFont(view.GetType().Assembly);
                if (font != null)
                {
                    Set(view, "Font", font);
                    var cell = Get(view, "Cell");
                    if (cell != null)
                    {
                        Set(cell, "Font", font);
                        SetEnum(cell, "FocusRingType", "None");
                    }
                }
                var editor = Get(view, "CurrentEditor");
                if (editor != null)
                {
                    SetEnum(editor, "FocusRingType", "None");
                    if (font != null) Set(editor, "Font", font);
                }
                Log(font == null ? "styled without a native font" : "styled " + Describe(font));
            }
            catch (Exception e)
            {
                Log(e.GetType().Name + ": " + e.Message);
            }
        }

        static object InputFont(Assembly mono)
        {
            if (_font != null) return _font;
            RegisterGeist();
            var nsFont = mono.GetType("MonoMac.AppKit.NSFont") ?? mono.GetType("AppKit.NSFont");
            if (nsFont == null) return null;
            _font = CallFont(nsFont, "FromFontName", "Geist-Regular", 16)
                ?? CallFont(nsFont, "SystemFontOfSize", null, 16);
            return _font;
        }

        static object CallFont(Type nsFont, string name, string family, double size)
        {
            var flags = BindingFlags.Public | BindingFlags.Static;
            foreach (var method in nsFont.GetMethods(flags))
            {
                if (method.Name != name) continue;
                var args = method.GetParameters();
                try
                {
                    if (family == null && args.Length == 1)
                        return method.Invoke(null, new[] { Size(args[0].ParameterType, size) });
                    if (family != null && args.Length == 2 && args[0].ParameterType == typeof(string))
                        return method.Invoke(null, new[] { (object)family, Size(args[1].ParameterType, size) });
                }
                catch
                {
                    // Try the next overload.
                }
            }
            return null;
        }

        static object Size(Type type, double size)
        {
            if (type == typeof(float)) return (float)size;
            if (type == typeof(double)) return size;
            if (type == typeof(int)) return (int)size;
            return Convert.ChangeType(size, type);
        }

        static void RegisterGeist()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                var src = typeof(ForskField).Assembly.GetManifestResourceStream("RhinoMCPPlugin.Fonts.Geist-Regular.otf");
                if (src == null) return;
                byte[] bytes;
                using (src)
                using (var copy = new MemoryStream())
                {
                    src.CopyTo(copy);
                    bytes = copy.ToArray();
                }
                var dir = Path.Combine(Path.GetTempPath(), "forsk-geist");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "Geist-Regular.otf");
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
            catch (Exception e)
            {
                Log("font register " + e.Message);
            }
        }

        static void Set(object target, string name, object value)
        {
            var prop = target.GetType().GetProperty(name);
            if (prop == null || !prop.CanWrite || value == null) return;
            if (!prop.PropertyType.IsInstanceOfType(value) && !prop.PropertyType.IsAssignableFrom(value.GetType()))
                return;
            prop.SetValue(target, value, null);
        }

        static void SetEnum(object target, string name, string value)
        {
            var prop = target.GetType().GetProperty(name);
            if (prop == null || !prop.CanWrite || !prop.PropertyType.IsEnum) return;
            prop.SetValue(target, Enum.Parse(prop.PropertyType, value), null);
        }

        static object Get(object target, string name)
        {
            var prop = target.GetType().GetProperty(name);
            if (prop == null) return null;
            return prop.GetValue(target, null);
        }

        static string Describe(object font)
        {
            var name = Get(font, "FontName") ?? Get(font, "FamilyName");
            var size = Get(font, "PointSize");
            return (name == null ? "font" : name.ToString()) + " " + (size == null ? "" : size.ToString());
        }

        static void Log(string line)
        {
            if (line == _logged) return;
            _logged = line;
            try
            {
                File.AppendAllText("/tmp/forsk-field.log", line + "\n");
            }
            catch
            {
                // The field still works if the log cannot be written.
            }
        }

        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, long length, byte isDirectory);

        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        static extern void CFRelease(IntPtr value);

        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreText.framework/CoreText")]
        static extern byte CTFontManagerRegisterFontsForURL(IntPtr fontUrl, int scope, IntPtr error);
    }
}
