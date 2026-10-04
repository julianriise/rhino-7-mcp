using System;
using System.Collections.Generic;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Input.Custom;
using Rhino.UI;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The viewport half of hover: the pointer rests over an element and the
    /// chat lights the receipts that name it. A mouse move only notes where the
    /// pointer is and restarts the debounce; the viewport is asked what is under
    /// it once, when the pointer has rested ForskHoverLink.DebounceMs. Nothing
    /// is asked while a command runs or a button is down (orbit, drag).
    /// </summary>
    sealed class ForskViewHover : MouseCallback
    {
        readonly Func<bool> _enabled;
        readonly Action<IReadOnlyList<string>> _hovered;
        readonly UITimer _rest;
        RhinoView _view;
        System.Drawing.Point _at;
        Guid _last = Guid.Empty;

        /// <summary>enabled: the ForskHoverFocus setting. hovered gets the element ids under the pointer, empty for none.</summary>
        public ForskViewHover(Func<bool> enabled, Action<IReadOnlyList<string>> hovered)
        {
            _enabled = enabled;
            _hovered = hovered;
            _rest = new UITimer { Interval = ForskHoverLink.DebounceMs / 1000.0 };
            _rest.Elapsed += (s, e) =>
            {
                _rest.Stop();
                Ask();
            };
        }

        public void Stop()
        {
            Enabled = false;
            _rest.Stop();
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            base.OnMouseMove(e);
            if (e?.View == null) return;
            if (e.MouseButton != MouseButton.None)
            {
                _rest.Stop();
                return;
            }
            _view = e.View;
            _at = e.ViewportPoint;
            _rest.Stop();
            _rest.Start();
        }

        /// <summary>Forget what was under the pointer, so the same object lights again after the chat cleared it.</summary>
        public void Forget()
        {
            _last = Guid.Empty;
        }

        void Ask()
        {
            try
            {
                if (!_enabled())
                {
                    Forget();
                    _hovered(new string[0]);
                    return;
                }
                if (Rhino.Commands.Command.InCommand()) return;
                var found = Under(RhinoDoc.ActiveDoc, _view, _at);
                var id = found?.Id ?? Guid.Empty;
                if (id == _last) return;
                _last = id;
                _hovered(found == null ? new string[0] : ElementIds(RhinoDoc.ActiveDoc, found));
            }
            catch (Exception e)
            {
                ForskWindow.Log("view hover " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The nearest object a click at this point would pick, or null.</summary>
        static RhinoObject Under(RhinoDoc doc, RhinoView view, System.Drawing.Point at)
        {
            if (doc == null || view == null || view is RhinoPageView) return null;
            var viewport = view.ActiveViewport;
            if (!viewport.GetFrustumLine(at.X, at.Y, out var line)) return null;
            using (var context = new PickContext { View = view, PickStyle = PickStyle.PointPick, PickMode = PickMode.Shaded, PickLine = line })
            {
                context.SetPickTransform(viewport.GetPickTransform(at));
                RhinoObject nearest = null;
                var best = double.MaxValue;
                foreach (var hit in doc.Objects.PickObjects(context) ?? new ObjRef[0])
                {
                    var obj = hit?.Object();
                    if (obj == null || obj.IsHidden) continue;
                    var point = hit.SelectionPoint();
                    var depth = point.IsValid ? line.From.DistanceTo(point) : double.MaxValue / 2;
                    if (depth >= best) continue;
                    best = depth;
                    nearest = obj;
                }
                return nearest;
            }
        }

        /// <summary>The ids the chat can name this object by: its own, and its marker's (a room plate, an opening's block).</summary>
        static IReadOnlyList<string> ElementIds(RhinoDoc doc, RhinoObject obj)
        {
            var ids = new List<string>();
            Add(ids, obj);
            foreach (var key in new[] { "forsk:marker", "forsk:marker_id" })
            {
                if (!Guid.TryParse(obj.Attributes.GetUserString(key), out var marker)) continue;
                Add(ids, doc.Objects.FindId(marker));
            }
            return ids;
        }

        static void Add(List<string> ids, RhinoObject obj)
        {
            if (obj?.Attributes == null) return;
            foreach (var key in new[] { "forsk:id", "forsk:mark" })
            {
                var value = obj.Attributes.GetUserString(key);
                if (!string.IsNullOrWhiteSpace(value) && !ids.Contains(value)) ids.Add(value);
            }
        }
    }
}
