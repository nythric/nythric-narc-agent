namespace NarcAgent.Command.Models
{
    public enum CommandResultKind
    {
        Ack,
        Content,
        Async,
        Error,
        PluginError
    }

    public record CommandResult(
        bool Success,
        string? Message,
        string? Payload,
        CommandResultKind Kind
    )
    {
        public static CommandResult Ack(string? message = null) =>
            new(true, message, null, CommandResultKind.Ack);

        public static CommandResult Content(string payload, string? message = null) =>
            new(true, message, payload, CommandResultKind.Content);

        public static CommandResult Async(string? message = null) =>
            new(true, message, null, CommandResultKind.Async);

        public static CommandResult Error(string message) =>
            new(false, message, null, CommandResultKind.Error);

        public static CommandResult PluginError(string action, string error, string? details = null) =>
            new(false, $"Plugin execution error on '{action}': {error}", details, CommandResultKind.PluginError);
    }
}
