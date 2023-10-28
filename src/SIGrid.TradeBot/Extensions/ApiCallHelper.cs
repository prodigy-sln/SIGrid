using CryptoExchange.Net.Objects;

namespace SIGrid.TradeBot.Extensions;

public static class ApiCallHelper
{
    private const int TooManyRequestsErrorCode = 429;

    public static async Task<WebCallResult<T>> ExecuteWithRetry<T>(this Func<Task<WebCallResult<T>>> callTaskFunc, int maxBackOffRetries = 10, TimeSpan? backOffInterval = default, int backOffRetryNumber = 1, CancellationToken ct = default)
    {
        var result = await callTaskFunc().ConfigureAwait(false);

        if (result.Success)
        {
            return result;
        }

        if (result is { Success: false, Error.Code: TooManyRequestsErrorCode })
        {
            var delay = backOffInterval?.TotalMilliseconds ?? 100.0D * backOffRetryNumber;
            await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            return await ExecuteWithRetry<T>(callTaskFunc, maxBackOffRetries, backOffInterval, backOffRetryNumber + 1, ct);
        }

        return result;
    }
}
