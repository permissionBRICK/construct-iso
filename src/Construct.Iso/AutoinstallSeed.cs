using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Construct.Iso;

public sealed record AutoinstallSeed(string UserData, string MetaData)
{
    public static AutoinstallSeed FromEnvironment()
    {
        static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        var keyPath = Env("BOOTSTRAP_PUBKEY_FILE", "");
        if (keyPath.Length == 0) throw new ArgumentException("Set BOOTSTRAP_PUBKEY_FILE to the bootstrap SSH public key.");
        return Create(Env("VM_USER", "agent"), Env("VM_PASS", "agent"), Env("VM_HOST", "agent-vm"),
            Env("VM_REALNAME", "The Construct"), Env("SOURCE_ID", "ubuntu-server-minimal"),
            Env("VM_HOSTNAME_SOURCE", "static"), File.ReadAllText(keyPath).Trim());
    }

    public static AutoinstallSeed Create(string user, string password, string hostname, string realName,
        string sourceId, string hostnameSource, string publicKey, string? salt = null)
    {
        if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$")) throw new ArgumentException("Invalid seed username.");
        if (hostnameSource is not ("static" or "hyperv-kvp")) throw new ArgumentException("Unknown hostname source; use static or hyperv-kvp.");
        if (!Regex.IsMatch(hostname, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) throw new ArgumentException("Invalid seed hostname.");
        if (!Regex.IsMatch(sourceId, "^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid installer source ID.");
        if (publicKey.Contains('\n') || publicKey.Contains('\r') ||
            !Regex.IsMatch(publicKey, "^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(?:256|384|521)) [A-Za-z0-9+/]+={0,2}( .*)?$"))
            throw new ArgumentException("Invalid bootstrap SSH public key.");
        var generic = hostnameSource == "hyperv-kvp";
        var identity = generic ? "construct-seed" : hostname;
        static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));
        var values = new Dictionary<string, string>
        {
            ["VM_USER"] = user, ["IDENTITY_HOSTNAME"] = identity, ["SOURCE_ID"] = sourceId,
            ["PASS_HASH"] = Sha512Crypt.Hash(password, salt),
            ["REALNAME_JSON"] = JsonSerializer.Serialize(realName),
            ["PUBLIC_KEY_JSON"] = JsonSerializer.Serialize(publicKey),
            ["BANNER_TARGET"] = generic ? "<this VM's name>.mshome.net" : hostname + ".mshome.net",
            ["HOSTNAME_SCRIPT_B64"] = B64(Template("hostname.sh")),
            ["HOSTNAME_UNIT_B64"] = B64(Template("hostname.service")),
            ["HOSTNAME_DEFAULT_B64"] = B64($"CONSTRUCT_HOSTNAME_SOURCE={hostnameSource}\n")
        };
        string Render(string text) => Regex.Replace(text, @"\$\{([A-Z_][A-Z0-9_]*)\}", m =>
            values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new InvalidDataException("Unknown seed template variable."));
        values["BANNER_B64"] = B64(Render(Template("banner.txt")));
        // JSON strings are valid YAML scalars; substitute whole scalars so quotes/comments cannot alter YAML.
        var template = Template("user-data.txt")
            .Replace("\"${VM_REALNAME}\"", "${REALNAME_JSON}")
            .Replace("${BOOTSTRAP_PUBKEY}", "${PUBLIC_KEY_JSON}");
        return new(Render(template + (generic ? Template("generic-user-data.txt") : "")),
            $"instance-id: {identity}\nlocal-hostname: {identity}\n");
    }

    private static string Template(string name)
    {
        using var stream = typeof(AutoinstallSeed).Assembly.GetManifestResourceStream($"Construct.Iso.Templates.{name}")
            ?? throw new InvalidDataException("Missing embedded seed template.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
