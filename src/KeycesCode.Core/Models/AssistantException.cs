namespace KeycesCode.Core.Models;

public sealed class AssistantException : Exception
{
    public AssistantException(string message) : base(message) { }
}
