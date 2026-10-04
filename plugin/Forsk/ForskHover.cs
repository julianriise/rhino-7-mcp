namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Hovering the chat takes the keyboard. Leaving it returns the keyboard to
    /// Rhino. Typing in a field that already has text, or a drag, leaves focus
    /// where it is. The plugin setting <see cref="SettingKey"/> turns it off.
    /// The default is on.
    /// </summary>
    public static class ForskHover
    {
        public const string SettingKey = "ForskHoverFocus";

        public const string Chat = "chat";
        public const string Rhino = "rhino";
        public const string None = "none";

        /// <summary>chat, rhino, or none. edge is enter or leave.</summary>
        public static string Decide(bool enabled, string edge, bool typing, bool dragging)
        {
            if (!enabled || typing || dragging) return None;
            if (edge == "enter") return Chat;
            if (edge == "leave") return Rhino;
            return None;
        }
    }
}
