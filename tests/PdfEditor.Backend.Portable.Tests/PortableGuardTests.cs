using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

public class PortableGuardTests
{
    [Fact]
    public void Translates_a_parser_crash_into_a_catchable_error()
    {
        // Throwing NullReferenceException deliberately: this is exactly what PdfSharp's content
        // parser does on an unrecognised operator, and reproducing it is the point of the test.
#pragma warning disable CA2201
        var error = Assert.Throws<InvalidDataException>(
            () => PortableGuard.Run<int>(() => throw new NullReferenceException("inside the parser")));
#pragma warning restore CA2201

        Assert.IsType<NullReferenceException>(error.InnerException);
    }

    [Fact]
    public void Lets_an_unrelated_failure_through_untouched()
    {
        // Swallowing everything would hide our own bugs behind a "malformed PDF" message.
        Assert.Throws<InvalidOperationException>(
            () => PortableGuard.Run(() => throw new InvalidOperationException("a real bug")));
    }

    [Fact]
    public void Returns_the_value_when_nothing_goes_wrong()
    {
        Assert.Equal(42, PortableGuard.Run(() => 42));
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(EndOfStreamException))]
    public void Recognises_every_shape_a_malformed_file_provokes(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(PortableGuard.IsMalformedInput(exception));
    }

    [Fact]
    public void Does_not_recognise_a_programming_error()
    {
        Assert.False(PortableGuard.IsMalformedInput(new InvalidOperationException()));
    }
}
