using Xunit;

namespace PdfEditor.Perf.Tests;

/// <summary>
/// Guards how the budget multiplier is chosen. These are plain logic tests (no timing), so they
/// never flake — they exist because getting this wrong is exactly what made the performance guards
/// fail at random in solution-wide CI runs, where the suites compete for a shared runner.
/// </summary>
public class PerfHarnessTests
{
    [Theory]
    [InlineData("2", 2.0)]      // explicit override wins...
    [InlineData("0.5", 0.5)]    // ...including a stricter-than-default one
    [InlineData("3", 3.0)]      // what ci.yml's dedicated perf job sets
    public void ExplicitSlack_AlwaysWins(string env, double expected)
    {
        Assert.Equal(expected, PerfHarness.ResolveSlack(env, isCi: true));
        Assert.Equal(expected, PerfHarness.ResolveSlack(env, isCi: false));
    }

    [Theory]
    [InlineData(null)]      // variable not set at all
    [InlineData("")]        // set but empty
    [InlineData("abc")]     // unparseable
    [InlineData("0")]       // non-positive: would zero out every budget
    [InlineData("-1")]
    public void WithoutAUsableOverride_CiIsRelaxed_AndLocalStaysStrict(string? env)
    {
        Assert.Equal(PerfHarness.CiSlack, PerfHarness.ResolveSlack(env, isCi: true));
        Assert.Equal(1.0, PerfHarness.ResolveSlack(env, isCi: false));
    }

    [Fact]
    public void CiSlack_ActuallyRelaxesTheBudgets()
    {
        Assert.True(PerfHarness.CiSlack > 1.0, "a CI default that doesn't relax anything is pointless");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("True")]
    public void CiIsDetected_FromTruthyValues(string value) => Assert.True(PerfHarness.IsCi(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("0")]
    public void CiIsNotDetected_WhenUnsetOrFalsey(string? value) => Assert.False(PerfHarness.IsCi(value));
}
