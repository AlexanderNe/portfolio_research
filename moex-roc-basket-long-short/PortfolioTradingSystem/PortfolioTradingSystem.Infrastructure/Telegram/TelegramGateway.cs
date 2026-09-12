using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioTradingSystem.Application.Configuration;
using PortfolioTradingSystem.Application.Ports;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace PortfolioTradingSystem.Infrastructure.Telegram;

/// <summary>
/// Publishes signals to the configured Telegram channel. Sends nothing when the
/// bot is disabled or not configured (an empty bot token is not a failure).
/// Send failures are logged and swallowed so the caller never loses signal/trade
/// records because a notification could not be delivered.
/// </summary>
public sealed class TelegramGateway : ITelegramGateway
{
    private readonly bool _enabled;
    private readonly TelegramBotClient? _bot;
    private readonly string? _chatId;
    private readonly ILogger<TelegramGateway> _logger;

    public TelegramGateway(IOptions<TelegramOptions> options, ILogger<TelegramGateway> logger)
    {
        var o = options.Value;
        _logger = logger;
        _enabled = o.Enabled && !string.IsNullOrWhiteSpace(o.BotToken) && !string.IsNullOrWhiteSpace(o.ChannelId);
        if (_enabled)
        {
            _bot = new TelegramBotClient(o.BotToken);
            _chatId = o.ChannelId;
        }
        else if (o.Enabled)
        {
            _logger.LogWarning("Telegram is enabled but bot token / channel id is not configured; notifications disabled");
        }
    }

    private const int Attempts = 3;

    public async Task SendMessageAsync(string text, CancellationToken ct)
    {
        if (!_enabled || _bot is null || _chatId is null)
        {
            return;
        }

        // An undelivered signal is a trade the operator never sees, so a single
        // transient network blip should not be the end of it. Still swallowed at
        // the end: the trade record is already committed either way.
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await _bot.SendMessage(new ChatId(_chatId), text, cancellationToken: ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt == Attempts)
                {
                    _logger.LogError(
                        ex, "Telegram send failed to chat {ChatId} after {Attempts} attempts; signal NOT delivered",
                        _chatId, Attempts);
                    return;
                }

                _logger.LogWarning(
                    ex, "Telegram send attempt {Attempt}/{Attempts} failed; retrying", attempt, Attempts);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}