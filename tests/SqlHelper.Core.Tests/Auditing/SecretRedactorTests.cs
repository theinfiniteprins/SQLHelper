using SqlHelper.Core.Auditing;

namespace SqlHelper.Core.Tests.Auditing;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("CREATE LOGIN app WITH PASSWORD = 'hunter2'")]
    [InlineData("ALTER LOGIN app WITH PASSWORD='hunter2', CHECK_POLICY = OFF")]
    [InlineData("ALTER LOGIN app WITH PASSWORD = N'hunter2'")]
    [InlineData("CREATE LOGIN app WITH password = 'hunter2'")]
    [InlineData("EXEC sp_addlogin 'app', @passwd = 'x'; CREATE USER u WITH PASSWORD = 'hunter2'")]
    public void A_password_in_a_statement_is_never_written_to_the_audit_log(string statement)
    {
        string scrubbed = SecretRedactor.Scrub(statement)!;

        Assert.DoesNotContain("hunter2", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SecretRedactor.Placeholder, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hashed_password_is_redacted_too()
    {
        string scrubbed = SecretRedactor.Scrub("CREATE LOGIN app WITH PASSWORD = 0x0200ABCDEF HASHED")!;

        Assert.DoesNotContain("0x0200ABCDEF", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_password_containing_a_doubled_quote_is_redacted_whole()
    {
        string scrubbed = SecretRedactor.Scrub("ALTER LOGIN app WITH PASSWORD = 'he''s in'")!;

        Assert.DoesNotContain("he''s in", scrubbed, StringComparison.Ordinal);
        Assert.Equal($"ALTER LOGIN app WITH PASSWORD = {SecretRedactor.Placeholder}", scrubbed);
    }

    [Fact]
    public void An_ordinary_statement_is_left_exactly_as_it_was()
    {
        const string statement = "UPDATE dbo.Orders SET Status = 'Closed' WHERE Id = 42";

        Assert.Equal(statement, SecretRedactor.Scrub(statement));
    }

    [Fact]
    public void A_column_that_merely_mentions_password_is_not_mangled()
    {
        const string statement = "UPDATE dbo.Users SET PasswordChangedOn = GETDATE() WHERE Id = 7";

        Assert.Equal(statement, SecretRedactor.Scrub(statement));
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Null(SecretRedactor.Scrub(null));
        Assert.Equal(string.Empty, SecretRedactor.Scrub(string.Empty));
    }
}
