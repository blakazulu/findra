namespace Findra;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        return mode switch
        {
            "--names"       => 0,   // Task 7
            "--searchprobe" => 0,   // Task 10
            "--searchtest"  => 0,   // Task 10
            _               => Hello(),
        };
    }

    private static int Hello()
    {
        Log.Info("startup", $"findra {Log.Version} - no UI yet");
        Log.Flush();
        Console.WriteLine($"log: {Log.Dir}");
        return 0;
    }
}
