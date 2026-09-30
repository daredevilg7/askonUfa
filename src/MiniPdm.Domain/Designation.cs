using System.Text.RegularExpressions;

namespace MiniPdm.Domain;

public static partial class Designation
{
    [GeneratedRegex("\\A[А-ЯЁ]{4}\\.[0-9]{6}\\.[0-9]{3}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}
