namespace RecruIT.List.Services;

/// <summary>
/// A failure whose message is already localized and fit to show the user as it is - unlike any
/// other exception, whose message (a driver's, the framework's) is for the log, not for them. See
/// ErrorReporter, which tells the two apart.
/// </summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message)
        : base(message) { }

    public UserFacingException(string message, Exception innerException)
        : base(message, innerException) { }
}
