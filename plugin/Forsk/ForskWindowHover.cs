using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    sealed partial class ForskWindow
    {
        /// <summary>The viewport pointer rests over an element (named by these ids), or over nothing.</summary>
        void ViewHovered(IReadOnlyList<string> elementIds)
        {
            ShowHover(_link.Hover(HoverOn(), elementIds));
        }

        /// <summary>The pointer is in the chat now: whatever the viewport lit is no longer under it.</summary>
        void ClearViewHover()
        {
            _viewHover.Forget();
            ShowHover(_link.Clear());
        }

        /// <summary>The receipt rows to light, empty to clear, null when nothing changed.</summary>
        void ShowHover(string[] rows)
        {
            if (rows == null || !_ready) return;
            Script("Forsk.hover", new JArray(rows));
        }
    }
}
