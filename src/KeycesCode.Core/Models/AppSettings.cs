namespace KeycesCode.Core.Models;

public sealed record AppSettings
{
    public string ThemeId { get; init; } = "dark";
    public string FontFamily { get; init; } = "Cascadia Code";
    public double FontSize { get; init; } = 14;
    public bool AutoSave { get; init; } = false;
    public int AutoSaveDelaySeconds { get; init; } = 30;
    public bool ShowMinimap { get; init; } = true;
    public string AssistantModel { get; init; } = "qwen2.5-coder:1.5b";
}