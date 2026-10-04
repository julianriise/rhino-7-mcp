using System;
using System.Reflection;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The Forsk window stays light under dark appearance: its native window
    /// and web view are pinned to Aqua. The plugin has no Xamarin.Mac
    /// reference, so AppKit is reached by reflection, in the assembly of the
    /// object's first AppKit or WebKit base type (Eto subclasses them).
    /// </summary>
    static class ForskAqua
    {
        public static bool Pin(object native)
        {
            if (native == null) return false;
            try
            {
                var type = native.GetType();
                while (type != null && type.Namespace != "AppKit" && type.Namespace != "WebKit")
                    type = type.BaseType;
                var aqua = Aqua((type ?? native.GetType()).Assembly);
                var prop = aqua == null ? null : Prop(native, "Appearance");
                if (prop == null || !prop.CanWrite || !prop.PropertyType.IsInstanceOfType(aqua)) return false;
                prop.SetValue(native, aqua, null);
                return true;
            }
            catch (Exception e)
            {
                ForskWindow.Log("aqua " + e.Message);
                return false;
            }
        }

        static object Aqua(Assembly mono)
        {
            Type type;
            try { type = mono.GetType("AppKit.NSAppearance", false); }
            catch (Exception) { return null; }
            var name = type?.GetProperty("NameAqua", BindingFlags.Public | BindingFlags.Static);
            if (name == null) return null;
            var value = name.GetValue(null, null);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "GetAppearance" || method.GetParameters().Length != 1) continue;
                try { return method.Invoke(null, new[] { value }); }
                catch (Exception) { }
            }
            return null;
        }

        static PropertyInfo Prop(object target, string name)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo match = null;
                foreach (var prop in type.GetProperties(flags))
                {
                    if (prop.Name != name || prop.GetIndexParameters().Length != 0) continue;
                    if (match == null || (prop.CanWrite && !match.CanWrite)) match = prop;
                }
                if (match != null) return match;
            }
            return null;
        }
    }
}
