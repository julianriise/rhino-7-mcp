using Rhino.PlugIns;
using RhinoMCPPlugin.Forsk;

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
            // Forsk is a floating window (Forsk, ForskChat). There is no panel to register.
            Functions.ForskWhiteHost.Start();
            Functions.ForskChromeHost.Start();
            Functions.ForskWallHatchHost.Start();
            Functions.RoomTypeColorHost.Start();
            Functions.ForskInteriorHost.Start();
            ForskSupportHttp.RetryOnStartup();
            ForskUpdate.CheckOnStartup();
            ForskWhatsNewGate.Prepare();
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            Functions.ForskInteriorHost.Stop();
            Functions.RoomTypeColorHost.Stop();
            Functions.ForskWallHatchHost.Stop();
            Functions.ForskChromeHost.Stop();
            Functions.ForskWhiteHost.Stop();
        }
    }
}