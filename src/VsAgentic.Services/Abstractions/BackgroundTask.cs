namespace VsAgentic.Services.Abstractions;

/// <summary>
/// A task the CLI runs in the background, for example a shell command started
/// with <c>run_in_background</c>. Taken from the CLI's
/// <c>system/background_tasks_changed</c> event.
/// </summary>
public sealed record BackgroundTask(string Id, string Description, string? Type);
