using System.Numerics;
using System.Text.RegularExpressions;
using McpRedmine.Config;

namespace McpRedmine.Redmine.Redaction;

/// <summary>
/// One-way masking of sensitive values in free text before it is shown to the model: e-mail addresses, API keys and
/// tokens, passwords in <c>key=value</c> pairs and URLs, private keys, and checksum-validated PESEL, NIP, IBAN and card
/// numbers. Values are replaced by numbered placeholders such as <c>[email-1]</c>; the same value gets the same
/// placeholder within one instance (use one instance per tool call), so references stay readable.
/// Names inside text are only masked when they are known Redmine users of the same response (see <see cref="AddKnownPeople"/>).
/// It cannot clean images, and it is a safety net, not a guarantee.
/// </summary>
public sealed partial class TextRedactor
{
    public const string Withheld = "[text withheld: it could not be scanned safely]";

    private const int TimeoutMilliseconds = 2000;

    private readonly bool _enabled;
    private readonly string[] _keepEmailDomains;
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly List<(Regex Pattern, string Replacement)> _people = [];

    private TextRedactor(bool enabled, IEnumerable<string>? keepEmailDomains)
    {
        _enabled = enabled;
        _keepEmailDomains = (keepEmailDomains ?? []).Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToArray();
    }

    public static TextRedactor For(TargetConfig target) => new(target.RedactText, target.KeepEmailDomains);

    public static TextRedactor Off() => new(false, null);

    public bool Enabled => _enabled;

    /// <summary>Masks the full names of users known from the same response with the same form used in structured fields.</summary>
    public void AddKnownPeople(IEnumerable<RedmineIdName?> people, bool redactPeople)
    {
        if (!_enabled || !redactPeople)
            return;

        foreach (var person in people.Where(p => p?.Name is not null).DistinctBy(p => p!.Id).OrderByDescending(p => p!.Name!.Length))
        {
            var tokens = person!.Name!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // A single word ("bot", "Admin") would also hit ordinary text; only full names are replaced.
            if (tokens.Length < 2)
                continue;

            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", tokens.Select(Regex.Escape)) + @"(?![\p{L}\p{N}])";
            _people.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMilliseconds)),
                PersonRedactor.Format(person, redact: true)!));
        }
    }

    public string? Redact(string? text)
    {
        if (!_enabled || string.IsNullOrEmpty(text))
            return text;

        try
        {
            var result = PrivateKey().Replace(text, m => Placeholder("private-key", m.Value));
            result = Jwt().Replace(result, m => Placeholder("secret", m.Value));
            result = KnownToken().Replace(result, m => Placeholder("secret", m.Value));
            result = UrlPassword().Replace(result, m => Placeholder("secret", m.Value));
            result = AuthorizationValue().Replace(result, m => Placeholder("secret", m.Value));
            result = SecretAssignment().Replace(result, MaskAssignment);
            result = Email().Replace(result, MaskEmail);
            result = Iban().Replace(result, m => IsPolishIban(m.Value) ? Placeholder("iban", Digits(m.Value)) : m.Value);
            result = Card().Replace(result, m => IsCardNumber(Digits(m.Value)) ? Placeholder("card", Digits(m.Value)) : m.Value);
            result = Pesel().Replace(result, m => IsPesel(m.Value) ? Placeholder("pesel", m.Value) : m.Value);
            result = Nip().Replace(result, m => IsNip(Digits(m.Value)) ? Placeholder("nip", Digits(m.Value)) : m.Value);

            foreach (var (pattern, replacement) in _people)
                result = pattern.Replace(result, replacement);

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: if the text cannot be scanned in time, none of it is shown.
            return Withheld;
        }
    }

    private string Placeholder(string kind, string value)
    {
        var key = kind + "\0" + value;
        if (_placeholders.TryGetValue(key, out var existing))
            return existing;

        _counters[kind] = _counters.GetValueOrDefault(kind) + 1;
        return _placeholders[key] = $"[{kind}-{_counters[kind]}]";
    }

    private string MaskAssignment(Match match)
    {
        var value = match.Groups["value"].Value;

        // Already masked, a template such as [PASSWORD], or nothing but filler like ****.
        if (value.StartsWith('[') || value.All(c => c is '*' or 'x' or 'X' or '.' or '-'))
            return match.Value;

        return match.Groups["key"].Value + match.Groups["sep"].Value + Placeholder("secret", value);
    }

    private string MaskEmail(Match match)
    {
        var domain = match.Groups["domain"].Value.ToLowerInvariant();
        if (_keepEmailDomains.Any(kept => domain == kept || domain.EndsWith("." + kept, StringComparison.Ordinal)))
            return match.Value;

        return Placeholder("email", match.Value.ToLowerInvariant());
    }

    // ---- validators: patterns find candidates, checksums keep ordinary numbers readable -----------------------

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    internal static bool IsPesel(string value)
    {
        if (value.Length != 11 || !value.All(char.IsAsciiDigit))
            return false;

        int[] weights = [1, 3, 7, 9, 1, 3, 7, 9, 1, 3];
        var sum = weights.Select((w, i) => w * (value[i] - '0')).Sum();
        return (10 - (sum % 10)) % 10 == value[10] - '0';
    }

    internal static bool IsNip(string digits)
    {
        if (digits.Length != 10)
            return false;

        int[] weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        var sum = weights.Select((w, i) => w * (digits[i] - '0')).Sum() % 11;
        return sum != 10 && sum == digits[9] - '0';
    }

    internal static bool IsPolishIban(string value)
    {
        var digits = Digits(value);
        if (digits.Length != 26)
            return false;

        // Move the check digits behind the account number and read "PL" as 2521.
        return BigInteger.Parse(digits[2..] + "2521" + digits[..2], System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
    }

    internal static bool IsCardNumber(string digits)
    {
        // Issuer prefixes (Visa 4, Mastercard 5/2, Amex 34/37, Discover 6) keep timestamps and ids out.
        if (digits.Length is < 13 or > 19 || digits[0] is not ('3' or '4' or '5' or '6' or '2'))
            return false;

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    // ---- patterns -------------------------------------------------------------------------------------------

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Jwt();

    // GitHub (ghp_..., github_pat_...), GitLab, Slack, Stripe, OpenAI/Anthropic-style sk-..., AWS access key ids, Google API keys, npm.
    [GeneratedRegex(
        @"\b(?:(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_-]{15,}|xox[baprs]-[A-Za-z0-9-]{10,}|(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{10,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|npm_[A-Za-z0-9]{36})\b",
        RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?<=\b[a-z][a-z0-9+.-]*://[^\s/:@]+:)[^\s/@]+(?=@)", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"(?<=\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/-]{16,}=*", RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(
        @"(?<key>\b(?:password|passwd|pwd|secret|client[_-]?secret|api[_-]?key|apikey|access[_-]?key|access[_-]?token|auth[_-]?token|refresh[_-]?token|private[_-]?key|token|has[łl]o)\b)(?<sep>[""']?\s*[:=]\s*[""']?)(?<value>[^\s""'`;,&<>]{3,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})(?![A-Za-z0-9-])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:PL\s?)?\d{2}(?:\s?\d{4}){6}(?!\d)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d-])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Card();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Pesel();

    [GeneratedRegex(@"(?<![\d-])\d{3}-?\d{3}-?\d{2}-?\d{2}(?![\d-])", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Nip();
}
