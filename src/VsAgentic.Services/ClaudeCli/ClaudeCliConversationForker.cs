using System.Text.Json;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Forks a Claude Code conversation. The new chat keeps no session id of its
/// own, only the original's under <see cref="ForkFromProperty"/>; its first
/// start runs <c>claude --resume &lt;id&gt; --fork-session</c>, and the CLI
/// then copies the conversation under a new id. Nothing is copied before the
/// user sends a message, so a fork that is never used leaves nothing in the
/// CLI's own store.
/// </summary>
public sealed class ClaudeCliConversationForker : IConversationForker
{
    /// <summary>The field of the history JSON that names the conversation to fork.</summary>
    public const string ForkFromProperty = "forkFromCliSessionId";

    public string? CreateForkHistory(string? serializedHistory)
    {
        if (string.IsNullOrEmpty(serializedHistory)) return null;

        try
        {
            using var doc = JsonDocument.Parse(serializedHistory!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // A fork of a fork that has not started yet forks the same
            // conversation its source would have.
            var source = ReadString(root, "cliSessionId") ?? ReadString(root, ForkFromProperty);
            if (source is null) return null;

            // The model goes with it: --resume keeps the conversation on the
            // model it ran on, and the panel shows it from here.
            return JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["cliSessionId"] = null,
                ["model"] = ReadString(root, "model"),
                [ForkFromProperty] = source,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;
}
