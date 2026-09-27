using System.ComponentModel;
using System.Runtime.Versioning;
using BatchPad.Core.Config;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class SecretStoreTests
{
    private const string Scripts = """
        { "id": "login", "path": "echo.bat", "params": [ { "name": "token", "type": "secret" } ] }
        """;

    private const string LoginScript = """{ "id": "login", "command": "echo", "params": [ { "name": "token", "type": "secret" } ] }""";

    [TestMethod]
    public async Task AnUnattendedRunGetsItsSecretFromTheStoreAndKeepsItMasked()
    {
        using var workspace = Workspace();
        var secrets = new FakeSecretStore();
        SecretScope.Of(workspace.Workspace).Set(secrets, "token", "hunter2");
        var store = new HistoryStore(workspace.Temp.Path("history"));
        var request = SecretFill.Apply(workspace.Request("login") with { Unattended = true }, secrets);

        using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
        var record = await HistoryRecorder.Attach(run, store, request, "Workspace:id:login").WaitAsync(Limit);

        Assert.AreEqual(0, record.ExitCode);
        Assert.IsTrue(run.Output.Any(l => l.Text.Contains("hunter2")));
        Assert.AreEqual(RunRecord.Masked, record.Values["token"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", record.Command);
        StringAssert.Contains(record.Command, RunRecord.Masked);
        var log = File.ReadAllText(store.LogPath(record));
        StringAssert.Contains(log, RunRecord.Masked);
        Assert.DoesNotContain("hunter2", log);
    }

    [TestMethod]
    public void AMissingEntryStillNeedsAValue()
    {
        using var workspace = Workspace();

        var request = SecretFill.Apply(workspace.Request("login") with { Unattended = true }, new FakeSecretStore());

        StringAssert.Contains(Assert.Throws<RunException>(() => RunPlanner.CheckUnattended(request)).Message, "needs a value for token");
    }

    [TestMethod]
    public void AGivenValueWinsOverTheStoreAndARunSomeoneStartsIsNotFilled()
    {
        using var workspace = Workspace();
        var secrets = new FakeSecretStore();
        SecretScope.Of(workspace.Workspace).Set(secrets, "token", "stored");

        var given = SecretFill.Apply(workspace.Request("login", new() { ["token"] = "given" }) with { Unattended = true }, secrets);
        var attended = SecretFill.Apply(workspace.Request("login"), secrets);

        Assert.AreEqual("given", given.Values!["token"]!.GetValue<string>());
        Assert.IsNull(attended.Values);
    }

    [TestMethod]
    public void ASharedParameterIsStoredUnderTheSharedName()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), """
            { "id": "ws", "sharedParams": { "apiKey": { "type": "secret" } },
              "scripts": [ { "id": "shared", "command": "echo", "params": [ { "use": "apiKey", "name": "key" } ] } ] }
            """);
        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data")));
        var secrets = new FakeSecretStore();
        SecretScope.Of(loaded).Set(secrets, "apiKey", "k-1");

        var request = SecretFill.Apply(new RunRequest(loaded, loaded.Workspace,
            (ScriptNode)loaded.References.Resolve("shared", TreeKind.Workspace)!) { Unattended = true }, secrets);

        Assert.AreEqual("k-1", request.Values!["key"]!.GetValue<string>());
    }

    [TestMethod]
    public void AGlobalScriptReadsTheGlobalTarget()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path("data"));
        ConfigWriter.Write(paths.GlobalFile, new WorkspaceFile
        {
            Scripts = [new ScriptNode { Id = "publish", Command = "echo", Params = [new ParameterDefinition { Name = "token", Type = ParameterType.Secret }] }],
        });
        File.WriteAllText(dir.Path("batchpad.json"), """{ "id": "ws", "scripts": [] }""");
        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), paths);
        var secrets = new FakeSecretStore();
        SecretScope.Of(loaded).Set(secrets, "token", "workspace");
        SecretScope.Global.Set(secrets, "token", "global");

        var request = SecretFill.Apply(new RunRequest(loaded, loaded.Global,
            (ScriptNode)loaded.References.Resolve("publish", TreeKind.Global)!) { Unattended = true }, secrets);

        Assert.AreEqual("BatchPad:global:token", SecretScope.Global.Target("token"));
        Assert.AreEqual("global", request.Values!["token"]!.GetValue<string>());
    }

    [TestMethod]
    public void AWorkspaceWithTheIdGlobalGetsNoGlobalSecrets()
    {
        using var dir = new TempDir();
        var loaded = Load(dir, "ws", "global");
        var secrets = new FakeSecretStore();
        SecretScope.Global.Set(secrets, "token", "global");

        Assert.IsNull(Filled(loaded, secrets));
        Assert.AreEqual("BatchPad:ws:global:token", SecretScope.Of(loaded).Target("token"));
    }

    [TestMethod]
    public void TwoFoldersWithTheSameIdDoNotSeeEachOthersSecrets()
    {
        using var dir = new TempDir();
        var mine = Load(dir, "mine", "shared-id");
        var copy = Load(dir, "copy", "shared-id");
        var secrets = new FakeSecretStore();
        SecretScope.Of(mine).Set(secrets, "token", "hunter2");

        Assert.AreEqual("hunter2", Filled(mine, secrets));
        Assert.IsNull(Filled(copy, secrets));
        Assert.IsEmpty(SecretScope.Of(copy).Names(secrets));
        Assert.IsFalse(SecretScope.Of(copy).Remove(secrets, "token"));
        Assert.AreEqual("hunter2", Filled(mine, secrets));
    }

    [TestMethod]
    public void AWorktreeOfTheSameRepositorySharesItsSecrets()
    {
        using var repo = new GitRepo(new Dictionary<string, string>
        {
            ["batchpad.json"] = $$"""{ "id": "repo-secrets", "scripts": [ {{LoginScript}} ] }""",
        });
        var paths = new AppPaths(Path.Combine(repo.Root, "data"));
        var main = WorkspaceLoader.Load(Path.Combine(repo.Main, "batchpad.json"), paths);
        var worktree = WorkspaceLoader.Load(Path.Combine(repo.Worktree, "batchpad.json"), paths);
        var secrets = new FakeSecretStore();
        SecretScope.Of(main).Set(secrets, "token", "hunter2");

        Assert.AreEqual("hunter2", Filled(worktree, secrets));
        CollectionAssert.AreEqual(new[] { "token" }, SecretScope.Of(worktree).Names(secrets).ToArray());
    }

    [TestMethod]
    public async Task AScheduledScriptsPrerequisiteGetsItsSecret()
    {
        using var workspace = new RunWorkspace("""
            { "id": "login", "path": "check.bat", "params": [ { "name": "token", "type": "secret" } ] },
            { "id": "build", "path": "echo.bat", "dependsOn": ["login"] }
            """);
        File.WriteAllText(workspace.Temp.Path("check.bat"), "@if \"%~1\"==\"hunter2\" (exit /b 0) else (exit /b 5)\r\n");
        File.WriteAllText(workspace.Temp.Path("echo.bat"), "@echo %*\r\n");
        var secrets = new FakeSecretStore();
        SecretScope.Of(workspace.Workspace).Set(secrets, "token", "hunter2");
        var history = new HistoryStore(workspace.Temp.Path("history"));
        using var scheduler = new Scheduler(history,
            new GatedScheduleLauncher(workspace.Workspace, workspace.Gate, RunWorkspace.Interpreters, secrets),
            new ScheduleStateStore(workspace.Temp.Path("schedules.json")), new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            Secrets = secrets,
        };
        ScheduleFire? fire = null;
        scheduler.ScheduleFired += f => fire = f;
        var schedule = new Schedule { Id = "build", Target = "workspace:build", Trigger = new Trigger { Every = "1h" } };
        scheduler.Start([ScheduleEntry.Create("build", schedule, workspace.Workspace.Workspace, workspace.Workspace)]);

        Assert.IsTrue(scheduler.RunNow("build"));
        var record = await fire!.Recorded.WaitAsync(Limit);

        Assert.AreEqual(0, record.ExitCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void CredentialManagerRoundTrip()
    {
        var store = new CredentialManagerSecretStore();
        var prefix = $"BatchPad:test-{Guid.NewGuid():N}:";
        var target = prefix + "token";
        try
        {
            Assert.IsNull(store.Get(target));
            store.Set(target, "s3cret ✓", @"C:\repo");

            Assert.AreEqual(("s3cret ✓", @"C:\repo"), store.Get(target));
            CollectionAssert.AreEqual(new[] { (target, (string?)@"C:\repo") }, store.List(prefix).ToArray());
            Assert.IsTrue(store.Remove(target));
            Assert.IsNull(store.Get(target));
            Assert.IsFalse(store.Remove(target));
            Assert.IsEmpty(store.List(prefix));
        }
        finally
        {
            store.Remove(target);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ATooLongValueIsRefusedWithItsLimit()
    {
        var store = new CredentialManagerSecretStore();

        var error = Assert.Throws<Win32Exception>(() => store.Set($"BatchPad:test-{Guid.NewGuid():N}:token", new string('x', 1281), null));

        StringAssert.Contains(error.Message, "at most 1280 characters");
    }

    private static string? Filled(LoadedWorkspace loaded, ISecretStore secrets) =>
        SecretFill.Apply(new RunRequest(loaded, loaded.Workspace, (ScriptNode)loaded.References.Resolve("login", TreeKind.Workspace)!)
        {
            Unattended = true,
        }, secrets).Values?["token"]?.GetValue<string>();

    private static LoadedWorkspace Load(TempDir dir, string folder, string id)
    {
        Directory.CreateDirectory(dir.Path(folder));
        File.WriteAllText(dir.Path(folder, "batchpad.json"), $$"""{ "id": "{{id}}", "scripts": [ {{LoginScript}} ] }""");
        return WorkspaceLoader.Load(dir.Path(folder, "batchpad.json"), new AppPaths(dir.Path("data")));
    }

    private static RunWorkspace Workspace()
    {
        var workspace = new RunWorkspace(Scripts);
        File.WriteAllText(workspace.Temp.Path("echo.bat"), "@echo off\r\necho %*\r\n");
        return workspace;
    }
}
