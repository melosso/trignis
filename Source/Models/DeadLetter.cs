public class DeadLetterStats
{
    public long TotalCount { get; set; }
    public long LastHourCount { get; set; }
    public long Last24HoursCount { get; set; }
    public long Last7DaysCount { get; set; }
    public string? MostCommonError { get; set; }
    public long MostCommonErrorCount { get; set; }
}