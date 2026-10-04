using System.Globalization;
using System.Runtime.CompilerServices;

namespace SoftParam.Tests;

// The suite runs under a decimal-comma culture so a bare double.Parse or ToString fails here, as it did on a Norwegian Mac.
static class CommaCulture
{
    [ModuleInitializer]
    internal static void Use()
    {
        var culture = new CultureInfo("nb-NO");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
