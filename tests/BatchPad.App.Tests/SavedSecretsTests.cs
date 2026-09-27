using System.ComponentModel;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class SavedSecretsTests
{
    [TestMethod]
    public void ARememberedSecretIsStoredUnderTheWorkspaceTarget()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask, secrets) = Open(test);
        main.Select("Workspace/Login");

        ask.Answer = form =>
        {
            var field = (SecretFieldViewModel)form.Field("token")!;
            Assert.IsTrue(field.CanRemember);
            Assert.IsFalse(field.Remember);
            field.Text = "s3cret";
            field.Remember = true;
        };
        main.Details.RunCommand.Execute(null);

        Assert.HasCount(1, launcher.Requests);
        Assert.AreEqual("s3cret", SecretScope.Of(main.Workspace!).Get(secrets, "token"));
    }

    [TestMethod]
    public void ASecretThatCannotBeSavedIsReportedAfterTheRunStarts()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask, secrets) = Open(test);
        secrets.Failure = new Win32Exception(87, "Could not save 'token': too long.");
        main.Select("Workspace/Login");

        ask.Answer = form =>
        {
            var field = (SecretFieldViewModel)form.Field("token")!;
            field.Text = "s3cret";
            field.Remember = true;
        };
        main.Details.RunCommand.Execute(null);

        Assert.HasCount(1, launcher.Requests);
        Assert.AreEqual("Could not save 'token': too long.", main.Details.RunError);
    }

    [TestMethod]
    public void AnUntickedSecretIsNotStored()
    {
        using var test = new TestWorkspace();
        var (main, _, ask, secrets) = Open(test);
        main.Select("Workspace/Login");

        ask.Answer = form => ((SecretFieldViewModel)form.Field("token")!).Text = "s3cret";
        main.Details.RunCommand.Execute(null);

        Assert.IsEmpty(secrets.Entries);
    }

    [TestMethod]
    public void TheFormOffersRememberOnlyWhenAsking()
    {
        using var test = new TestWorkspace();
        var (main, _, _, _) = Open(test);
        main.Select("Workspace/Login");

        Assert.IsFalse(((SecretFieldViewModel)main.Details.Form!.Field("token")!).CanRemember);
    }

    [TestMethod]
    public void SettingsListsSavedNamesAndRemovesThem()
    {
        using var test = new TestWorkspace();
        var (main, _, _, secrets) = Open(test);
        var scope = SecretScope.Of(main.Workspace!);
        scope.Set(secrets, "token", "hunter2");
        SecretScope.Global.Set(secrets, "api", "hunter3");
        secrets.Set("BatchPad:ws:other:token", "hunter4", scope.Root);
        secrets.Set(scope.Target("elsewhere"), "hunter5", @"C:notherolder");

        main.OpenSettingsCommand.Execute(null);
        var saved = main.SettingsPage!.SavedSecrets;

        CollectionAssert.AreEqual(new[] { "token", "api" }, saved.Secrets.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "This workspace", "Global" }, saved.Secrets.Select(s => s.Scope).ToArray());
        Assert.IsTrue(saved.HasSecrets);

        saved.Secrets[0].RemoveCommand.Execute(null);

        Assert.AreEqual("api", saved.Secrets.Single().Name);
        Assert.IsNull(scope.Get(secrets, "token"));
        Assert.HasCount(3, secrets.Entries);
    }

    private static (MainViewModel, FakeLauncher, FakeAsk, FakeSecretStore) Open(TestWorkspace test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ws")).FullName;
        File.WriteAllText(Path.Combine(directory, "login.bat"), "@echo %1\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "secrets-test", "scripts": [
              { "id": "login", "name": "Login", "path": "login.bat", "params": [ { "name": "token", "type": "secret" } ] } ] }
            """);
        var launcher = new FakeLauncher();
        var ask = new FakeAsk();
        var secrets = new FakeSecretStore();
        var main = test.OpenMain(directory, trusted: true, launcher: launcher, shell: new FakeShell(), ask: ask, secrets: secrets);
        return (main, launcher, ask, secrets);
    }
}
