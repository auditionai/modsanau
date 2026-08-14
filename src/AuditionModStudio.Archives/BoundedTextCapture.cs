using System.Text;

namespace AuditionModStudio.Archives;

internal sealed class BoundedTextCapture(int maximumCharacters)
{
    private readonly StringBuilder _value = new(Math.Min(maximumCharacters, 4096));

    public bool IsTruncated { get; private set; }

    public void Append(ReadOnlySpan<char> value)
    {
        var remaining = maximumCharacters - _value.Length;
        if (remaining <= 0)
        {
            IsTruncated = true;
            return;
        }

        var accepted = Math.Min(remaining, value.Length);
        _value.Append(value[..accepted]);
        IsTruncated |= accepted < value.Length;
    }

    public override string ToString() => _value.ToString();
}
