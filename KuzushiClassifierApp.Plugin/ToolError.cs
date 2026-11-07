using System.Text.Json.Nodes;

namespace KuzushiClassifierApp.Plugin;

public static class ToolErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string NotFound = "NOT_FOUND";
    public const string Unavailable = "UNAVAILABLE";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string UnsupportedOperation = "UNSUPPORTED_OPERATION";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// Error carried through a tools/call result: isError=true and
/// structuredContent = { "error": { "code": &lt;code&gt;, "message": &lt;message&gt; } }.
/// </summary>
public sealed class ToolError
{
    public ToolError(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }

    public JsonObject ToStructuredContent()
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = Code,
                ["message"] = Message,
            },
        };
    }
}

/// <summary>
/// An expected, user/agent-visible failure. Unhandled exceptions are mapped
/// separately by the dispatcher so diagnostics stay precise.
/// </summary>
public sealed class ToolErrorException : Exception
{
    public ToolErrorException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ToolErrorException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    public ToolError ToError() => new(Code, Message);
}
