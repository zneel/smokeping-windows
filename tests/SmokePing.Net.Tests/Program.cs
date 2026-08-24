namespace SmokePing.Net.Tests;

/// <summary>
/// Runs the test suite. Pass a substring to run only the tests whose name contains it.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var runner = new TestRunner();

        StorageTests.Register(runner);
        AlertTests.Register(runner);
        ConfigurationTests.Register(runner);
        GraphingTests.Register(runner);
        ProbeTests.Register(runner);
        ServiceRegistrationTests.Register(runner);
        NativeConfigTests.Register(runner);

        Console.WriteLine("SmokePing.NET test suite");
        Console.WriteLine();

        return runner.Run(args.Length > 0 ? args[0] : null);
    }
}
