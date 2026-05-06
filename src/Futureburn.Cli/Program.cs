using System.Reflection;
using Futureburn.Core.Imapi;

string version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";

Console.WriteLine($"futureburn v{version}");

if (args.Length == 0)
{
    return PrintUsage();
}

return args[0].ToLowerInvariant() switch
{
    "drives" or "--drives" or "-d" => ListDrives(),
    "help" or "--help" or "-h" => PrintUsage(),
    _ => Unknown(args[0]),
};

static int PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("usage:");
    Console.WriteLine("  futureburn drives        List optical drives on this system");
    Console.WriteLine();
    Console.WriteLine("Burning isn't wired up yet. Soon.");
    return 0;
}

static int ListDrives()
{
    var drives = DriveEnumerator.Enumerate();
    Console.WriteLine();

    if (drives.Count == 0)
    {
        Console.WriteLine("No optical drives found.");
        Console.WriteLine("(None installed, or Windows can't see them.)");
        return 0;
    }

    Console.WriteLine($"Found {drives.Count} optical drive{(drives.Count == 1 ? "" : "s")}:");
    Console.WriteLine();
    foreach (var d in drives)
    {
        var letters = d.MountPoints.Count > 0 ? string.Join(", ", d.MountPoints) : "(no drive letter)";
        Console.WriteLine($"  {letters}");
        Console.WriteLine($"    Vendor:   {d.VendorId}");
        Console.WriteLine($"    Product:  {d.ProductId}");
        Console.WriteLine($"    Revision: {d.Revision}");
        Console.WriteLine($"    Id:       {d.UniqueId}");
        Console.WriteLine();
    }
    return 0;
}

static int Unknown(string cmd)
{
    Console.WriteLine();
    Console.WriteLine($"Unknown command: {cmd}");
    Console.WriteLine("Try `futureburn help`.");
    return 1;
}
