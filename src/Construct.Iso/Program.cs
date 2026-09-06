using Construct.Iso;
using System.Text.Json;
using System.Text;

var stdinRequest = args.Length == 1 && args[0] == "--request-stdin";
if (!stdinRequest && (args.Length != 2 || args.Contains("--help")))
{
    Console.WriteLine("Usage: Construct.Iso SOURCE.iso OUTPUT.iso");
    Console.WriteLine("       Construct.Iso --request-stdin  (JSON request on stdin, including seed credentials)");
    Console.WriteLine("Build Ubuntu autoinstall media without WSL or external programs.");
    Console.WriteLine("Environment: VM_USER, VM_PASS, VM_HOST, VM_REALNAME, SOURCE_ID,");
    Console.WriteLine("  VM_HOSTNAME_SOURCE (static or hyperv-kvp), BOOTSTRAP_PUBKEY_FILE (required).");
    return args.Contains("--help") ? 0 : 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (stdinRequest)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        var request = JsonSerializer.Deserialize<IsoBuildRequest>(await reader.ReadToEndAsync(cancellation.Token),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Missing build request.");
        await request.BuildAsync(Console.WriteLine, cancellation.Token);
    }
    else
    {
        var seed = AutoinstallSeed.FromEnvironment();
        await UbuntuIsoPatcher.BuildAsync(args[0], args[1], seed, Console.WriteLine, cancellation.Token);
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("ISO build cancelled; output was not published.");
    return 130;
}
catch (JsonException)
{
    Console.Error.WriteLine("Invalid JSON build request.");
    return 2;
}
catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
