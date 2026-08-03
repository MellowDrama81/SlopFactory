namespace Mellow.SlopFactory.Domain;

public class SlopFactoryException : Exception
{
    public SlopFactoryException(string message) : base(message)
    {
    }

    public SlopFactoryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class LibraryValidationException : SlopFactoryException
{
    public LibraryValidationException(string message) : base(message)
    {
    }
}

public sealed class LibraryLockedException : SlopFactoryException
{
    public LibraryLockedException(string message) : base(message)
    {
    }
}

public sealed class NameConflictException : SlopFactoryException
{
    public NameConflictException(string message) : base(message)
    {
    }
}

public sealed class RecordNotFoundException : SlopFactoryException
{
    public RecordNotFoundException(string message) : base(message)
    {
    }
}

