namespace ll_launcher.Models;

public sealed record LauncherItem(
    long Id,
    long GroupId,
    string Name,
    string Target,
    string Arguments,
    string WorkingDirectory,
    string Kind,
    bool IsFavorite,
    int SortOrder);
