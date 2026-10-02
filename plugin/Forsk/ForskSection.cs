using System;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoMCPPlugin.Functions;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// F5.3b. Top view, then a line locked to X or Y at z=0. The name dialog
    /// comes after the line. section_add stores the cut, so the marker stays
    /// A–A and the sheet stays section_&lt;letter&gt;. Cancel adds nothing.
    /// </summary>
    public static class ForskSection
    {
        public const string CommandName = "ForskSection";
        public const string LayerName = "cross-sections";

        /// <summary>UI thread. ownUndo is false when the MCP dispatcher is already recording.</summary>
        public static JObject RunOnUi(bool ownUndo)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            try
            {
                ActivateTop(doc);
                if (!TryEnds(out var from, out var raw))
                    return ForskTools.Fail("Cross section cancelled.");
                var defs = Sections.Read(doc.Strings.GetValue(Sections.MetaSection, Sections.MetaKey));
                if (!Sections.TrySnapAxis(from, raw, out _))
                    return ForskTools.Fail(Sections.TooShortMessage);
                var drawn = Sections.Decide(true, AskName(Sections.NextSectionName(defs)), defs, from, raw);
                if (drawn == null) return ForskTools.Fail("Cross section cancelled.");
                if (!string.IsNullOrEmpty(drawn.Error)) return ForskTools.Fail(drawn.Error);
                return Store(doc, drawn, ownUndo);
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
        }

        static void ActivateTop(RhinoDoc doc)
        {
            RhinoView top = null;
            foreach (var view in doc.Views)
            {
                var name = view?.ActiveViewport?.Name;
                if (name != null && name.Equals("Top", StringComparison.OrdinalIgnoreCase))
                {
                    top = view;
                    break;
                }
            }
            if (top == null)
            {
                foreach (var view in doc.Views)
                {
                    var vp = view?.ActiveViewport;
                    if (vp == null || !vp.IsParallelProjection) continue;
                    if (vp.CameraDirection.IsParallelTo(Vector3d.ZAxis) == 0) continue;
                    top = view;
                    break;
                }
            }
            if (top != null && !ReferenceEquals(doc.Views.ActiveView, top))
                doc.Views.ActiveView = top;
            doc.Views.Redraw();
        }

        static bool TryEnds(out Pt from, out Pt raw)
        {
            from = default;
            raw = default;
            var first = new GetPoint();
            first.SetCommandPrompt("Start of cross section");
            first.Constrain(Plane.WorldXY, false);
            if (first.Get() != GetResult.Point) return false;
            var a = first.Point();
            from = new Pt(a.X, a.Y);

            var second = new AxisPoint(new Point3d(from.X, from.Y, 0));
            if (second.Get() != GetResult.Point) return false;
            var b = second.Point();
            raw = new Pt(b.X, b.Y);
            return true;
        }

        /// <summary>Null when the user cancels. A blank name comes back as the suggestion.</summary>
        static string AskName(string suggestion)
        {
            var fallback = string.IsNullOrEmpty(suggestion) ? "A1" : suggestion;
            string chosen = null;
            var accepted = false;
            var box = new TextBox { Text = fallback };
            var add = new Button { Text = "Add section" };
            var cancel = new Button { Text = "Cancel" };
            var dialog = new Dialog
            {
                Title = "Cross section",
                Padding = new Eto.Drawing.Padding(12),
                Resizable = false
            };
            add.Click += (s, e) =>
            {
                var text = (box.Text ?? "").Trim();
                chosen = text.Length == 0 ? fallback : text;
                accepted = true;
                dialog.Close();
            };
            cancel.Click += (s, e) => dialog.Close();
            dialog.DefaultButton = add;
            dialog.AbortButton = cancel;
            dialog.Content = new StackLayout
            {
                Spacing = 8,
                Items =
                {
                    new Label { Text = "Section name" },
                    box,
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Items = { add, cancel }
                    }
                }
            };
            dialog.Shown += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            var parent = RhinoEtoApp.MainWindow;
            if (parent != null) dialog.ShowModal(parent);
            else dialog.ShowModal();
            return accepted ? chosen : null;
        }

        static JObject Store(RhinoDoc doc, Sections.Drawn drawn, bool ownUndo)
        {
            uint record = 0;
            // Inside a pill's record, the section is part of it.
            ownUndo = ownUndo && !doc.UndoRecordingIsActive;
            if (ownUndo) record = doc.BeginUndoRecord("Forsk: section_pick");
            ForskCalls.Enter();
            var id = Guid.Empty;
            var created = false;
            var hid = false;
            var layerIndex = -1;
            try
            {
                layerIndex = CrossSectionLayer(doc, out created, out hid);
                if (layerIndex < 0)
                    throw new InvalidOperationException("Could not add the cross-sections layer.");
                var curve = new LineCurve(
                    new Point3d(drawn.A.X, drawn.A.Y, 0),
                    new Point3d(drawn.B.X, drawn.B.Y, 0));
                var attr = new ObjectAttributes { LayerIndex = layerIndex, Name = drawn.Name };
                attr.SetUserString("forsk:section", drawn.Letter);
                attr.SetUserString("forsk:name", drawn.Name);
                id = doc.Objects.AddCurve(curve, attr);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Could not add the cross section line.");
                ForceHidden(doc, layerIndex);
                var added = new RhinoMCPFunctions().SectionAdd(new JObject
                {
                    ["letter"] = drawn.Letter,
                    ["from"] = new JArray(drawn.A.X, drawn.A.Y),
                    ["to"] = new JArray(drawn.B.X, drawn.B.Y),
                    ["name"] = drawn.Name
                });
                added["name"] = drawn.Name;
                added["line_id"] = id.ToString();
                try { doc.Views.Redraw(); }
                catch (Exception) { }
                return new JObject { ["status"] = "success", ["result"] = added };
            }
            catch (Exception e)
            {
                Rollback(doc, id, layerIndex, created, hid);
                return ForskTools.Fail(e.Message);
            }
            finally
            {
                ForskCalls.Exit();
                if (ownUndo) doc.EndUndoRecord(record);
            }
        }

        static int CrossSectionLayer(RhinoDoc doc, out bool created, out bool hid)
        {
            created = false;
            hid = false;
            for (var i = 0; i < doc.Layers.Count; i++)
            {
                var layer = doc.Layers[i];
                if (layer == null || layer.IsDeleted) continue;
                if (!layer.Name.Equals(LayerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (layer.IsVisible)
                {
                    hid = true;
                    layer.IsVisible = false;
                }
                if (hid || RhinoMCPFunctions.StampPrintInk(layer))
                    doc.Layers.Modify(layer, layer.Index, true);
                return layer.Index;
            }
            var createdLayer = new Layer
            {
                Name = LayerName,
                Color = System.Drawing.Color.FromArgb(90, 90, 90),
                IsVisible = false
            };
            RhinoMCPFunctions.StampPrintInk(createdLayer);
            var index = doc.Layers.Add(createdLayer);
            created = index >= 0;
            return index;
        }

        static void ForceHidden(RhinoDoc doc, int index)
        {
            var layer = doc.Layers[index];
            if (layer == null || !layer.IsVisible) return;
            layer.IsVisible = false;
            doc.Layers.Modify(layer, index, true);
        }

        static void Rollback(RhinoDoc doc, Guid id, int layerIndex, bool created, bool hid)
        {
            if (id != Guid.Empty) doc.Objects.Delete(id, true);
            if (layerIndex < 0) return;
            if (created)
                doc.Layers.Delete(layerIndex, true);
            else if (hid)
            {
                var layer = doc.Layers[layerIndex];
                if (layer != null && !layer.IsVisible)
                {
                    layer.IsVisible = true;
                    doc.Layers.Modify(layer, layerIndex, true);
                }
            }
        }

        /// <summary>The rubber band is the snapped line, not the free mouse point.</summary>
        sealed class AxisPoint : GetPoint
        {
            readonly Point3d _from;

            public AxisPoint(Point3d from)
            {
                _from = from;
                SetCommandPrompt("End of cross section");
                SetBasePoint(_from, false);
                Constrain(Plane.WorldXY, false);
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var raw = e.CurrentPoint;
                if (Sections.TrySnapAxis(new Pt(_from.X, _from.Y), new Pt(raw.X, raw.Y), out var snapped))
                    e.Display.DrawLine(_from, new Point3d(snapped.X, snapped.Y, 0), System.Drawing.Color.Black);
                base.OnDynamicDraw(e);
            }
        }
    }
}
