using System.Runtime.CompilerServices;
using Findra;

/// <summary>Before any test runs, the log is sent to a folder of the test run's own, never the
/// installed Findra's (<c>LogPlacementTests</c>).</summary>
internal static class TestLogFolder
{
#pragma warning disable CA2255 // A test assembly is the one place a module initializer is meant for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Redirect() => Log.WriteTo(Path.Combine(Path.GetTempPath(), "findra-test-logs"));
}
