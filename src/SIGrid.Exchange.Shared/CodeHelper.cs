namespace SIGrid.Exchange.Shared;

public static class CodeHelper
{
    public struct VoidResult
    {
        public static VoidResult Empty = new();
    }

    public static VoidResult WrapVoid(Action action)
    {
        action();
        return VoidResult.Empty;
    }

    public static VoidResult NoOp() => VoidResult.Empty;
}
