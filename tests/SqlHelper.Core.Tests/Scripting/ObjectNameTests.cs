using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Scripting;

public sealed class ObjectNameTests
{
    [Theory]
    [InlineData("usp_GetOrders", "dbo", "usp_GetOrders")]
    [InlineData("sales.usp_GetOrders", "sales", "usp_GetOrders")]
    [InlineData("[dbo].[usp_GetOrders]", "dbo", "usp_GetOrders")]
    [InlineData("[dbo].[usp Get Orders]", "dbo", "usp Get Orders")]
    [InlineData("\"sales\".\"usp_GetOrders\"", "sales", "usp_GetOrders")]
    [InlineData("AcmeDb.dbo.usp_GetOrders", "dbo", "usp_GetOrders")]
    [InlineData("SVR.AcmeDb.dbo.usp_GetOrders", "dbo", "usp_GetOrders")]
    [InlineData("  dbo . usp_GetOrders ", "dbo", "usp_GetOrders")]
    public void Parses_qualified_names(string input, string schema, string name)
    {
        ObjectName parsed = ObjectName.Parse(input);

        Assert.Equal(schema, parsed.Schema);
        Assert.Equal(name, parsed.Name);
    }

    [Fact]
    public void Handles_escaped_closing_bracket_in_an_identifier()
    {
        ObjectName parsed = ObjectName.Parse("[dbo].[weird]]name]");

        Assert.Equal("weird]name", parsed.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.b.c.d.e")]
    [InlineData("dbo.")]
    [InlineData(".name")]
    public void Rejects_invalid_names(string input)
    {
        Assert.False(ObjectName.TryParse(input, out _));
    }

    [Fact]
    public void Bracketed_output_escapes_special_characters()
    {
        var name = new ObjectName("dbo", "a]b");

        Assert.Equal("[dbo].[a]]b]", name.Bracketed);
    }

    [Fact]
    public void Plain_output_round_trips_through_parse()
    {
        var original = new ObjectName("sales", "usp_X");

        Assert.Equal(original, ObjectName.Parse(original.Plain));
    }
}
