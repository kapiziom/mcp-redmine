using System.Diagnostics;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Redaction;
using McpRedmine.Tests.TestSupport;
using McpRedmine.Tools;

namespace McpRedmine.Tests;

public class TextRedactorTests
{
    private static TextRedactor On(params string[] keepDomains) =>
        TextRedactor.For(Targets.Make() with { KeepEmailDomains = keepDomains });

    [Fact]
    public void Emails_are_replaced_by_numbered_placeholders_and_repeats_share_one()
    {
        var text = On().Redact("Reported by Jan.Kowalski+test@Klient.PL, cc anna@firma.com. Again jan.kowalski+test@klient.pl.");

        Assert.Equal("Reported by [email-1], cc [email-2]. Again [email-1].", text);
    }

    [Fact]
    public void Company_domains_stay_visible_including_subdomains_and_others_do_not()
    {
        var text = On("firma.com").Redact("qa@firma.com, qa@test.firma.com, client@evil.com, x@notfirma.com");

        Assert.Equal("qa@firma.com, qa@test.firma.com, [email-1], [email-2]", text);
    }

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz0123456789ABCDEF")]
    [InlineData("glpat-abcdefghijklmnopqrst")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("sk_live_abcdefghijklmnop")]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwx")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuv")]
    [InlineData("npm_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r")]
    public void Well_known_token_formats_are_masked(string token)
    {
        var text = On().Redact($"deploy failed with {token} in the log");

        Assert.Equal("deploy failed with [secret-1] in the log", text);
    }

    [Fact]
    public void Private_key_blocks_are_removed_whole()
    {
        var text = On().Redact("key:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEow\nabc\n-----END RSA PRIVATE KEY-----\nthanks");

        Assert.DoesNotContain("MIIEow", text);
        Assert.Contains("[private-key-1]", text);
        Assert.EndsWith("thanks", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=Sup3rSecret!", "password=[secret-1]")]
    [InlineData("Password: hunter22", "Password: [secret-1]")]
    [InlineData("\"api_key\": \"abcd1234efgh\"", "\"api_key\": \"[secret-1]\"")]
    [InlineData("Server=db;User Id=sa;Password=P@ssw0rd;Database=x", "Server=db;User Id=sa;Password=[secret-1];Database=x")]
    [InlineData("hasło: tajne123", "hasło: [secret-1]")]
    [InlineData("access_token = xyz789abc", "access_token = [secret-1]")]
    public void Key_value_secrets_keep_the_name_and_lose_the_value(string input, string expected) =>
        Assert.Equal(expected, On().Redact(input));

    [Fact]
    public void Credentials_inside_urls_and_authorization_headers_are_masked()
    {
        var text = On().Redact("clone https://deploy:Zx9ykp2mnb@git.firma.com/x.git and Authorization: Bearer abcdefghijklmnop1234567890");

        Assert.Equal("clone https://deploy:[secret-1]@git.firma.com/x.git and Authorization: Bearer [secret-2]", text);
    }

    [Fact]
    public void Masking_a_url_password_does_not_turn_the_rest_into_an_email()
    {
        var text = On().Redact("https://user:pass1234@host.example.com/path");

        Assert.Equal("https://user:[secret-1]@host.example.com/path", text);
    }

    [Fact]
    public void Polish_identifiers_are_masked_only_when_the_checksum_is_valid()
    {
        var text = On().Redact("PESEL 44051401359, bad PESEL 44051401358, NIP 123-456-32-18, bad NIP 123-456-32-19");

        Assert.Equal("PESEL [pesel-1], bad PESEL 44051401358, NIP [nip-1], bad NIP 123-456-32-19", text);
    }

    [Fact]
    public void Iban_and_card_numbers_need_valid_checksums_and_plausible_prefixes()
    {
        var text = On().Redact("konto PL61 1090 1014 0000 0712 1981 2874, nrb 61109010140000071219812874, karta 4111 1111 1111 1111, id 4111 1111 1111 1112");

        Assert.Equal("konto [iban-1], nrb [iban-1], karta [card-1], id 4111 1111 1111 1112", text);
    }

    [Fact]
    public void Ordinary_technical_text_is_left_alone()
    {
        const string text = "Build 10.0.401 failed at 2025-01-31T09:00:00Z, epoch 1700000000000, commit 3f2a9c1b7d4e5f60718293a4b5c6d7e8f9012345, "
                            + "phone +48 600 700 800, order 20250131123, ticket #12345, token expired, password required, see https://example.com/a?b=c";

        Assert.Equal(text, On().Redact(text));
    }

    [Fact]
    public void Placeholders_and_filler_values_are_not_masked_again()
    {
        const string text = "password=[REDACTED] token: ***** secret=[secret-9]";

        Assert.Equal(text, On().Redact(text));
    }

    [Fact]
    public void Known_full_names_become_the_same_form_as_structured_fields_but_single_words_do_not()
    {
        var redactor = On();
        redactor.AddKnownPeople([new RedmineIdName(12, "Jan Kowalski"), new RedmineIdName(7, "bot"), null], redactPeople: true);

        var text = redactor.Redact("Jan  Kowalski said the bot is down; jan kowalski again, not Janek Kowalskiego.");

        Assert.Equal("J.K. (#12) said the bot is down; J.K. (#12) again, not Janek Kowalskiego.", text);
    }

    [Fact]
    public void Names_stay_when_people_redaction_is_off()
    {
        var redactor = On();
        redactor.AddKnownPeople([new RedmineIdName(12, "Jan Kowalski")], redactPeople: false);

        Assert.Equal("Jan Kowalski", redactor.Redact("Jan Kowalski"));
    }

    [Fact]
    public void Disabled_redaction_returns_the_text_untouched()
    {
        var redactor = TextRedactor.For(Targets.Make() with { RedactText = false });

        Assert.Equal("a@b.com password=secret1", redactor.Redact("a@b.com password=secret1"));
        Assert.Null(redactor.Redact(null));
    }

    [Fact]
    public void Pathological_input_finishes_quickly()
    {
        var hostile = string.Concat(Enumerable.Repeat("a.a.a.a.a.a.a.a.a.a.", 5000)) + "@" + new string('b', 50_000)
                      + new string('9', 100_000) + string.Concat(Enumerable.Repeat("password = ", 5000));
        var clock = Stopwatch.StartNew();

        var result = On().Redact(hostile);

        Assert.NotNull(result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Config_defaults_round_trip_and_domains_are_validated()
    {
        Assert.True(Targets.Make().RedactText);
        var good = new RedmineMcpConfig { Targets = [Targets.Make() with { KeepEmailDomains = ["firma.com", "@test.firma.com"] }] };
        var bad = new RedmineMcpConfig { Targets = [Targets.Make() with { KeepEmailDomains = ["a@b.com"] }] };

        Assert.Empty(ConfigValidator.Validate(good));
        Assert.Contains(ConfigValidator.Validate(bad), e => e.Contains("keep_email_domains"));
    }
}

public sealed class RedactionInToolsTests : IDisposable
{
    private const string IssueJson = """
        {"issue":{"id":42,"subject":"Login fails for klient@client.pl","description":"Contact jan.nowak@client.pl or klient@client.pl, password=Sup3rSecret! . Reported by Anna Kowal.",
          "project":{"id":1,"name":"Alpha"},"author":{"id":5,"name":"Anna Kowal"},"assigned_to":{"id":6,"name":"Bob Builder"},
          "custom_fields":[{"id":1,"name":"Customer contact","value":"kontakt@client.pl"},{"id":2,"name":"CC","value":["a@client.pl","b@firma.com"]},{"id":3,"name":"Count","value":4}],
          "journals":[{"id":1,"user":{"id":6,"name":"Bob Builder"},"notes":"Asked Anna Kowal, she wrote from anna@client.pl; token ghp_abcdefghijklmnopqrstuvwxyz0123456789","details":[{"property":"attr","name":"subject","old_value":"x@client.pl","new_value":"y"}]}],
          "attachments":[{"id":9,"filename":"dump-anna@client.pl.txt","filesize":40,"content_type":"text/plain","author":{"id":5,"name":"Anna Kowal"}}]}}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-red-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FixedFactory(RedmineClient client) : IRedmineClientFactory
    {
        public RedmineClient Create(TargetConfig target) => client;
    }

    private static (TargetConfig Target, IssueTools Issues, ProjectTools Projects, StubHandler Handler) Make(TargetConfig target)
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/projects.json", StringComparison.Ordinal) =>
                StubHandler.Json("""{"projects":[{"id":1,"identifier":"a","name":"Alpha for ops@client.pl","description":"Owner: boss@client.pl"}],"total_count":1}"""),
            var p when p.EndsWith("/issues.json", StringComparison.Ordinal) =>
                StubHandler.Json("""{"issues":[{"id":42,"subject":"Login fails for klient@client.pl","assigned_to":{"id":6,"name":"Bob Builder"}}],"total_count":1}"""),
            _ => StubHandler.Json(IssueJson),
        });
        var resolver = new TargetResolver(new RedmineMcpConfig { Targets = [target] });
        var factory = new FixedFactory(Targets.Client(handler, target));
        var scope = new ProjectScope();
        return (target, new IssueTools(resolver, factory, scope), new ProjectTools(resolver, factory, scope), handler);
    }

    [Fact]
    public async Task Get_issue_masks_every_free_text_field_but_keeps_the_meaning()
    {
        var (_, issues, _, _) = Make(Targets.Make("main") with { KeepEmailDomains = ["firma.com"] });

        var json = await issues.GetIssue(42, include_journals: true, include_attachments: true);

        Assert.DoesNotContain("client.pl", json);
        Assert.DoesNotContain("Sup3rSecret", json);
        Assert.DoesNotContain("ghp_", json);
        Assert.DoesNotContain("Anna", json);
        Assert.Contains("b@firma.com", json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Matches(@"^Login fails for \[email-\d+\]$", root.GetProperty("subject").GetString());
        Assert.Contains("password=[secret-", Untrusted.Unwrap(root.GetProperty("description").GetString()));
        Assert.Contains("Reported by A.K. (#5).", Untrusted.Unwrap(root.GetProperty("description").GetString()));
        Assert.Equal(4, root.GetProperty("custom_fields")[2].GetProperty("value").GetInt32());
        Assert.StartsWith("Asked A.K. (#5), she wrote from [email-", Untrusted.Unwrap(root.GetProperty("journals")[0].GetProperty("notes").GetString()), StringComparison.Ordinal);
        Assert.Matches(@"^\[email-\d+\]$", root.GetProperty("attachments")[0].GetProperty("filename").GetString());
    }

    [Fact]
    public async Task The_same_address_gets_the_same_mask_across_fields_of_one_response()
    {
        var (_, issues, _, _) = Make(Targets.Make("main"));

        var json = await issues.GetIssue(42);

        // klient@client.pl appears in the subject and again in the description.
        using var document = JsonDocument.Parse(json);
        var subject = document.RootElement.GetProperty("subject").GetString()!;
        var description = document.RootElement.GetProperty("description").GetString()!;
        var mask = System.Text.RegularExpressions.Regex.Match(subject, @"\[email-\d+\]").Value;
        Assert.Contains($"or {mask},", Untrusted.Unwrap(description));
    }

    [Fact]
    public async Task Switching_text_redaction_off_returns_the_original_text()
    {
        var (_, issues, _, _) = Make(Targets.Make("main") with { RedactText = false });

        var json = await issues.GetIssue(42);

        Assert.Contains("klient@client.pl", json);
        Assert.Contains("Sup3rSecret", json);
    }

    [Fact]
    public async Task Search_rows_and_project_lists_are_masked_too()
    {
        var (_, issues, projects, _) = Make(Targets.Make("main"));

        var rows = await issues.SearchIssues();
        var list = await projects.ListProjects();

        Assert.DoesNotContain("client.pl", rows);
        Assert.Contains("[email-1]", rows);
        Assert.DoesNotContain("client.pl", list);
        Assert.Contains("[email-", list);
    }
}
