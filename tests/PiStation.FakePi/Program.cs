using PiStation.FakePi;

if (args.Contains("--terminal-mouse-probe", StringComparer.Ordinal))
{
    return TerminalMouseProbe.Run(
        transitionOnly: args.Contains("--transition-only", StringComparer.Ordinal));
}

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine("0.84.4");
    return 0;
}

var arguments = FakePiArguments.Parse(args);
using var server = new FakePiServer(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    Console.Error,
    arguments);
return await server.RunAsync();
