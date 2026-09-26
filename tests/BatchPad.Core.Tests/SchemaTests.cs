using System.Text.Json;
using BatchPad.Core.Model;
using Json.Schema;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class SchemaTests
{
    private static readonly string SchemaText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "batchpad.schema.json"));
    private static readonly BuildOptions Options = new() { SchemaRegistry = new SchemaRegistry() };
    private static readonly JsonSchema WorkspaceSchema = JsonSchema.FromText(SchemaText, Options);
    private static readonly JsonSchema UserSchema = Definition("userFile");
    private static readonly JsonSchema GlobalSchema = Definition("globalFile");

    [TestMethod]
    public void TheSchemaIdIsTheUrlNewWorkspacesPointTo()
    {
        using var schema = JsonDocument.Parse(SchemaText);
        Assert.AreEqual(WorkspaceFile.SchemaUrl, schema.RootElement.GetProperty("$id").GetString());
    }

    [TestMethod]
    public void EverySampleAndFixtureConfigMatches()
    {
        var files = Directory.EnumerateFiles(Fixtures.Path(), "batchpad.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Fixtures.Path("config"), "*.json"))
            .Append(Path.Combine(Fixtures.DemoWorkspace, "batchpad.json"))
            .Where(f => !Path.GetFileName(f).StartsWith("invalid_", StringComparison.Ordinal))
            .ToList();
        Assert.IsGreaterThanOrEqualTo(6, files.Count);

        var problems = files
            .Select(f => (File: f, Errors: Errors(Path.GetFileName(f).StartsWith("user_") ? UserSchema : WorkspaceSchema, f)))
            .Where(r => r.Errors.Count > 0)
            .Select(r => $"{Path.GetFileName(r.File)}: {string.Join("; ", r.Errors)}")
            .ToList();
        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void AMisspelledTriggerKindFails() =>
        Assert.IsNotEmpty(Errors(UserSchema, Fixtures.Path("config", "invalid_trigger.json")));

    [TestMethod]
    public void ANestedScriptWithAnUnknownRunnerFails() =>
        Assert.IsNotEmpty(Errors(WorkspaceSchema, """{ "scripts": [ { "items": [ { "path": "a.sh", "runner": "bash" } ] } ] }"""));

    [TestMethod]
    public void ALockScopeIsCheckoutOrMachine()
    {
        Assert.IsEmpty(Errors(WorkspaceSchema, """{ "scripts": [ { "path": "a.bat", "lock": "port", "lockScope": "machine" } ] }"""));
        Assert.IsEmpty(Errors(WorkspaceSchema, """{ "scripts": [ { "id": "w", "lockScope": "checkout", "steps": [ { "run": "a" } ] } ] }"""));
        Assert.IsNotEmpty(Errors(WorkspaceSchema, """{ "scripts": [ { "path": "a.bat", "lockScope": "global" } ] }"""));
    }

    [TestMethod]
    public void SchedulesAreRejectedInAWorkspaceFile() =>
        Assert.IsNotEmpty(Errors(WorkspaceSchema, Fixtures.Path("config", "user_schedules.json")));

    [TestMethod]
    public void AGlobalScheduleForAWorkspaceTargetNamesTheWorkspace()
    {
        const string schedule = """{ "target": "workspace:tests", "trigger": { "onStart": true } }""";
        Assert.IsNotEmpty(Errors(GlobalSchema, $$"""{ "schedules": [ {{schedule}} ] }"""));
        Assert.IsEmpty(Errors(GlobalSchema, $$"""{ "schedules": [ {{schedule[..^1]}}, "workspace": "C:/src/game" } ] }"""));
    }

    private static JsonSchema Definition(string name) =>
        JsonSchema.FromText($$"""{ "$id": "https://batchpad.test/{{name}}", "$ref": "{{WorkspaceFile.SchemaUrl}}#/$defs/{{name}}" }""", Options);

    private static List<string> Errors(JsonSchema schema, string pathOrJson)
    {
        var text = File.Exists(pathOrJson) ? File.ReadAllText(pathOrJson) : pathOrJson;
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return results.IsValid
            ? []
            : (results.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}"))
                .DefaultIfEmpty("invalid")
                .ToList();
    }
}
