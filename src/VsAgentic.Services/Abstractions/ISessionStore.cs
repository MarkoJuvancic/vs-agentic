using VsAgentic.Services.Models;

namespace VsAgentic.Services.Abstractions;

public interface ISessionStore
{
    // Workspace
    Task<bool> WorkspaceExistsAsync(string folderPath);
    Task EnsureWorkspaceAsync(string folderPath);
    string GetWorkspaceId(string folderPath);

    // Session index
    Task<IReadOnlyList<SessionEntry>> GetSessionIndexAsync(string folderPath);
    Task<SessionEntry> CreateSessionAsync(string folderPath, string title);
    Task UpdateSessionAsync(string folderPath, SessionEntry entry);
    Task DeleteSessionAsync(string folderPath, int sessionId);

    /// <summary>
    /// Marks a session as archived, or clears that mark when
    /// <paramref name="archived"/> is false. Nothing on disk moves.
    /// </summary>
    Task SetSessionArchivedAsync(string folderPath, int sessionId, bool archived);

    /// <summary>
    /// The startup clean-up, in two stages, both measured against
    /// <paramref name="days"/>: a session that has been idle for that long is
    /// archived, and a session that has been archived for that long is
    /// deleted. A session therefore survives idle for twice
    /// <paramref name="days"/> before anything is lost, and the user has the
    /// whole second stretch to restore it.
    /// </summary>
    Task PurgeOldSessionsAsync(string folderPath, int days);

    // Messages
    Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string folderPath, int sessionId);
    Task SaveMessagesAsync(string folderPath, int sessionId, IReadOnlyList<PersistedMessage> messages);
    Task AppendMessageAsync(string folderPath, int sessionId, PersistedMessage message);

    // AI conversation history
    Task<string?> GetConversationHistoryAsync(string folderPath, int sessionId);
    Task SaveConversationHistoryAsync(string folderPath, int sessionId, string historyJson);
}
