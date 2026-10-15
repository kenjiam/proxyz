namespace Proxyz.Core.Tests;

// Dependency-free executable tests. A nonzero exit code is a failure; there is no
// dependency on a NuGet test adapter. See scripts/test.ps1 and docs/TESTING.md.
internal sealed class TestSuite
{
    private readonly List<(string Name, Action Test)> _tests = [];
    public void Add(string name, Action action) => _tests.Add((name, action));
    public int Run()
    {
        int failed = 0;
        foreach (var (name, test) in _tests)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + e); }
        }
        Console.WriteLine($"{_tests.Count - failed}/{_tests.Count} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
internal static class Check
{
    public static void True(bool condition, string message = "Expected true")
    { if (!condition) throw new Exception(message); }
    public static void False(bool condition, string message = "Expected false") => True(!condition, message);
    public static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T e) { return e; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
