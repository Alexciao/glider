using Glider.ContractGen;

// Usage: dotnet run --project tools/Glider.ContractGen [-- --check]
var root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Environment.CurrentDirectory)
    ?? throw new InvalidOperationException("Could not locate repo root (Glider.slnx)");
var path = Path.Combine(root, TsContractGenerator.RelativeOutputPath);
var generated = TsContractGenerator.Generate();

if (args.Contains("--check"))
{
    var current = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
    if (current != generated)
    {
        Console.Error.WriteLine($"{TsContractGenerator.RelativeOutputPath} is stale. Run: dotnet run --project tools/Glider.ContractGen");
        return 1;
    }

    Console.WriteLine("contract.gen.ts is up to date");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(path)!);
File.WriteAllText(path, generated);
Console.WriteLine($"Wrote {path}");
return 0;

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}
