using System.Text;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class LibraryBackupServiceTests
{
    [Fact]
    public async Task ExportAndImport_RoundTripsCatalogue()
    {
        Audiobook[] books =
        [
            new("one", "Book One", "Author One", "Narrator", "8h", "Manually added"),
            new("two", "Book Two", "Author Two", "", "", "Demo")
        ];
        await using var stream = new MemoryStream();

        await LibraryBackupService.ExportAsync(stream, books);
        stream.Position = 0;
        var restored = await LibraryBackupService.ImportAsync(stream);

        Assert.Equal(books, restored);
    }

    [Fact]
    public async Task Import_RejectsUnsupportedVersion()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"FormatVersion":99,"CreatedAtUtc":"2026-01-01T00:00:00Z","Books":[{"Id":"1","Title":"T","Author":"A","Narrator":"","Length":"","Status":"Demo"}]}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryBackupService.ImportAsync(stream));
    }

    [Fact]
    public async Task Import_RejectsDuplicateIds()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"FormatVersion":1,"CreatedAtUtc":"2026-01-01T00:00:00Z","Books":[{"Id":"1","Title":"T","Author":"A","Narrator":"","Length":"","Status":"Demo"},{"Id":"1","Title":"T2","Author":"A2","Narrator":"","Length":"","Status":"Demo"}]}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryBackupService.ImportAsync(stream));
    }

    [Fact]
    public async Task Import_RejectsEmptyBackup()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"FormatVersion":1,"CreatedAtUtc":"2026-01-01T00:00:00Z","Books":[]}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryBackupService.ImportAsync(stream));
    }

    [Fact]
    public async Task Export_RejectsEmptyCatalogue()
    {
        await using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => LibraryBackupService.ExportAsync(stream, []));
    }
}
