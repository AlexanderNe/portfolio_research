namespace PortfolioTradingSystem.Domain.Enums;

public enum SignalDirection
{
    Long = 1,
    Short = -1
}

public enum ProcessingStatus
{
    Paused = 0,
    Running = 1
}

public enum ExitReason
{
    StopLoss,
    TakeProfit,
    SignalReversal,
    Manual,
    System
}

public enum SignalType
{
    PositionOpened,
    PositionClosed
}