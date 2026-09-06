using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Construct.Iso;

namespace Construct.Iso.Tests;

public class SeedTests
{
    // Cross-checked against OpenSSL passwd -6, including a multibyte UTF-8 password.
    [Theory]
    [InlineData("test password", "testsalt", "$6$testsalt$0RgN/NxJEf8U.c.ijMFFr450LCFC8YDZy.vcV/Qd7djn7YTesjDhdnd1WYJ5/YGp134MZH2xsHemLf6OX8tXe1")]
    [InlineData("pāss🔑", "abc", "$6$abc$PpgNo9QDwgx.hu1/dkskxM9D0JYM1MvTGA9XXpVx1iadN6034lhpOeWhRLVChPuoDxVqYmjx/jB8uyBungBNz1")]
    public void HashMatchesIndependentImplementation(string password, string salt, string expected) =>
        Assert.Equal(expected, Sha512Crypt.Hash(password, salt));

    [Fact]
    public void PasswordHashesUseFreshSalts() => Assert.NotEqual(Sha512Crypt.Hash("agent"), Sha512Crypt.Hash("agent"));

    [Fact]
    public void StaticSeedHasProvisionerBannerAndNoGenericSudoRule()
    {
        var seed = Seed("static");
        Assert.Equal("instance-id: test-vm\nlocal-hostname: test-vm\n", seed.MetaData);
        Assert.Contains("hostname: test-vm", seed.UserData);
        Assert.Contains("id: ubuntu-server-minimal", seed.UserData);
        Assert.DoesNotContain("NOPASSWD", seed.UserData);
        var banner = DecodePayloads(seed.UserData).Single();
        Assert.Contains(".\\Provision-AgentVM.ps1", banner);
        Assert.Contains("test-vm.mshome.net", banner);
        Assert.DoesNotContain("test password", seed.UserData);
    }

    [Fact]
    public void GenericSeedCarriesOriginalHostnameAdoptionScript()
    {
        var seed = Seed("hyperv-kvp");
        Assert.Contains("hostname: construct-seed", seed.UserData);
        Assert.DoesNotContain("test-vm", seed.UserData + seed.MetaData);
        Assert.Contains("linux-cloud-tools-virtual", seed.UserData);
        Assert.Contains("agent ALL=(ALL) NOPASSWD:ALL", seed.UserData);
        var payloads = DecodePayloads(seed.UserData);
        Assert.Contains(payloads, p => p.Contains("VirtualMachineName") && p.Contains("hostnamectl"));
        Assert.Contains("CONSTRUCT_HOSTNAME_SOURCE=hyperv-kvp\n", payloads);
        Assert.Contains(payloads, p => p.Contains("ExecStart=/usr/local/sbin/construct-hostname.sh"));
    }

    [Theory]
    [InlineData("a\"\n  malicious: true")]
    [InlineData("${VM_USER} ${UNKNOWN}")]
    public void RealNameRemainsOneLiteralYamlScalar(string realName)
    {
        var seed = AutoinstallSeed.Create("agent", "test password", "test-vm", realName,
            "ubuntu-server-minimal", "static", "ssh-ed25519 AAAA comment");
        var scalar = Regex.Match(seed.UserData, @"(?m)^    realname: (.+)$").Groups[1].Value;
        Assert.Equal(realName, JsonSerializer.Deserialize<string>(scalar));
    }

    [Theory]
    [InlineData("bad user", "test-vm", "static", "ssh-ed25519 AAAA")]
    [InlineData("agent", "bad\nhost", "static", "ssh-ed25519 AAAA")]
    [InlineData("agent", "test-vm", "cloud-init-metadata", "ssh-ed25519 AAAA")]
    [InlineData("agent", "test-vm", "static", "ssh-ed25519 AAAA\nmalicious")]
    public void InvalidInputsAreRejected(string user, string host, string source, string key) =>
        Assert.Throws<ArgumentException>(() => AutoinstallSeed.Create(user, "test", host, "Test", "ubuntu-server", source, key));

    [Fact]
    public void GrubPreservesManualEntriesButOwnsDefaultAndTimeout()
    {
        var patched = UbuntuIsoPatcher.PatchGrub("set timeout=30\r\nset default=2\r\nmenuentry 'manual' {}\r\n");
        Assert.StartsWith("set timeout=5\nset default=0\n", patched);
        Assert.Contains("ds=nocloud\\;s=/cdrom/boot/grub/", patched);
        Assert.Contains("menuentry 'manual' {}", patched);
        Assert.DoesNotContain("set default=2", patched);
        Assert.DoesNotContain('\r', patched);
    }

    private static AutoinstallSeed Seed(string source) => AutoinstallSeed.Create("agent", "test password", "test-vm",
        "The Construct", "ubuntu-server-minimal", source, "ssh-ed25519 AAAA bootstrap@construct", "testsalt");
    private static string[] DecodePayloads(string text) => Regex.Matches(text, @"echo ([A-Za-z0-9+/=]+) \| base64 -d")
        .Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))).ToArray();
}
