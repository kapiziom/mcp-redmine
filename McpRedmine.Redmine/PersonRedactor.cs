namespace McpRedmine.Redmine;

/// <summary>
/// Turns a person's name into initials before it is shown to the model, so tool results do not carry
/// real names. The numeric id stays: it keeps people distinguishable and usable in filters (<c>assigned_to</c>).
/// </summary>
public static class PersonRedactor
{
    /// <summary>"Jan Kowalski" -> "J.K.", "Anna Nowak-Kowalska" -> "A.N.K.", "bot" -> "B.".</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";

        var initials = name
            .Split([' ', '\t', ' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.FirstOrDefault(char.IsLetterOrDigit))
            .Where(letter => letter != default)
            .Select(letter => char.ToUpperInvariant(letter) + ".");

        var joined = string.Concat(initials);
        return joined.Length == 0 ? "?" : joined;
    }

    /// <summary>"J.K. (#12)" when redacting, the plain name otherwise. Null stays null.</summary>
    public static string? Format(RedmineIdName? person, bool redact)
    {
        if (person is null)
            return null;

        return redact ? $"{Initials(person.Name)} (#{person.Id})" : person.Name;
    }
}
