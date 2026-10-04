namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Hovering the chat takes the keyboard. Leaving it returns the keyboard to
    /// Rhino. Typing in a field that already has text, a drag, or a Rhino
    /// command that is reading keys, leaves focus where it is. The plugin
    /// setting <see cref="SettingKey"/> turns it off. The default is on.
    /// </summary>
    public static class ForskHover
    {
        public const string SettingKey = "ForskHoverFocus";

        public const string Chat = "chat";
        public const string Rhino = "rhino";
        public const string None = "none";

        /// <summary>chat, rhino, or none. edge is enter or leave. command: a Rhino command is reading the keyboard.</summary>
        public static string Decide(bool enabled, string edge, bool typing, bool dragging, bool command = false)
        {
            if (!enabled || typing || dragging || command) return None;
            if (edge == "enter") return Chat;
            if (edge == "leave") return Rhino;
            return None;
        }
    }
}
