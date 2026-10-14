using Hutu.Reference;

// The reference collector: reads a case's page in a real browser and writes what it measured beside
// the case, where the conformance tests read it back.
//
//   dotnet run --project tools/Hutu.Reference -- --case conformance/font-relative-boxes
//
// The reference is a checked-in file, so the browser that produced it is recorded inside it: a number
// that cannot be traced to a viewport, a scale factor and a script-free document is an anecdote.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Hutu.Reference --case <directory|case.json> [--source <file|url>] [--out <reference.json>]");
    return 1;
}

String? caseArgument = null;
String? source = null;
String? output = null;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--case":
            caseArgument = Next(args, ref index);
            break;
        case "--source":
            source = Next(args, ref index);
            break;
        case "--out":
            output = Next(args, ref index);
            break;
        default:
            Console.Error.WriteLine($"unknown argument '{args[index]}'");
            return 1;
    }
}

if (caseArgument is null)
{
    Console.Error.WriteLine("--case is required");
    return 1;
}

var directory = Directory.Exists(caseArgument) ? caseArgument : Path.GetDirectoryName(caseArgument);
var manifest = Directory.Exists(caseArgument)
    ? Path.Combine(caseArgument!, "case.json")
    : caseArgument!;

var request = ReferenceCase.Load(manifest);

// A case names its page; a one-off capture of a live address overrides it, which is how a page worth
// looking at becomes a case before anyone has written one.
source ??= request.Page;

Console.WriteLine($"capturing {source} at {request.ViewportWidth}x{request.ViewportHeight} content width");

var reference = await ReferenceCollector.CaptureAsync(request, directory ?? ".", source);

output ??= Path.Combine(directory ?? ".", "reference.json");
reference.Save(output);

var missing = reference.Elements.Where(e => !e.Found).Select(e => e.Selector).ToList();

Console.WriteLine($"  {reference.Elements.Count} boxes, document {reference.Environment.ClientWidth}x{reference.Environment.ScrollHeight}");
Console.WriteLine($"  browser {reference.Environment.Browser}, dpr {reference.Environment.DeviceScaleFactor}, " +
                  $"scripts neutralized {reference.Environment.ScriptsNeutralized}, webfonts {reference.Environment.WebFonts}");

if (missing.Count > 0)
{
    Console.WriteLine($"  {missing.Count} selectors matched nothing: {string.Join(", ", missing.Take(8))}");
}

Console.WriteLine($"  {Path.GetRelativePath(Environment.CurrentDirectory, output)}");
return 0;

static String Next(String[] args, ref Int32 index)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"{args[index]} needs a value");
    }

    return args[++index];
}
