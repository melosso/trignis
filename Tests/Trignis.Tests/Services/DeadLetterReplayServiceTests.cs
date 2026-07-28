using System;
using Trignis.Services;
using Xunit;

namespace Trignis.Tests.Services;

public class DeadLetterReplayServiceTests
{
    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 120)]
    [InlineData(2, 240)]
    [InlineData(3, 480)]
    [InlineData(4, 960)]
    public void Backoff_doubles_per_attempt(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeadLetterReplayService.Backoff(attempts, 60));
    }

    [Fact]
    public void Backoff_is_capped_so_a_long_outage_never_parks_a_row_for_days()
    {
        Assert.Equal(TimeSpan.FromHours(6), DeadLetterReplayService.Backoff(30, 60));
    }

    [Fact]
    public void Backoff_does_not_overflow_at_large_attempt_counts()
    {
        // Math.Pow keeps this finite where a shift would have wrapped.
        Assert.Equal(TimeSpan.FromHours(6), DeadLetterReplayService.Backoff(int.MaxValue, 60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Backoff_floors_a_nonsense_base_delay_at_one_second(int baseSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(1), DeadLetterReplayService.Backoff(0, baseSeconds));
    }

    [Fact]
    public void Backoff_never_returns_a_negative_delay_for_a_negative_attempt_count()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), DeadLetterReplayService.Backoff(-1, 60));
    }
}
