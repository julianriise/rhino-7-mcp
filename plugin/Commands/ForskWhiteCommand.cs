using Rhino;
using Rhino.Commands;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Commands
{
    /// <summary>Turns Forsk White on or off. Off sends model views back to Shaded.</summary>
    public class ForskWhiteCommand : Command
    {
        public override string EnglishName => "ForskWhite";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var next = !ForskWhiteHost.Enabled();
            ForskWhiteHost.SetEnabled(next);
            ForskWhiteHost.ApplyActive();
            RhinoApp.WriteLine(next ? "Forsk White is on." : "Forsk White is off.");
            return Result.Success;
        }
    }
}
