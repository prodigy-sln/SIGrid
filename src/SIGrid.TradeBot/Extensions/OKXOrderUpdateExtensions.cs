using OKX.Net.Enums;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;

namespace SIGrid.TradeBot.Extensions;

public static class OKXOrderUpdateExtensions
{
    public static decimal GetFillFeeNotionalUsd(this OKXOrderUpdate orderUpdate, OKXInstrument instrument)
    {
        switch (instrument.ContractType)
        {
            case OKXContractType.Linear:
                return orderUpdate.FillFee;
            case OKXContractType.Inverse:
                return orderUpdate.FillFee * orderUpdate.FillPrice ?? 0;
            default:
                return 0;
        }
    }
}
