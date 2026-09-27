using System.Xml.Linq;
using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workspace;
using static BatchPad.Core.Tests.CronTests;
using static BatchPad.Core.Tests.SchedulerTests;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TaskSchedulerTests
{
    private static readonly TaskHost Host = new(@"C:\Program Files\BatchPad\BatchPad.exe", TaskHost.DefaultFolder);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [TestMethod]
    public void ACronScheduleRegistersItsNextFireAndRunsTheScheduleInItsWorkspace()
    {
        using var h = new Harness("""{ "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" }""");

        var xml = Xml(h, @"C:\My Work\batchpad.json");

        Assert.AreEqual($$"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>BatchPad</Author>
                <Description>Runs the BatchPad schedule 'nightly' for build in C:\My Work\batchpad.json. Managed by BatchPad: change it on the Schedules page.</Description>
              </RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <StartBoundary>2026-09-25T02:00:00+02:00</StartBoundary>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>C:\Program Files\BatchPad\BatchPad.exe</Command>
                  <Arguments>run --schedule sched:nightly --workspace "C:\My Work\batchpad.json" --due 2026-09-25T02:00:00+02:00</Arguments>
                  <WorkingDirectory>C:\My Work</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """.ReplaceLineEndings("\n"), xml.ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void AnEveryScheduleWithAWindowStartsInsideItAndLeavesOverlapsToTheRun()
    {
        using var h = new Harness("""
            { "id": "day", "target": "workspace:build", "trigger": { "every": "30m", "between": "09:00-18:00" },
              "overlap": "queue", "missed": "runOnce", "runIn": "windows" }
            """);

        var task = XElement.Parse(Xml(h, h.Temp.Path("batchpad.json")));

        Assert.AreEqual("2026-09-25T09:00:00+02:00", task.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.AreEqual("Parallel", task.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.AreEqual("true", task.Descendants(Ns + "StartWhenAvailable").Single().Value);
    }

    [TestMethod]
    public void AnAtScheduleRegistersItsTimeInThatDaysOffsetAndPassesADataFolder()
    {
        using var h = new Harness("""{ "id": "xmas", "target": "workspace:build", "trigger": { "at": "2026-12-24T18:00" }, "overlap": "parallel", "runIn": "windows" }""");
        var host = Host with { DataDirectory = @"D:\data dir" };
        var entry = ScheduleEntry.For(h.Workspace).Single();

        var task = XElement.Parse(TaskSchedulerXml.For(host, entry, @"C:\w\batchpad.json", Next(h, entry), Berlin));

        Assert.AreEqual("2026-12-24T18:00:00+01:00", task.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.AreEqual("Parallel", task.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.AreEqual("""--data-dir "D:\data dir" run --schedule sched:xmas --workspace C:\w\batchpad.json --due 2026-12-24T18:00:00+01:00""",
            task.Descendants(Ns + "Arguments").Single().Value);
    }

    [TestMethod]
    public void EnablingRegistersAndDisablingOrDeletingRemoves()
    {
        using var h = new Harness(Confirmed(""" "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" """));
        var (sync, registrar) = Sync(h);
        var workspace = h.Workspace;
        var name = Host.TaskName(workspace.Id, ScheduleEntry.For(workspace).Single());

        sync.Sync(workspace, ScheduleEntry.For(workspace), trusted: true);
        StringAssert.StartsWith(name, @"\BatchPad\sched\nightly-");
        StringAssert.Contains(registrar.Registered[name], "2026-09-25T02:00:00+02:00");
        var status = sync.StatusOf("sched:nightly")!;
        Assert.AreEqual(WindowsTaskState.Registered, status.State);
        Assert.AreEqual(Local(2026, 9, 25, 2, 0), status.NextRun);

        var disabled = ScheduleEntry.For(workspace).Single();
        disabled.Schedule.Enabled = false;
        sync.Sync(workspace, [disabled], trusted: true);
        Assert.IsEmpty(registrar.Registered);
        Assert.AreEqual("Disabled", sync.StatusOf("sched:nightly")!.Reason);

        disabled.Schedule.Enabled = true;
        sync.Sync(workspace, ScheduleEntry.For(workspace), trusted: true);
        sync.Sync(workspace, [], trusted: true);
        Assert.IsEmpty(registrar.Registered);

        sync.Sync(workspace, ScheduleEntry.For(workspace), trusted: true);
        var inApp = ScheduleEntry.For(workspace).Single();
        inApp.Schedule.RunIn = RunIn.App;
        sync.Sync(workspace, [inApp], trusted: true);
        Assert.IsEmpty(registrar.Registered);
    }

    [TestMethod]
    public void AnUnchangedTaskIsNotWrittenAgainButAMissingOneIs()
    {
        using var h = new Harness(Confirmed(""" "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" """));
        var (sync, registrar) = Sync(h);
        var writes = 0;
        var counting = new CountingRegistrar(registrar, () => writes++);
        sync = new TaskSchedulerSync(counting, Host, h.Time);

        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);
        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);
        Assert.AreEqual(1, writes);

        registrar.Registered.Clear();
        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);
        Assert.AreEqual(2, writes);
    }

    [TestMethod]
    public void AnUnconfirmedScheduleOrUntrustedWorkspaceRegistersNothing()
    {
        using var h = new Harness("""{ "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" }""");
        var (sync, registrar) = Sync(h);

        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);
        Assert.IsEmpty(registrar.Registered);
        Assert.AreEqual(TaskSchedulerSync.WaitingForConfirm, sync.StatusOf("sched:nightly")!.Reason);

        var confirmed = ScheduleEntry.For(h.Workspace).Single();
        confirmed.Schedule.DefinitionHash = DefinitionHash.Of(confirmed.Target!);
        sync.Sync(h.Workspace, [confirmed], trusted: false);
        Assert.IsEmpty(registrar.Registered);
        Assert.AreEqual(WindowsTaskState.NotRegistered, sync.StatusOf("sched:nightly")!.State);
    }

    [TestMethod]
    public void OnlyTimeTriggersCanRunInWindows()
    {
        using var h = new Harness("""{ "id": "watch", "target": "workspace:build", "trigger": { "fileChanged": "*.cs" }, "runIn": "windows" }""");
        var (sync, registrar) = Sync(h);
        var entry = ScheduleEntry.For(h.Workspace).Single();

        sync.Sync(h.Workspace, [entry], trusted: true);

        Assert.AreEqual(ScheduleEntry.OnlyTimeTriggersInWindows, entry.Problem);
        Assert.IsEmpty(registrar.Registered);
    }

    [TestMethod]
    public void TheAppsSchedulerLeavesWindowsSchedulesToTaskScheduler()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" }, "runIn": "windows" }""");
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        h.Time.Advance(TimeSpan.FromHours(2));

        Assert.IsEmpty(h.Launcher.Runs);
        Assert.IsNull(h.Scheduler.Statuses().Single().NextFire);
    }

    [TestMethod]
    public void SchtasksCreatesFindsAndDeletesATask()
    {
        if (Environment.GetEnvironmentVariable("BATCHPAD_SYSTEM_TESTS") != "1")
            Assert.Inconclusive("Writes to the user's Task Scheduler; set BATCHPAD_SYSTEM_TESTS=1 to run it.");
        using var h = new Harness("""{ "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" }""");
        var registrar = new SchtasksRegistrar();
        const string Folder = @"\BatchPad-test\";
        var name = Folder + Guid.NewGuid().ToString("N");
        var xml = TaskSchedulerXml.For(new TaskHost(@"C:\Windows\System32\cmd.exe", Folder), ScheduleEntry.For(h.Workspace).Single(),
            h.Temp.Path("batchpad.json"), DateTimeOffset.Now.AddYears(1), TimeZoneInfo.Local);
        try
        {
            registrar.Register(name, xml);
            CollectionAssert.Contains(registrar.Tasks(Folder).Select(t => t.Name).ToList(), name);
        }
        finally
        {
            registrar.Delete(name);
        }
        CollectionAssert.DoesNotContain(registrar.Tasks(Folder).Select(t => t.Name).ToList(), name);
    }

    [TestMethod]
    public void TheFirstSyncRemovesOrphanTasksEvenWithNoWindowsSchedules()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" } }""");
        var (sync, registrar) = Sync(h);
        registrar.Registered[@"\BatchPad\sched\gone"] = "<Task/>";

        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);

        Assert.IsEmpty(registrar.Registered);
    }

    [TestMethod]
    public void EntriesWhoseIdsCollideGetTheirOwnTasks()
    {
        using var h = new Harness("""{ "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" }""");
        var schedule = ScheduleEntry.For(h.Workspace).Single().Schedule;
        string[] keys = ["sched:x", "global:x", "sched:My Job", "sched:My_Job", "sched:Backup", "sched:backup"];

        var names = keys.Select(key => Host.TaskName("sched", new ScheduleEntry(key, schedule))).ToList();

        Assert.HasCount(keys.Length, names.Distinct(StringComparer.OrdinalIgnoreCase));
        var global = XElement.Parse(TaskSchedulerXml.For(Host, new ScheduleEntry("global:x", schedule), @"C:\w\batchpad.json", Next(h, ScheduleEntry.For(h.Workspace).Single()), Berlin));
        StringAssert.StartsWith(global.Descendants(Ns + "Arguments").Single().Value, "run --schedule global:x ");
    }

    [TestMethod]
    public void ATaskRegisteredByAnotherWorkspaceFileWithTheSameIdIsLeftAlone()
    {
        using var h = new Harness(Confirmed(""" "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" """));
        var (sync, registrar) = Sync(h);
        var entry = ScheduleEntry.For(h.Workspace).Single();
        var name = Host.TaskName(h.Workspace.Id, entry);
        const string Other = @"C:\other checkout\batchpad.json";
        var theirs = TaskSchedulerXml.For(Host, entry, Other, Next(h, entry), Berlin);
        registrar.Registered[name] = theirs;
        registrar.Registered[@"\BatchPad\sched\theirs"] = theirs;
        registrar.Registered[@"\BatchPad\sched\mine"] = TaskSchedulerXml.For(Host, entry, h.Workspace.FilePath, Next(h, entry), Berlin);

        sync.Sync(h.Workspace, [entry], trusted: true);

        Assert.AreEqual(theirs, registrar.Registered[name]);
        Assert.IsTrue(registrar.Registered.ContainsKey(@"\BatchPad\sched\theirs"));
        Assert.IsFalse(registrar.Registered.ContainsKey(@"\BatchPad\sched\mine"));
        Assert.AreEqual(new WindowsTaskStatus(name, WindowsTaskState.OtherWorkspace, Reason: Other) { At = h.Time.GetUtcNow() }, sync.StatusOf(entry.Key));

        sync.Sync(h.Workspace, [], trusted: true);
        Assert.AreEqual(theirs, registrar.Registered[name]);
    }

    [TestMethod]
    public void TheWorkspaceATaskRunsIsReadFromItsArguments()
    {
        const string Mine = @"C:\My Work\Päivä\batchpad.json";

        Assert.IsNull(TaskSchedulerXml.OtherWorkspace(null, Mine));
        Assert.IsNull(TaskSchedulerXml.OtherWorkspace("run --schedule x", Mine));
        Assert.IsNull(TaskSchedulerXml.OtherWorkspace("""run --schedule x --workspace "c:\my work\PÄIVÄ\batchpad.json" --due 2026""", Mine));
        Assert.IsNull(TaskSchedulerXml.OtherWorkspace("""--workspace "C:\My Work\P?iv?\batchpad.json" """, Mine));
        Assert.IsNull(TaskSchedulerXml.OtherWorkspace("--workspace \"C:\\My Work\\P\uFFFDiv\uFFFD\\batchpad.json\"", Mine));
        Assert.AreEqual(@"C:\My Work\Paiva\batchpad.json", TaskSchedulerXml.OtherWorkspace("""--workspace "C:\My Work\Paiva\batchpad.json" """, Mine));
        Assert.AreEqual(@"D:\x\batchpad.json", TaskSchedulerXml.OtherWorkspace(@"run --workspace D:\x\batchpad.json", Mine));
    }

    [TestMethod]
    public void SchtasksQueryOutputIsReadIntoTasksOfTheFolder()
    {
        const string Output = """

            <Tasks>


            <!-- \BatchPad\sched\nightly-0123abcd -->
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>C:\BatchPad\BatchPad.exe</Command>
                  <Arguments>run --schedule sched:nightly --workspace "C:\My Work\batchpad.json"</Arguments>
                </Exec>
              </Actions>
            </Task>

            <!-- \BatchPad\sched\broken -->
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">

            <!-- \BatchPad\sched\sub\deeper -->
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task" />
            </Tasks>
            """;

        var tasks = SchtasksRegistrar.ParseQuery(Output, @"\BatchPad\sched\");

        CollectionAssert.AreEqual(new[]
        {
            new RegisteredTask(@"\BatchPad\sched\nightly-0123abcd", """run --schedule sched:nightly --workspace "C:\My Work\batchpad.json" """.TrimEnd()),
            new RegisteredTask(@"\BatchPad\sched\broken", null),
        }, tasks.ToList());
        Assert.IsEmpty(SchtasksRegistrar.ParseQuery("", @"\BatchPad\sched\"));
    }

    [TestMethod]
    public void APercentSignInTheArgumentsKeepsAScheduleOutOfWindows()
    {
        using var h = new Harness("""{ "id": "50%", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" }""");
        var (sync, registrar) = Sync(h);

        sync.Sync(h.Workspace, ScheduleEntry.For(h.Workspace), trusted: true);

        Assert.AreEqual(ScheduleEntry.PercentInWindows, ScheduleEntry.For(h.Workspace).Single().Problem);
        Assert.IsEmpty(registrar.Registered);

        using var fine = new Harness(Confirmed(""" "id": "nightly", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "runIn": "windows" """));
        var percentData = new TaskSchedulerSync(registrar, Host with { DataDirectory = @"C:\%TEMP%\data" }, fine.Time);
        percentData.Sync(fine.Workspace, ScheduleEntry.For(fine.Workspace), trusted: true);

        Assert.AreEqual(ScheduleEntry.PercentInWindows, percentData.StatusOf("sched:nightly")!.Reason);
        Assert.IsEmpty(registrar.Registered);
    }

    [TestMethod]
    public void AFailureFromATaskRunShowsUntilTheNextSync()
    {
        var at = new DateTimeOffset(2026, 9, 25, 2, 0, 0, TimeSpan.Zero);
        var synced = new WindowsTaskStatus("t", WindowsTaskState.Registered, at) { At = at };
        var failure = new WindowsTaskFailure("Access is denied.", at.AddMinutes(1));

        Assert.AreSame(synced, WindowsTaskStatus.Latest(synced, null));
        Assert.AreEqual(new WindowsTaskStatus("t", WindowsTaskState.Failed, Reason: "Access is denied.") { At = failure.At },
            WindowsTaskStatus.Latest(synced, failure));
        var later = synced with { At = at.AddMinutes(2) };
        Assert.AreSame(later, WindowsTaskStatus.Latest(later, failure));
        Assert.AreEqual(WindowsTaskState.Failed, WindowsTaskStatus.Latest(null, failure)!.State);
    }

    private static string Xml(Harness h, string workspaceFile)
    {
        var entry = ScheduleEntry.For(h.Workspace).Single();
        return TaskSchedulerXml.For(Host, entry, workspaceFile, Next(h, entry), Berlin);
    }

    private static DateTimeOffset Next(Harness h, ScheduleEntry entry) => TriggerMath.NextFire(entry.Schedule.Trigger, h.Time.GetUtcNow(), Berlin)!.Value;

    private static (TaskSchedulerSync Sync, FakeTaskRegistrar Registrar) Sync(Harness h)
    {
        var registrar = new FakeTaskRegistrar();
        return (new TaskSchedulerSync(registrar, Host, h.Time), registrar);
    }

    /// <summary>A schedule whose <c>definitionHash</c> matches the harness's <c>build</c> script.</summary>
    private static string Confirmed(string fields)
    {
        using var probe = new Harness($"{{ {fields} }}");
        var hash = DefinitionHash.Of(ScheduleEntry.For(probe.Workspace).Single().Target!);
        return $$"""{ {{fields}}, "definitionHash": "{{hash}}" }""";
    }

    private sealed class CountingRegistrar(ITaskRegistrar inner, Action onRegister) : ITaskRegistrar
    {
        public void Register(string taskName, string xml)
        {
            onRegister();
            inner.Register(taskName, xml);
        }

        public void Delete(string taskName) => inner.Delete(taskName);
        public IReadOnlyList<RegisteredTask> Tasks(string folder) => inner.Tasks(folder);
    }
}
