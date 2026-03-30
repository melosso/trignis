public readonly record struct DeadLetterStats
{
    public long TotalCount { get; init; }
    public long LastHourCount { get; init; }
    public long Last24HoursCount { get; init; }
    public long Last7DaysCount { get; init; }
    public string? MostCommonError { get; init; }
    public long MostCommonErrorCount { get; init; }
}
