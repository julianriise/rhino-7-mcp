using Rhino;
using Rhino.Commands;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Commands
{
    /// <summary>Turns Forsk Technical on or off. Off puts plans and elevations back on Forsk White.</summary>
    public class ForskTechnicalCommand : Command
    {
        public override string EnglishName => "ForskTechnical";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var next = !ForskTechnicalHost.Enabled();
            ForskTechnicalHost.SetEnabled(next);
            ForskWhiteHost.ApplyActive();
            RhinoApp.WriteLine(next ? "Forsk Technical is on." : "Forsk Technical is off.");
            return Result.Success;
        }
    }
}
