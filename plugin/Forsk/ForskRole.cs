using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The voices of the one window. None is the router's General turn.
    /// More roles may come. Area statistics land under Analyser later in v3.
    /// </summary>
    public enum ForskRole
    {
        None,
        Planner,
        Modeller,
        Plotter,
        Analyser,
        Render
    }

    /// <summary>
    /// The role on an answer is the router's label for that turn, unless the
    /// user's override was set when the turn was sent. A role sharpens the
    /// suggestions and the tool order. It is not a mode: slot 1, the history
    /// and the full tool list do not change with it. Render names a turn and
    /// has no tool pack. No RhinoCommon.
    /// </summary>
    public static class ForskRoles
    {
        /// <summary>Menu order. Render names the turn. It has no tool pack, so the tool order stays the router's. Auto follows these, as the clear.</summary>
        public static readonly IReadOnlyList<ForskRole> Pickable = new[] { ForskRole.Planner, ForskRole.Modeller, ForskRole.Plotter, ForskRole.Analyser, ForskRole.Render };

        /// <summary>The router's role. Import is Planner, build and edit are Modeller, print and sheets are Plotter, daylight is Analyser.</summary>
        public static ForskRole Of(ForskIntent intent)
        {
            switch (intent)
            {
                case ForskIntent.Import:
                case ForskIntent.Dxf:
                    return ForskRole.Planner;
                case ForskIntent.Build:
                case ForskIntent.Edit:
                    return ForskRole.Modeller;
                case ForskIntent.Print:
                case ForskIntent.Sheets:
                    return ForskRole.Plotter;
                case ForskIntent.Daylight:
                    return ForskRole.Analyser;
                default:
                    return ForskRole.None;
            }
        }

        /// <summary>
        /// The role that knows an action. Daylight analysis is Analyser.
        /// Adding a window stays Modeller: that is geometry. rooms.list stays
        /// Planner. Area statistics land under Analyser later in v3, not yet.
        /// The bridge and help have no role.
        /// </summary>
        public static ForskRole OfAction(string actionId)
        {
            switch (actionId)
            {
                case "file.import":
                case "file.use_curves":
                case "file.scale":
                case "file.check":
                case "file.draw":
                case "rooms.list":
                    return ForskRole.Planner;
                case "file.generate":
                case "file.rebuild":
                case "wall.split":
                case "wall.move":
                case "wall.delete":
                case "room.push_pull":
                case "exist.mark":
                case "edit.undo":
                case "opening.move":
                case "opening.resize":
                case "opening.type":
                case "opening.delete":
                case "opening.add_door":
                case "daylight.window":
                    return ForskRole.Modeller;
                case "file.print":
                case "print.one":
                case "print.clear":
                case "sheets.clear":
                case "meta.title":
                case "ink.set":
                case "section.add":
                case "section.room":
                case "section.remove":
                    return ForskRole.Plotter;
                case "daylight.rooms":
                case "daylight.run":
                case "daylight.again":
                case "daylight.hide":
                case "daylight.show":
                case "daylight.room":
                    return ForskRole.Analyser;
                default:
                    return ForskRole.None;
            }
        }

        /// <summary>The mark on a typed turn's answer and on its thinking line.</summary>
        public static string Mark(ForskIntent intent, ForskRole overrideRole)
        {
            if (overrideRole != ForskRole.None) return Label(overrideRole);
            var role = Of(intent);
            return role == ForskRole.None ? null : Label(role);
        }

        /// <summary>The mark on a pill's receipts: the role that knows that action.</summary>
        public static string MarkForAction(string actionId)
        {
            var role = OfAction(actionId);
            return role == ForskRole.None ? null : Label(role);
        }

        public static string Label(ForskRole role)
        {
            return role == ForskRole.None ? ForskText.Get("role.auto") : ForskText.Get("role." + role.ToString().ToLowerInvariant());
        }

        /// <summary>The control's value back to a role. Anything but a pickable role is Auto.</summary>
        public static ForskRole Parse(string id)
        {
            foreach (var role in Pickable)
                if (string.Equals(role.ToString(), id, StringComparison.OrdinalIgnoreCase)) return role;
            return ForskRole.None;
        }

        /// <summary>The control in the view model: the pickable roles in menu order, then Auto. The value is the override.</summary>
        public static JObject Control(ForskRole overrideRole)
        {
            var options = new JArray();
            foreach (var role in Pickable)
                options.Add(new JObject { ["id"] = role.ToString().ToLowerInvariant(), ["label"] = Label(role) });
            options.Add(new JObject { ["id"] = "auto", ["label"] = Label(ForskRole.None) });
            return new JObject
            {
                ["value"] = overrideRole == ForskRole.None ? "auto" : overrideRole.ToString().ToLowerInvariant(),
                ["label"] = ForskText.Get("role.control"),
                ["title"] = ForskText.Get("role.title"),
                ["options"] = options
            };
        }
    }
}
