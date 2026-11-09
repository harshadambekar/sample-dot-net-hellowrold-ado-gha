using System.Runtime.InteropServices;

namespace HelloWorld;

public static class Greeter
{
    public static string Greet(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Hello, World!" : $"Hello, {name.Trim()}!";

    // Surfaces the process bitness so an x86 build is visible at runtime.
    public static string Describe() =>
        $"Running as {RuntimeInformation.ProcessArchitecture} on .NET {Environment.Version}";
}
