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
            _               => 0,   // the UI, later plans
        };
    }
}
