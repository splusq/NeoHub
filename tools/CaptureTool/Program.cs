using CaptureTool;

if (args.Length < 2)
{
    Console.Error.WriteLine("""
        usage:
          CaptureTool analyze  <capture.log>...
          CaptureTool fixtures <outdir> <markers.txt|-> <capture.log>...
        """);
    return 2;
}

return args[0] switch
{
    "analyze" => Commands.Analyze(args[1..]),
    "fixtures" => Commands.Fixtures(args[1], args[2], args[3..]),
    _ => 2,
};
