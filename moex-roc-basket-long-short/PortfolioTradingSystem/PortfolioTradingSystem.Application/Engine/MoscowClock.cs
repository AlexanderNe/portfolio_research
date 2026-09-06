namespace PortfolioTradingSystem.Application.Engine;

/// <summary>
/// Moscow exchange time constant. MOEX trades at UTC+3 year-round; the research
/// data and daily-bar/ROC logic are keyed on Moscow calendar days.
/// </summary>
public static class MoscowClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    public static DateTimeOffset ToMoscow(this DateTimeOffset utc) => utc.ToOffset(Offset);

    public static DateOnly ToMoscowDate(this DateTimeOffset value) => DateOnly.FromDateTime(value.ToOffset(Offset).DateTime);
}