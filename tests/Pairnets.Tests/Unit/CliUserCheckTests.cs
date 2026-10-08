using Microsoft.Data.Sqlite;
using Pairnets.Server.Cli;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>The server's maintenance commands run only as the user that owns the data folder (pairnets).</summary>
public sealed class CliUserCheckTests
{
    private const string DataDir = "/var/lib/pairnets";

    [Theory]
    [InlineData("pairnets", "root", true)] // plain sudo: would leave root-owned database files behind
    [InlineData("pairnets", "someone", true)] // an ordinary user: cannot open the files anyway
    [InlineData("pairnets", "pairnets", false)]
    [InlineData("root", "root", false)]
    public void OnlyTheOwnerOfTheDataFolderMayRunACommand(string owner, string current, bool refused)
    {
        var problem = DataDirUser.Problem(new DataDirUser.Users(owner, current), DataDir, ["owner-link"]);

        if (!refused)
        {
            Assert.Null(problem);
            return;
        }
        Assert.NotNull(problem);
        Assert.StartsWith($"Run this as the {owner} user", problem);
        Assert.EndsWith($"sudo -u {owner} /opt/pairnets/pairnets-server owner-link", problem);
        Assert.DoesNotContain('\n', problem);
    }

    [Fact]
    public void WhenTheUsersCannotBeToldNothingIsRefused() =>
        Assert.Null(DataDirUser.Problem(null, DataDir, ["devices", "list"]));

    [Fact]
    public void TheHintRepeatsTheCommandSoItCanBePasted()
    {
        var problem = DataDirUser.Problem(new DataDirUser.Users("pairnets", "root"), "/srv/my nest", ["rescan", "--data-dir", "/srv/my nest", "--dry-run"]);
        Assert.EndsWith("sudo -u pairnets /opt/pairnets/pairnets-server rescan --data-dir '/srv/my nest' --dry-run", problem);
    }

    [Theory]
    [InlineData("owner-link")]
    [InlineData("devices list")]
    [InlineData("devices remove LAPTOP")]
    [InlineData("rescan")]
    [InlineData("history purge")]
    public async Task ACommandRunAsTheWrongUserTouchesNothing(string command)
    {
        using var dir = new TempDir("cli-user");
        string[] args = [.. command.Split(' '), "--data-dir", dir.Path];
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CliCommands.RunAsync(args, output, error, _ => new DataDirUser.Users("pairnets", "root"));

        Assert.Equal(1, code);
        Assert.Contains("Run this as the pairnets user", error.ToString());
        Assert.Contains("sudo -u pairnets ", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task TheOwnerItselfGoesAhead()
    {
        using var dir = new TempDir("cli-user");
        var output = new StringWriter();
        var code = await CliCommands.RunAsync(["devices", "list", "--data-dir", dir.Path], output, new StringWriter(),
            _ => new DataDirUser.Users("pairnets", "pairnets"));
        Assert.Equal(0, code);
        Assert.Contains("No computer has its own key yet.", output.ToString());
    }

    [Fact]
    public void PermissionErrorsAreRecognisedAndExplained()
    {
        Assert.True(DataDirUser.IsAccessDenied(new UnauthorizedAccessException("Access to the path is denied.")));
        Assert.True(DataDirUser.IsAccessDenied(new SqliteException("unable to open database file", 14)));
        Assert.True(DataDirUser.IsAccessDenied(new SqliteException("attempt to write a readonly database", 8)));
        Assert.False(DataDirUser.IsAccessDenied(new SqliteException("database is locked", 5)));
        Assert.False(DataDirUser.IsAccessDenied(new IOException("The process cannot access the file.")));

        string[] args = ["owner-link"];
        // Unknown owner: the usual service user.
        Assert.Contains("sudo -u pairnets /opt/pairnets/pairnets-server owner-link", DataDirUser.AccessDenied(null, DataDir, args));
        Assert.Contains("sudo -u pairnets ", DataDirUser.AccessDenied(new DataDirUser.Users("pairnets", "someone"), DataDir, args));
        // The right user, and still no access: files left behind by a run with plain sudo.
        Assert.Contains("sudo chown -R pairnets: /var/lib/pairnets", DataDirUser.AccessDenied(new DataDirUser.Users("pairnets", "pairnets"), DataDir, args));
    }

    [Fact]
    public void TheRealCheckFindsWhoOwnsAFolder()
    {
        using var dir = new TempDir("cli-user");
        var users = DataDirUser.Probe(dir.Path);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(users); // nothing new on Windows
            return;
        }
        Assert.NotNull(users);
        Assert.Equal(users.Current, users.Owner); // this test made the folder
        Assert.Null(DataDirUser.Probe(Path.Combine(dir.Path, "not-there")));
    }
}
