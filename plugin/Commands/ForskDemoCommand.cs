using Rhino;
using Rhino.Commands;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Commands
{
    /// <summary>
    /// Demo flag for the viewport chrome. On hides the palettes, the right
    /// sidebar, and the top toolbar. Off, and quitting Rhino, put them back.
    /// The command field stays.
    /// </summary>
    public class ForskDemoCommand : Command
    {
        public override string EnglishName => "ForskDemo";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var next = !ForskChromeHost.Enabled();
            ForskChromeHost.SetEnabled(next);
            RhinoApp.WriteLine(next
                ? "Forsk demo chrome is on. The command field stays."
                : "Forsk demo chrome is off.");
            return Result.Success;
        }
    }
}
