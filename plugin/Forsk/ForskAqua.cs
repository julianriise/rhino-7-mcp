using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

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

        /// <summary>
        /// The views under this native web view accept the click that makes the
        /// window key. macOS otherwise spends that click on activation, so a
        /// button needs a second one. The hit view is often a private subview,
        /// so each view in the tree gets acceptsFirstMouse:. Returns how many
        /// views were armed. No AppKit reference: the ObjC runtime only.
        /// </summary>
        public static int ArmClick(object native)
        {
            if (native == null || !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 0;
            try
            {
                var handle = HandleOf(native);
                if (handle == IntPtr.Zero) return 0;
                var armed = 0;
                ArmTree(handle, 0, ref armed);
                if (armed > 0 && !_loggedClick)
                {
                    _loggedClick = true;
                    ForskWindow.Log("first click · " + armed + " view(s) accept the activating click");
                }
                return armed;
            }
            catch (Exception e)
            {
                ForskWindow.Log("first click " + e.GetBaseException().Message);
                return 0;
            }
        }

        static bool _loggedClick;
        static readonly Dictionary<IntPtr, IntPtr> _clickClass = new Dictionary<IntPtr, IntPtr>();
        static readonly AcceptsFirst _acceptsFirst = (self, sel, evt) => 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte AcceptsFirst(IntPtr self, IntPtr sel, IntPtr evt);

        static void ArmTree(IntPtr view, int depth, ref int armed)
        {
            if (view == IntPtr.Zero || depth > 8) return;
            var cls = object_getClass(view);
            if (cls == IntPtr.Zero) return;
            if (!Ours(cls))
            {
                var sub = ClickClass(cls);
                if (sub != IntPtr.Zero && sub != cls)
                {
                    object_setClass(view, sub);
                    armed++;
                }
            }
            var kids = Msg(view, Sel("subviews"));
            if (kids == IntPtr.Zero) return;
            var count = (long)Msg(kids, Sel("count"));
            if (count < 1 || count > 32) return;
            for (long i = 0; i < count; i++)
                ArmTree(Msg1(kids, Sel("objectAtIndex:"), (IntPtr)i), depth + 1, ref armed);
        }

        static IntPtr ClickClass(IntPtr super)
        {
            if (_clickClass.TryGetValue(super, out var known)) return known;
            var superName = Marshal.PtrToStringAnsi(class_getName(super)) ?? "View";
            var name = "ForskClick_" + superName.Replace('.', '_').Replace(' ', '_');
            var existing = objc_getClass(name);
            if (existing != IntPtr.Zero)
            {
                _clickClass[super] = existing;
                return existing;
            }
            var created = objc_allocateClassPair(super, name, IntPtr.Zero);
            if (created == IntPtr.Zero) return IntPtr.Zero;
            class_addMethod(created, Sel("acceptsFirstMouse:"), Marshal.GetFunctionPointerForDelegate(_acceptsFirst), "B@:@");
            objc_registerClassPair(created);
            _clickClass[super] = created;
            return created;
        }

        static bool Ours(IntPtr cls)
        {
            var name = Marshal.PtrToStringAnsi(class_getName(cls)) ?? "";
            return name.StartsWith("ForskClick_", StringComparison.Ordinal);
        }

        static IntPtr HandleOf(object native)
        {
            var prop = native.GetType().GetProperty("Handle");
            var value = prop == null ? null : prop.GetValue(native, null);
            return value is IntPtr handle ? handle : IntPtr.Zero;
        }

        static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr object_getClass(IntPtr obj);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr object_setClass(IntPtr obj, IntPtr cls);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr class_getName(IntPtr cls);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern void objc_registerClassPair(IntPtr cls);

        [DllImport("/usr/lib/libobjc.dylib")]
        static extern byte class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr Msg(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr Msg1(IntPtr receiver, IntPtr selector, IntPtr arg);

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
