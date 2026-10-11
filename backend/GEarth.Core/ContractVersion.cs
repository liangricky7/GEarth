namespace GEarth.Core;

// A product rename changes the namespace label, not the validated wire shape.
public static class ContractVersion
{
    public static bool Matches(string value, string expected)
    {
        var dot = value.IndexOf('.');
        var expectedDot = expected.IndexOf('.');
        return dot > 0 && expectedDot > 0 && value[0] is >= 'a' and <= 'z'
            && value[..dot].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
            && value[(dot + 1)..] == expected[(expectedDot + 1)..];
    }
}
