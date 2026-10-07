using System;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The one place the chat's Grok model is named. Change Default to upgrade every user;
    /// FORSK_GROK_MODEL still overrides it on one Mac.
    /// </summary>
    public static class ForskGrokModel
    {
        /// <summary>xAI's cheap all-rounder: tool calling, 1M context, $1.25 in / $2.50 out per 1M tokens.</summary>
        public const string Default = "grok-4.3";
        public const string OverrideVariable = "FORSK_GROK_MODEL";

        public static string Current() => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));

        public static string Resolve(string over) => string.IsNullOrWhiteSpace(over) ? Default : over.Trim();
    }
}
