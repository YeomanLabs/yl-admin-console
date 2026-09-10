using FluentAssertions;
using Xunit;

namespace AdminConsole.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void TestHarness_IsWired()
    {
        (1 + 1).Should().Be(2);
    }
}
