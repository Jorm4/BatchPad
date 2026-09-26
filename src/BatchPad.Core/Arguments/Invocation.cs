namespace BatchPad.Core.Arguments;

/// <summary>One run of a script: its raw, unquoted arguments after the program, and environment additions.</summary>
public sealed record Invocation(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

public sealed class ArgumentAssemblyException(string message) : Exception(message);
