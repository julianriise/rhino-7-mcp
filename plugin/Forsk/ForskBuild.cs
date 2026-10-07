using System;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Which Forsk is running, for the line at the foot of Settings: the
    /// release from Package Manager, or the test copy the smokes and
    /// mcpstart use (MacPlugIns). Both carry one plug-in id, so Rhino loads
    /// one of them; scripts/use_release.sh and use_dev.sh switch.
    /// </summary>
    public static class ForskBuild
    {
        public const string PackageMark = "/packages/";

        /// <summary>"Forsk 1.1.0 · Package Manager" or "Forsk 1.1.0 · test copy".</summary>
        public static string Describe(Version version, string location)
        {
            var number = version == null ? "?" : version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
            var fromPackage = (location ?? "").Replace('\\', '/').IndexOf(PackageMark, StringComparison.OrdinalIgnoreCase) >= 0;
            return "Forsk " + number + " · " + (fromPackage ? "Package Manager" : "test copy");
        }

        /// <summary>This assembly's line.</summary>
        public static string Line()
        {
            try
            {
                var assembly = typeof(ForskBuild).Assembly;
                return Describe(assembly.GetName().Version, assembly.Location);
            }
            catch (Exception)
            {
                return "Forsk";
            }
        }
    }
}
