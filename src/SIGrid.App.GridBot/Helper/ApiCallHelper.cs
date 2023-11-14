using System.Net;
using CryptoExchange.Net.Objects;

namespace SIGrid.App.GridBot.Helper;

public static class ApiCallHelper
{
    private const int TooManyRequestsErrorCode = (int) HttpStatusCode.TooManyRequests;

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
            return await ExecuteWithRetry(callTaskFunc, maxBackOffRetries, backOffInterval, backOffRetryNumber + 1, ct);
        }

        return result;
    }
}
