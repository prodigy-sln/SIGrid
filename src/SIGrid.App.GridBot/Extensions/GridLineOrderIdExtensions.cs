using OKX.Net.Objects.Trade;

namespace SIGrid.App.GridBot.Extensions;

public static class GridLineOrderIdExtensions
{
    public static GridLineOrderId GetGridLineOrderId(this OKXOrder? order) => 
        GetGridLineOrderId(order?.ClientOrderId);

    public static int GetGridLineIndex(this OKXOrder? order) => GetGridLineOrderId(order).LineIndex;

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderPlaceRequest? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static int GetGridLineIndex(this OKXOrderPlaceRequest? order) => GetGridLineOrderId(order).LineIndex;

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderPlaceResponse? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static int GetGridLineIndex(this OKXOrderPlaceResponse? order) => GetGridLineOrderId(order).LineIndex;

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderCancelRequest? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static int GetGridLineIndex(this OKXOrderCancelRequest? order) => GetGridLineOrderId(order).LineIndex;

    public static GridLineOrderId GetGridLineOrderId(this OKXOrderCancelResponse? placeRequest) => 
        GetGridLineOrderId(placeRequest?.ClientOrderId);

    public static int GetGridLineIndex(this OKXOrderCancelResponse? order) => GetGridLineOrderId(order).LineIndex;

    private static GridLineOrderId GetGridLineOrderId(string? clientOrderId) => 
        string.IsNullOrWhiteSpace(clientOrderId) ? default(GridLineOrderId) : clientOrderId;
}
