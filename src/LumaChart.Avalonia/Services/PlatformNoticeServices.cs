namespace LumaChart.Avalonia.Services;

public enum PlatformNoticeSeverity
{
    Information,
    Warning,
    Error,
    Question
}

/// <summary>
/// Platform boundary for the one-way notices the editor raises: export results,
/// import failures, recovery notes and operation errors. Windows shows a native
/// message box; a platform without one has to supply its own surface or the
/// user is simply never told what happened.
/// </summary>
public interface IPlatformNoticeService
{
    void Show(string caption, string message, PlatformNoticeSeverity severity);
}

public static class PlatformNoticeServices
{
    public static IPlatformNoticeService? Current { get; set; }
}
