using System;
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

    /// <summary>Drag a cross-section line in Top, name it, store it with section_add.</summary>
    public class ForskSectionCommand : Command
    {
        public override string EnglishName => ForskSection.CommandName;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var envelope = ForskSection.RunOnUi(true);
            RhinoApp.WriteLine(ForskTools.Receipt("section_pick", envelope));
            if (string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return Result.Success;
            var message = envelope?["message"]?.ToString() ?? "";
            if (message.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0)
                return Result.Cancel;
            return Result.Failure;
        }
    }
}
