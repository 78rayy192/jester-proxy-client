namespace Jester.Regression;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--preview-material", StringComparer.OrdinalIgnoreCase)
            || args.Contains("--preview-tray", StringComparer.OrdinalIgnoreCase))
            return MaterialPreview.Run(args);
        var suite = new RegressionSuite();
        ConfigRegression.Register(suite);
        CoreRegression.Register(suite);
        ApiRegression.Register(suite);
        ProfileRegression.Register(suite);
        NativeConfigRegression.Register(suite);
        return suite.Run();
    }
}

internal sealed class RegressionSuite
{
    readonly List<(string Name, Action Check)> _checks = [];
    public void Case(string name, Action check) => _checks.Add((name, check));

    public int Run()
    {
        var failed = 0;
        foreach (var (name, check) in _checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        Console.WriteLine($"{_checks.Count - failed}/{_checks.Count} regression checks passed.");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message = "Unexpected value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
