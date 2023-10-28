namespace SIGrid.Exchange.Shared;

/// <summary>
///     Holds a reference to the type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type to reference</typeparam>
public class Ref<T>
    where T : struct
{
    public Ref(T value)
    {
        Value = value;
    }

    public T Value { get; set; }

    public static implicit operator Ref<T>(T? value)
    {
        return new Ref<T>(value.GetValueOrDefault());
    }

    public static implicit operator T(Ref<T> valueRef)
    {
        return valueRef.Value;
    }

    public override string? ToString()
    {
        return Value.ToString();
    }
}
