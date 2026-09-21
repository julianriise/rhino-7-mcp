using System;
using Rhino;
using Rhino.PlugIns;

namespace RhinoMCPPlugin
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>To complete plug-in information, please also see all PlugInDescription
    /// attributes in AssemblyInfo.cs (you might need to click "Project" ->
    /// "Show All Files" to see it in the "Solution Explorer" window).</para>
    ///</summary>
    public class RhinoMCPPlugin : Rhino.PlugIns.PlugIn
    {
        public RhinoMCPPlugin()
        {
            Instance = this;
        }
        
        ///<summary>Gets the only instance of the RhinoMCPPlugin plug-in.</summary>
        public static RhinoMCPPlugin Instance { get; private set; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                var icon = PanelIcon();
                Rhino.UI.Panels.RegisterPanel(this, typeof(Forsk.ForskPanel), "Forsk", icon);
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine("Forsk panel failed to register: " + e.Message);
            }
            return LoadReturnCode.Success;
        }

        static System.Drawing.Bitmap panelBitmap;
        static System.Drawing.Icon panelIcon;

        static System.Drawing.Icon PanelIcon()
        {
            if (panelIcon != null) return panelIcon;
            try
            {
                if (System.Drawing.SystemIcons.Application != null)
                    return System.Drawing.SystemIcons.Application;
            }
            catch
            {
                // Mono on Mac may not expose SystemIcons.
            }

            // Keep the bitmap alive: Icon.FromHandle borrows its pixels.
            panelBitmap = new System.Drawing.Bitmap(16, 16);
            using (var g = System.Drawing.Graphics.FromImage(panelBitmap))
                g.Clear(System.Drawing.Color.Black);
            panelIcon = System.Drawing.Icon.FromHandle(panelBitmap.GetHicon());
            return panelIcon;
        }
    }
}