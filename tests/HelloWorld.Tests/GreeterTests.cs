using System.Runtime.InteropServices;
using HelloWorld;

namespace HelloWorld.Tests;

public class GreeterTests
{
    [Fact]
    public void Greet_without_name_says_hello_world() =>
        Assert.Equal("Hello, World!", Greeter.Greet(null));

    [Theory]
    [InlineData("Ada", "Hello, Ada!")]
    [InlineData("  Grace  ", "Hello, Grace!")]
    [InlineData("", "Hello, World!")]
    [InlineData("   ", "Hello, World!")]
    public void Greet_uses_trimmed_name_or_falls_back(string? name, string expected) =>
        Assert.Equal(expected, Greeter.Greet(name));

    [Fact]
    public void Describe_reports_process_architecture() =>
        Assert.StartsWith($"Running as {RuntimeInformation.ProcessArchitecture}", Greeter.Describe());

#if X86_BUILD
    // Parity check for the migration: proves vstest.console ran the x86 testhost
    // (ADO: VSTest@3 platform: x86 + DOTNET_ROOT for x86).
    [Fact]
    public void X86_build_runs_in_a_32_bit_process()
    {
        Assert.False(Environment.Is64BitProcess);
        Assert.Equal(Architecture.X86, RuntimeInformation.ProcessArchitecture);
    }
#endif
}
