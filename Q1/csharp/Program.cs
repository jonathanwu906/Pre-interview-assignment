using System.Text.Json;

namespace Permissions;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Tests.RunAll();
            if (string.Equals(args[0], "benchmark", StringComparison.Ordinal))
            {
                int chains = args.Length > 1 ? int.Parse(args[1]) : 500;
                int depth = args.Length > 2 ? int.Parse(args[2]) : 200;
                int files = args.Length > 3 ? int.Parse(args[3]) : 100_000;
                int entries = args.Length > 4 ? int.Parse(args[4]) : 2;
                return Benchmark.Run(chains, depth, files, entries);
            }
            if (File.Exists(args[0]))
                return RunFile(args[0]);
            Console.Error.WriteLine($"input file not found: {args[0]}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine($"invalid input: {ex.Message}");
            PrintUsage();
            return 2;
        }
    }

    /// <summary>Evaluate a JSON input document and print the names of files the user can read, in input order.</summary>
    private static int RunFile(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonSerializer.Deserialize<InputDoc>(File.ReadAllText(path), options)
                  ?? throw new InvalidDataException("empty input document");
        var groups = new Dictionary<string, Group>(doc.Groups, StringComparer.Ordinal);
        var checker = new PermissionChecker(groups);
        var closure = checker.ComputeClosure(doc.User);
        foreach (var f in doc.Files)
            if (PermissionChecker.CanRead(f.Acl, closure))
                Console.WriteLine(f.Name);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine("  dotnet run                                          run the 13 tests");
        Console.Error.WriteLine("  dotnet run -- <input.json>                          print files readable by the user");
        Console.Error.WriteLine("  dotnet run -c Release -- benchmark [chains depth files entriesPerFile]");
        Console.Error.WriteLine("                                                      benchmark naive vs design with differential validation");
    }
}
