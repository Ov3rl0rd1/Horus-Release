using Horus.Platforms.Windows.Tunnel;
using Xunit;

namespace Horus.Tests;

/// <summary>
/// The processes a chosen application starts take its route. The core's process rule
/// matches the executable that owns a connection, so GitHub Desktop going through the VPN
/// left the Copilot helper it starts — another executable — going direct, with the real
/// address.
/// </summary>
public class ProcessFamilyTests
{
    private const string Windows = @"C:\Windows";
    private const string Desktop = @"C:\Users\u\AppData\Local\GitHubDesktop\app-3.5.2\GitHub Desktop.exe";
    private const string Copilot = @"C:\Users\u\AppData\Local\GitHubDesktop\app-3.5.2\resources\app\copilot\copilot-language-server.exe";
    private const string Git = @"C:\Users\u\AppData\Local\GitHubDesktop\app-3.5.2\resources\app\git\cmd\git.exe";
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Browser = @"C:\Program Files\Browser\browser.exe";

    private sealed class Machine
    {
        public readonly List<ListedProcess> Listed = [];
        public readonly Dictionary<int, ProcessDetail> Details = [];

        public Machine Run(int id, int parent, string image, long startedAt)
        {
            Listed.Add(new ListedProcess(id, parent, Path.GetFileName(image.Replace('\\', '/'))));
            Details[id] = new ProcessDetail(image, startedAt);
            return this;
        }

        public IReadOnlyList<string> Descendants(params string[] chosen) =>
            ProcessFamily.DescendantImages(Listed, chosen, id => Details.TryGetValue(id, out var d) ? d : null, Windows);
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    [Fact]
    public void A_helper_started_by_a_chosen_app_is_followed_by_its_full_path()
    {
        var machine = new Machine()
            .Run(100, 4, Desktop, startedAt: 10)
            .Run(200, 100, Copilot, startedAt: 20);

        Assert.Equal([Slashed(Copilot)], machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void Grandchildren_are_followed_through_a_system_shell_that_is_not()
    {
        var machine = new Machine()
            .Run(100, 4, Desktop, 10)
            .Run(300, 100, Cmd, 30)
            .Run(301, 300, Git, 31);

        Assert.Equal([Slashed(Git)], machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void Processes_outside_the_family_are_left_alone()
    {
        var machine = new Machine()
            .Run(100, 4, Desktop, 10)
            .Run(500, 4, Browser, 15)
            .Run(501, 500, Git, 16);

        Assert.Empty(machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void A_parent_id_reused_by_a_younger_process_is_not_a_parent()
    {
        // Windows keeps a child's parent id after the parent exits, and may hand the id to a
        // newer process; a "child" older than its parent was never started by it.
        var machine = new Machine()
            .Run(100, 4, Desktop, startedAt: 50)
            .Run(200, 100, Copilot, startedAt: 20);

        Assert.Empty(machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void A_process_that_cannot_be_read_is_not_followed()
    {
        var machine = new Machine().Run(100, 4, Desktop, 10);
        machine.Listed.Add(new ListedProcess(200, 100, "protected.exe"));

        Assert.Empty(machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void Chosen_apps_are_matched_by_name_whatever_the_case_and_never_repeated()
    {
        var machine = new Machine()
            .Run(100, 4, Desktop, 10)
            .Run(101, 100, Desktop, 11)
            .Run(200, 101, Copilot, 20);

        Assert.Equal([Slashed(Copilot)], machine.Descendants("github desktop.EXE"));
        Assert.Equal([Slashed(Copilot)], machine.Descendants(Desktop));
    }

    [Fact]
    public void Nothing_is_followed_without_a_chosen_app()
    {
        var machine = new Machine().Run(100, 4, Desktop, 10).Run(200, 100, Copilot, 20);

        Assert.Empty(machine.Descendants());
    }

    [Fact]
    public void Ids_that_loop_do_not_hang_the_walk()
    {
        var machine = new Machine()
            .Run(100, 200, Desktop, 10)
            .Run(200, 100, Copilot, 10);

        Assert.Equal([Slashed(Copilot)], machine.Descendants("GitHub Desktop.exe"));
    }

    [Fact]
    public void Helpers_seen_are_kept_while_a_chosen_app_runs()
    {
        // git is started for every fetch and lives a second: remembered, it is matched from
        // its first packet rather than after the next look at the process list.
        var known = ProcessFamily.Remember([Slashed(Git)], [Slashed(Copilot)], chosenAppRunning: true);

        Assert.Equal([Slashed(Copilot), Slashed(Git)], known);
    }

    [Fact]
    public void Helpers_are_forgotten_once_no_chosen_app_runs()
    {
        var known = ProcessFamily.Remember([Slashed(Git)], [], chosenAppRunning: false);

        Assert.Empty(known);
    }

    [Fact]
    public void Whether_a_chosen_app_runs_is_read_from_the_list()
    {
        var machine = new Machine().Run(100, 4, Desktop, 10);

        Assert.True(ProcessFamily.AnyRunning(machine.Listed, ["GitHub Desktop.exe"]));
        Assert.False(ProcessFamily.AnyRunning(machine.Listed, ["browser.exe"]));
    }
}
