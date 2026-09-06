using Construct.Iso;

namespace Construct.Iso.Tests;

public class PatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-iso-test-" + Guid.NewGuid().ToString("N"));
    public PatcherTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task SourceCannotBeOutput()
    {
        var path = Path.Combine(_dir, "source.iso");
        await File.WriteAllTextAsync(path, "original");
        await Assert.ThrowsAsync<ArgumentException>(() => UbuntuIsoPatcher.BuildAsync(path, path, new("", "")));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var output = Path.Combine(_dir, "output.iso");
        await File.WriteAllTextAsync(output, "keep me");
        await Assert.ThrowsAsync<IOException>(() => UbuntuIsoPatcher.BuildAsync("missing.iso", output, new("", "")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task InvalidImageLeavesNoOutputOrScratchFile()
    {
        var source = Path.Combine(_dir, "source.iso");
        await File.WriteAllBytesAsync(source, new byte[2048 * 40]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UbuntuIsoPatcher.BuildAsync(source, Path.Combine(_dir, "out.iso"), new("", "")));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task CancelledBuildDoesNotPublishAnything()
    {
        var source = Path.Combine(_dir, "source.iso");
        await File.WriteAllBytesAsync(source, new byte[2048 * 40]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UbuntuIsoPatcher.BuildAsync(source,
            Path.Combine(_dir, "out.iso"), new("", ""), cancellationToken: new CancellationToken(true)));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [UbuntuIsoFact]
    public async Task CancellationAfterTemporaryOutputCreationCleansUp()
    {
        using var cancellation = new CancellationTokenSource();
        var reachedCopy = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UbuntuIsoPatcher.BuildAsync(
            Environment.GetEnvironmentVariable("CONSTRUCT_TEST_ISO")!, Path.Combine(_dir, "out.iso"),
            AutoinstallSeed.Create("agent", "test", "test-vm", "Test", "ubuntu-server-minimal", "static", "ssh-ed25519 AAAA"),
            message =>
            {
                if (!message.StartsWith("Copying", StringComparison.Ordinal)) return;
                reachedCopy = true;
                Assert.Single(Directory.GetFiles(_dir, "*.partial"));
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.True(reachedCopy);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FailedOverwritePreservesExistingOutput()
    {
        var source = Path.Combine(_dir, "bad.iso");
        var output = Path.Combine(_dir, "out.iso");
        await File.WriteAllBytesAsync(source, new byte[2048 * 40]);
        await File.WriteAllTextAsync(output, "existing media");
        await Assert.ThrowsAsync<InvalidDataException>(() => UbuntuIsoPatcher.BuildAsync(source, output, new("", ""), overwrite: true));
        Assert.Equal("existing media", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [UbuntuIsoFact]
    public async Task JsonProtocolCanReplaceExistingMedia()
    {
        var output = Path.Combine(_dir, "out.iso");
        var key = Path.Combine(_dir, "bootstrap.pub");
        await File.WriteAllTextAsync(output, "existing media");
        await File.WriteAllTextAsync(key, "ssh-ed25519 AAAA test");
        var json = System.Text.Json.JsonSerializer.Serialize(new IsoBuildRequest
        {
            SourceIso = Environment.GetEnvironmentVariable("CONSTRUCT_TEST_ISO")!, OutputIso = output,
            BootstrapPublicKeyPath = key, Password = "quotes'\" spaces $ and Unicode ä", Overwrite = true
        });
        var request = System.Text.Json.JsonSerializer.Deserialize<IsoBuildRequest>(json)!;
        await request.BuildAsync(null, CancellationToken.None);
        Assert.True(new FileInfo(output).Length > new FileInfo(request.SourceIso).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }
}

public sealed class UbuntuIsoFactAttribute : FactAttribute
{
    public UbuntuIsoFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONSTRUCT_TEST_ISO")))
            Skip = "Set CONSTRUCT_TEST_ISO to a stock Ubuntu Server 24.04.4 amd64 ISO.";
    }
}
