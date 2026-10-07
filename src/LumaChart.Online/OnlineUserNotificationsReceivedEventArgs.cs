namespace LumaChart.Online;

/// <summary>
/// Publishes a committed notification page-chain to every desktop surface that
/// shares an <see cref="OnlineAccountSession"/>. Polling remains single-cursor;
/// consumers therefore cannot steal notifications from one another.
/// </summary>
public sealed class OnlineUserNotificationsReceivedEventArgs : EventArgs
{
    public OnlineUserNotificationsReceivedEventArgs(
        long userId,
        IReadOnlyList<OnlineUserNotificationDto> notifications)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userId, 1);
        ArgumentNullException.ThrowIfNull(notifications);
        UserId = userId;
        Notifications = notifications.ToArray();
    }

    public long UserId { get; }
    public IReadOnlyList<OnlineUserNotificationDto> Notifications { get; }
}
