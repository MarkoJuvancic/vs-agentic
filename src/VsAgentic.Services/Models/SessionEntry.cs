namespace VsAgentic.Services.Models;

public class SessionEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = "New Session";

    /// <summary>
    /// True once the user has renamed the session by hand. The title generated
    /// from the first message is then skipped, so a manual name is never
    /// overwritten.
    /// </summary>
    public bool TitleIsCustom { get; set; }

    public int Ordinal { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
}
