namespace AuditionModStudio.App.Shell;

public sealed record UserActivityEvent(string Severity, string Message);

public interface IUserActivityService
{
    event EventHandler<UserActivityEvent>? Published;
    void Publish(string severity, string message);
}

public sealed class UserActivityService : IUserActivityService
{
    public event EventHandler<UserActivityEvent>? Published;

    public void Publish(string severity, string message)
    {
        if (severity is not ("INFO" or "SUCCESS" or "WARNING" or "ERROR"))
            throw new ArgumentOutOfRangeException(nameof(severity));
        if (string.IsNullOrWhiteSpace(message) || message.Length > 240)
            throw new ArgumentException("ACTIVITY_MESSAGE_INVALID", nameof(message));
        Published?.Invoke(this, new(severity, message));
    }
}
