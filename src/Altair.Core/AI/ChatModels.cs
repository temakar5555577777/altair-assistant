namespace Altair.Core.AI;

public enum Role { System, User, Assistant }

public sealed class ChatMessage
{
    public Role Role { get; set; }
    public string Text { get; set; } = "";
    /// <summary>PNG/JPEG-картинки (для зрения агента: скриншот экрана).</summary>
    public List<ImagePart> Images { get; set; } = new();

    public ChatMessage() { }
    public ChatMessage(Role role, string text) { Role = role; Text = text; }

    public static ChatMessage System(string t) => new(Role.System, t);
    public static ChatMessage User(string t) => new(Role.User, t);
    public static ChatMessage Assistant(string t) => new(Role.Assistant, t);
}

public sealed record ImagePart(byte[] Data, string MimeType = "image/png")
{
    public string Base64 => Convert.ToBase64String(Data);
}

public sealed record ChatOptions(double Temperature = 0.5, int MaxTokens = 1500, bool Json = false, string? ModelOverride = null);

public sealed record ChatResult(string Text, int InputTokens, int OutputTokens, string Provider, string Model);

public interface IChatProvider
{
    string Id { get; }
    string Name { get; }
    bool SupportsVision { get; }
    Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken ct);
}

public sealed class AiException : Exception
{
    public int? Status { get; }
    public AiException(string message, int? status = null, Exception? inner = null) : base(message, inner) => Status = status;
}
