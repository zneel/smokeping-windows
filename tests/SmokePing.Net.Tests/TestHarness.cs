using System.Diagnostics;

namespace SmokePing.Net.Tests;

/// <summary>Raised when an assertion fails; carries no stack noise into the report.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

/// <summary>Assertions used by the test cases.</summary>
public static class Assert
{
    public static void True(bool condition, string because)
    {
        if (!condition)
        {
            throw new AssertionException($"expected true: {because}");
        }
    }

    public static void False(bool condition, string because) => True(!condition, because);

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"expected <{expected}> but got <{actual}>: {because}");
        }
    }

    public static void Close(double expected, double actual, double tolerance, string because)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new AssertionException($"expected <{expected}> +/- {tolerance} but got <{actual}>: {because}");
        }
    }

    public static void IsNaN(double actual, string because)
    {
        if (!double.IsNaN(actual))
        {
            throw new AssertionException($"expected NaN but got <{actual}>: {because}");
        }
    }

    public static void Contains(string haystack, string needle, string because)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"expected to find <{needle}>: {because}");
        }
    }

    /// <summary>Asserts that an action throws an exception of the given type.</summary>
    public static TException Throws<TException>(Action action, string because)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertionException($"expected {typeof(TException).Name} but got {other.GetType().Name}: {because}");
        }

        throw new AssertionException($"expected {typeof(TException).Name} but nothing was thrown: {because}");
    }
}

/// <summary>Collects test cases and reports the results.</summary>
public sealed class TestRunner
{
    private readonly List<(string Name, Action Body)> _tests = [];

    public void Add(string name, Action body) => _tests.Add((name, body));

    /// <summary>Runs every test and returns a process exit code.</summary>
    public int Run(string? filter)
    {
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<(string Name, Exception Error)>();
        var run = 0;

        foreach (var (name, body) in _tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            run++;
            try
            {
                body();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failures.Add((name, ex));
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.Message}");
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"{run - failures.Count}/{run} passed in {stopwatch.ElapsedMilliseconds} ms.");

        return failures.Count == 0 ? 0 : 1;
    }
}

/// <summary>A temporary directory that cleans itself up.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smokeping-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
