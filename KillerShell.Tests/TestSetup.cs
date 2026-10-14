using System;
using System.Runtime.CompilerServices;

namespace KillerShell.Tests
{
    internal static class TestSetup
    {
        // ThemeManager builds pack:// URIs in its static fields. WPF registers the pack scheme
        // only when an Application starts, so the test host registers it before any test runs.
        [ModuleInitializer]
        internal static void RegisterPackUriScheme() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
    }
}

namespace System.Runtime.CompilerServices
{
    // net48 does not ship this attribute; the compiler only needs it to exist.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
