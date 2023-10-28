using OKX.Net.Objects.Trade;

namespace SIGrid.TradeBot.Extensions;

public static class GridLineOrderIdExtensions
{
    public static GridLineOrderId GetGridLineOrderId(this OKXOrder? order) => 
        GetGridLineOrderId(order?.ClientOrderId);

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderPlaceRequest? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderPlaceResponse? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderCancelRequest? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    private static GridLineOrderId GetGridLineOrderId(string? clientOrderId) => 
        string.IsNullOrWhiteSpace(clientOrderId) ? default(GridLineOrderId) : clientOrderId;
}
