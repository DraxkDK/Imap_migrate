using PstMigration.PstParser;
using Xunit;

namespace PstMigration.Tests;

public class MockPstParserTests
{
    private readonly MockPstParser _sut = new();

    [Fact]
    public async Task InspectAsync_returns_counts()
    {
        var info = await _sut.InspectAsync("mock.pst", CancellationToken.None);
        Assert.True(info.FolderCount > 0);
        Assert.True(info.MailCount > 0);
        Assert.True(info.IsUnicode);
        Assert.False(info.IsCorrupted);
    }

    [Fact]
    public async Task ReadFolders_then_mail_yields_items()
    {
        var folderIds = new List<string>();
        await foreach (var f in _sut.ReadFoldersAsync("mock.pst", CancellationToken.None))
            folderIds.Add(f.SourceFolderId);

        Assert.NotEmpty(folderIds);

        var mail = new List<string>();
        await foreach (var m in _sut.ReadMailItemsAsync("mock.pst", folderIds[0], CancellationToken.None))
            mail.Add(m.SourceItemId);

        Assert.NotEmpty(mail);
        Assert.All(mail, id => Assert.StartsWith(folderIds[0], id));
    }

    [Fact]
    public async Task Attachment_stream_is_readable()
    {
        await foreach (var m in _sut.ReadMailItemsAsync("mock.pst", "Inbox", CancellationToken.None))
        {
            if (m.Attachments.Count == 0) continue;
            await using var stream = await m.Attachments[0].OpenReadAsync(CancellationToken.None);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            Assert.False(string.IsNullOrEmpty(content));
            return;
        }
        Assert.Fail("Expected at least one attachment in the mock data.");
    }
}
