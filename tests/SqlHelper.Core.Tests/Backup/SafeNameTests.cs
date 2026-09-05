using SqlHelper.Core.Backup;

namespace SqlHelper.Core.Tests.Backup;

public sealed class SafeNameTests
{
    [Theory]
    [InlineData("Fix tax rounding", "Fix-tax-rounding")]
    [InlineData("ORD-1487: fix / rounding", "ORD-1487-fix-rounding")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("", "unnamed")]
    [InlineData("   ", "unnamed")]
    public void Produces_a_filesystem_safe_slug(string input, string expected)
    {
        Assert.Equal(expected, SafeName.ForPath(input));
    }

    [Fact]
    public void Strips_characters_invalid_in_a_file_name()
    {
        string result = SafeName.ForPath("client<name>:\"weird\"|chars?*");

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(c, result);
        }
    }

    [Fact]
    public void Truncates_to_the_requested_length()
    {
        string result = SafeName.ForPath(new string('a', 200), maxLength: 20);

        Assert.True(result.Length <= 20);
    }

    [Fact]
    public void Timestamp_folder_is_sortable_and_filesystem_safe()
    {
        string stamp = SafeName.TimestampFolder(new DateTimeOffset(2026, 9, 4, 14, 30, 5, TimeSpan.Zero));

        Assert.Equal("2026-09-04_14-30-05", stamp);
    }
}
