namespace Construct.Iso.Tests;

public class TemplateParityTests
{
    [Theory]
    [InlineData("banner.txt", "cat >\"${BANNER_FILE}\" <<EOF\n", "\nEOF")]
    [InlineData("hostname.service", "cat >\"${WORK}/construct-hostname.service\" <<'UNIT'\n", "\nUNIT")]
    [InlineData("hostname.sh", "cat >\"${WORK}/construct-hostname.sh\" <<'GUESTSCRIPT'\n", "\nGUESTSCRIPT")]
    [InlineData("user-data.txt", "cat >\"${WORK}/${SEED_DIR_NAME}/user-data\" <<EOF\n", "\nEOF")]
    [InlineData("generic-user-data.txt", "cat >>\"${WORK}/${SEED_DIR_NAME}/user-data\" <<EOF\n", "\nEOF")]
    public void NativePayloadTemplatesMatchExistingShellBuilder(string name, string start, string end)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "reference/build-autoinstall-iso.sh"))) root = root.Parent;
        Assert.NotNull(root);
        var shell = File.ReadAllText(Path.Combine(root.FullName, "reference/build-autoinstall-iso.sh")).Replace("\r\n", "\n");
        var expected = shell.Split(start, 2)[1].Split(end, 2)[0] + "\n";
        if (name == "banner.txt") expected = expected.Replace("\\\\", "\\"); // unquoted bash heredoc escaping
        var actual = File.ReadAllText(Path.Combine(root.FullName, "src/Construct.Iso/Templates", name)).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }
}
