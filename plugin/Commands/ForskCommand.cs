using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Commands
{
    public class ForskCommand : Command
    {
        public override string EnglishName => "Forsk";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(typeof(ForskPanel));
            ForskPanel.FocusAfterCommand(doc);
            return Result.Success;
        }
    }

    public class ForskChatCommand : Command
    {
        public override string EnglishName => "ForskChat";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            Panels.OpenPanel(typeof(ForskPanel));
            ForskPanel.FocusAfterCommand(doc);
            return Result.Success;
        }
    }

    public class ForskWebCommand : Command
    {
        public override string EnglishName => "ForskWeb";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            ForskWebForm.Open();
            return Result.Success;
        }
    }

    /// <summary>Set the imported plan's scale: two points and the real length between them.</summary>
    public class ForskSetScaleCommand : Command
    {
        public override string EnglishName => ForskPlanImport.ScaleCommand;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            return ForskPlanImport.RunScaleCommand();
        }
    }
}
