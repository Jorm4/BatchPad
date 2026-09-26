namespace BatchPad.Core.Arguments;

/// <summary>One run of a script: its raw, unquoted arguments after the program, and environment additions.</summary>
/// <param name="Display">The command for the preview, e.g. <c>build.bat --release Alpha</c>.</param>
public sealed record Invocation(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string Display);

public sealed class ArgumentAssemblyException(string message) : Exception(message);
