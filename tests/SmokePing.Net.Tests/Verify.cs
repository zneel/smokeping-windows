using Xunit.Sdk;

namespace SmokePing.Net.Tests;

/// <summary>Raised when a check fails, carrying the explanation with it.</summary>
public sealed class VerificationException : XunitException
{
    public VerificationException(string message) : base(message)
    {
    }
}

/// <summary>
/// The assertion vocabulary used across these tests.
///
/// xunit's own assertions deliberately take no message, so a failure reads
/// "Assert.Equal() Failure: Expected 30, Actual 20" and leaves you to work out which
/// of the six checks in the test that was and why it mattered. Every check here says
/// what it was checking, because a test that fails a year from now has to explain
/// itself to somebody who has never read it.
/// </summary>
public static class Verify
{
    public static void True(bool condition, string because)
    {
        if (!condition)
        {
            throw new VerificationException($"expected true: {because}");
        }
    }

    public static void False(bool condition, string because) => True(!condition, because);

    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new VerificationException($"expected <{expected}> but got <{actual}>: {because}");
        }
    }

    public static void Close(double expected, double actual, double tolerance, string because)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new VerificationException($"expected <{expected}> +/- {tolerance} but got <{actual}>: {because}");
        }
    }

    public static void IsNaN(double actual, string because)
    {
        if (!double.IsNaN(actual))
        {
            throw new VerificationException($"expected NaN but got <{actual}>: {because}");
        }
    }

    public static void Contains(string haystack, string needle, string because)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new VerificationException($"expected to find <{needle}>: {because}");
        }
    }

    /// <summary>Asserts that an awaited operation throws an exception of the given type.</summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string because)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new VerificationException($"expected {typeof(TException).Name} but got {other.GetType().Name}: {because}");
        }

        throw new VerificationException($"expected {typeof(TException).Name} but nothing was thrown: {because}");
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
            throw new VerificationException($"expected {typeof(TException).Name} but got {other.GetType().Name}: {because}");
        }

        throw new VerificationException($"expected {typeof(TException).Name} but nothing was thrown: {because}");
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
