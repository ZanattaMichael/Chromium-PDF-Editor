namespace PdfEditor.Backend.Portable;

/// <summary>
/// Turns the exceptions a malformed PDF provokes out of a parser into a single, catchable
/// <see cref="InvalidDataException"/>.
///
/// This mirrors PdfIo.Guarded in PdfEditor.Core, and it is just as necessary here: PdfSharp's
/// content-stream parser throws a bare <see cref="NullReferenceException"/> on any operator it does
/// not recognise, which a hostile or merely unusual document can trigger at will. Without this a
/// bad input reaches the native host as a crash rather than an error response.
/// </summary>
public static class PortableGuard
{
    public static T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return operation();
        }
        catch (Exception ex) when (IsMalformedInput(ex))
        {
            throw new InvalidDataException("The PDF could not be parsed.", ex);
        }
    }

    public static void Run(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Run<object?>(() => { operation(); return null; });
    }

    /// <summary>
    /// The exception shapes a corrupt or unusual file provokes from a parser that assumed its input
    /// was well-formed. These are bugs in the parser, not in the caller, but they are the caller's
    /// problem to contain.
    /// </summary>
    public static bool IsMalformedInput(Exception ex) => ex
        is NullReferenceException
        or IndexOutOfRangeException
        or ArgumentOutOfRangeException
        or InvalidCastException
        or KeyNotFoundException
        or FormatException
        or OverflowException
        or EndOfStreamException;
}
