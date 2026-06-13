using PstMigration.Application.Services;
using Xunit;

namespace PstMigration.Tests;

public class FolderNameNormalizerTests
{
    private readonly FolderNameNormalizer _sut = new();

    [Theory]
    [InlineData(null, "Unnamed Folder")]
    [InlineData("", "Unnamed Folder")]
    [InlineData("   ", "Unnamed Folder")]
    [InlineData("Inbox", "Inbox")]
    [InlineData("Customer/2024", "Customer_2024")]
    [InlineData("bad\\name", "bad_name")]
    [InlineData("trailing.", "trailing")]
    public void Normalize_returns_safe_name(string? input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input));
    }

    [Fact]
    public void Normalize_truncates_long_names()
    {
        var name = new string('a', 500);
        Assert.True(_sut.Normalize(name).Length <= 250);
    }
}
