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
        static string _lookup = "not looked up";

        public static void Style(TextBox box)
        {
            try
            {
                var view = box == null ? null : box.ControlObject;
                if (view == null) return;
                Set(view, "Bezeled", false);
                Set(view, "Bordered", false);
                SetEnum(view, "FocusRingType", "None");
                // A small control size shrinks the face after Font is set. Regular first.
                SetEnum(view, "ControlSize", "Regular");
                var font = InputFont(view.GetType().Assembly);
                var cell = Get(view, "Cell");
                if (cell != null)
                {
                    SetEnum(cell, "ControlSize", "Regular");
                    SetEnum(cell, "FocusRingType", "None");
                }
                if (font != null)
                {
                    Set(view, "Font", font);
                    if (cell != null) Set(cell, "Font", font);
                }
                var editor = Get(view, "CurrentEditor");
                if (editor != null)
                {
                    SetEnum(editor, "FocusRingType", "None");
                    SetEnum(editor, "ControlSize", "Regular");
                    if (font != null) Set(editor, "Font", font);
                }
                Log(font == null
                    ? "styled without a native font (" + _lookup + ")"
                    : "styled " + Describe(font) + " via " + _lookup);
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
            var nsFont = FindNsFont(mono);
            if (nsFont == null) return null;
            _font = CallFont(nsFont, "FromFontName", "Geist-Regular", 16)
                ?? CallFont(nsFont, "SystemFontOfSize", null, 16);
            return _font;
        }

        static Type FindNsFont(Assembly preferred)
        {
            var direct = TypeIn(preferred, "AppKit.NSFont") ?? TypeIn(preferred, "MonoMac.AppKit.NSFont");
            if (direct != null)
            {
                _lookup = direct.FullName + " in " + preferred.GetName().Name;
                return direct;
            }
            Assembly[] loaded;
            try
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception e)
            {
                _lookup = "assembly list failed: " + e.Message;
                return null;
            }
            foreach (var asm in loaded)
            {
                var found = TypeIn(asm, "AppKit.NSFont") ?? TypeIn(asm, "MonoMac.AppKit.NSFont");
                if (found == null) continue;
                _lookup = found.FullName + " in " + asm.GetName().Name;
                return found;
            }
            _lookup = "NSFont missing in " + loaded.Length + " assemblies";
            return null;
        }

        static Type TypeIn(Assembly asm, string name)
        {
            if (asm == null) return null;
            try
            {
                return asm.GetType(name, false);
            }
            catch
            {
                return null;
            }
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
                    {
                        var point = Size(args[0].ParameterType, size);
                        if (point == null) continue;
                        return method.Invoke(null, new[] { point });
                    }
                    if (family != null && args.Length == 2 && args[0].ParameterType == typeof(string))
                    {
                        var point = Size(args[1].ParameterType, size);
                        if (point == null) continue;
                        return method.Invoke(null, new[] { (object)family, point });
                    }
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
            // Xamarin.Mac nfloat is a struct with a float or double constructor.
            var ctor = type.GetConstructor(new[] { typeof(float) })
                ?? type.GetConstructor(new[] { typeof(double) });
            if (ctor != null)
            {
                var arg = ctor.GetParameters()[0].ParameterType == typeof(float)
                    ? (object)(float)size
                    : size;
                return ctor.Invoke(new[] { arg });
            }
            try
            {
                return Convert.ChangeType(size, type);
            }
            catch
            {
                return null;
            }
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
