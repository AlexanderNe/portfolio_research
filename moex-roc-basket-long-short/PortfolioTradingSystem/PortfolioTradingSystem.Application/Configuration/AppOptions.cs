namespace PortfolioTradingSystem.Application.Configuration;

/// <summary>T-Bank (Tinkoff) Invest API connection settings.</summary>
public sealed class TinkoffOptions
{
    public const string SectionName = "Tinkoff";

    public string ApiUrl { get; set; } = "https://invest-public-api.tbank.ru:443";

    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Use the sandbox endpoint instead of production.</summary>
    public bool SandboxMode { get; set; }

    /// <summary>Maximum number of historical daily bars used for warm-up.</summary>
    public int HistoricMaxBars { get; set; } = 700;

    /// <summary>Class code used when resolving a bare ticker (MOEX equities main board: TQBR). Empty = any class.</summary>
    public string ResolveClassCode { get; set; } = "TQBR";
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>Channel/chat id (e.g. "@channel" or numeric chat id).</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Master switch: when false signals are not sent even if configured.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = string.Empty;

    public string Realm { get; set; } = "PortfolioTradingSystem";
}

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How often the supervisor re-syncs engines with the instrument table.</summary>
    public int RefreshInstrumentsIntervalSeconds { get; set; } = 60;

    /// <summary>Initial stream reconnect backoff.</summary>
    public int ReconnectDelaySeconds { get; set; } = 2;

    /// <summary>Maximum stream reconnect backoff.</summary>
    public int ReconnectMaxDelaySeconds { get; set; } = 60;
}