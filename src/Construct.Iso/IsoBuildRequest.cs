namespace Construct.Iso;

/// <summary>Version 1 installer/service protocol. Credentials travel on stdin, never the command line.</summary>
public sealed class IsoBuildRequest
{
    public string SourceIso { get; init; } = "";
    public string OutputIso { get; init; } = "";
    public string BootstrapPublicKeyPath { get; init; } = "";
    public string User { get; init; } = "agent";
    public string Password { get; init; } = "agent";
    public string Hostname { get; init; } = "agent-vm";
    public string RealName { get; init; } = "The Construct";
    public string SourceId { get; init; } = "ubuntu-server-minimal";
    public string HostnameSource { get; init; } = "static";
    public bool Overwrite { get; init; }

    public Task BuildAsync(Action<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceIso);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputIso);
        ArgumentException.ThrowIfNullOrWhiteSpace(BootstrapPublicKeyPath);
        var seed = AutoinstallSeed.Create(User, Password, Hostname, RealName, SourceId, HostnameSource,
            File.ReadAllText(BootstrapPublicKeyPath).Trim());
        return UbuntuIsoPatcher.BuildAsync(SourceIso, OutputIso, seed, progress, cancellationToken, Overwrite);
    }
}
