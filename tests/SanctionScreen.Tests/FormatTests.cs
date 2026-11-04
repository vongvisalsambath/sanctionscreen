using System.Text;
using SanctionScreen.Cli;
using Xunit;

namespace SanctionScreen.Tests;

public sealed class FormatTests
{
    private static readonly WatchList List = Ofac.LoadFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures"));

    [Fact]
    public async Task TheJsonFormRoundTrips_SyncAndAsync()
    {
        var bytes = new MemoryStream();
        ListJson.Save(List, bytes);
        var a = ListJson.Load(new MemoryStream(bytes.ToArray()));
        var b = await ListJson.LoadAsync(new MemoryStream(bytes.ToArray()));
        foreach (var l in new[] { a, b })
        {
            Assert.Equal((List.Source, List.Published, List.Entries.Count), (l.Source, l.Published, l.Entries.Count));
            Assert.Equal(List.Entries.Select(e => (e.Id, e.Type, string.Join("|", e.Names), string.Join("|", e.Programs), string.Join("|", e.Countries), string.Join("|", e.BirthYears))),
                         l.Entries.Select(e => (e.Id, e.Type, string.Join("|", e.Names), string.Join("|", e.Programs), string.Join("|", e.Countries), string.Join("|", e.BirthYears))));
        }
        // the same hits from either form
        Assert.Equal(new Screener(List).Screen(new Query("Jonathon Doe")).Select(h => (h.Entry.Id, h.Score)), new Screener(a).Screen(new Query("Jonathon Doe")).Select(h => (h.Entry.Id, h.Score)));
    }

    [Theory]
    [InlineData("{}", "no entries")]
    [InlineData("{\"entries\":[{\"id\":\"1\",\"type\":\"dragon\",\"names\":[[\"A\",\"primary\"]]}]}", "unknown type")]
    [InlineData("{\"entries\":[{\"id\":\"1\",\"type\":\"entity\",\"names\":[]}]}", "no id or no names")]
    [InlineData("{\"entries\":[{\"id\":\"1\",\"type\":\"entity\",\"names\":[[\"A\"]]}]}", "no id or no names")]
    [InlineData("not json", "isn't valid")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]", "isn't valid")]
    public async Task BadJsonIsRefused(string json, string reason)
    {
        Assert.Contains(reason, Assert.Throws<ListFormatException>(() => ListJson.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)))).Message);
        Assert.Contains(reason, (await Assert.ThrowsAsync<ListFormatException>(() => ListJson.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json))))).Message);
    }

    [Fact]
    public async Task AnOversizedListIsRefusedWhileReading()
    {
        // a stream that never ends and can't say its length
        var endless = new EndlessStream();
        Assert.Contains("larger than", (await Assert.ThrowsAsync<ListFormatException>(() => ListJson.LoadAsync(endless))).Message);
    }

    private sealed class EndlessStream : Stream
    {
        private static readonly byte[] Chunk = Encoding.ASCII.GetBytes("{\"source\":\"" + new string('x', 8000));
        private bool first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (first) { first = false; var n = Math.Min(count, 11); Array.Copy(Chunk, 0, buffer, offset, n); return n; }
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1+1", "\"'-1+1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("Banco Nacional de Cuba", "\"Banco Nacional de Cuba\"")]
    [InlineData("a\u202Eb", "\"a\\u202Eb\"")]
    public void CsvCellsCantBecomeFormulas(string text, string cell) => Assert.Equal(cell, Csv.Cell(text));

    [Fact]
    public void CsvLinesAreSplitWithQuotesAndBounded()
    {
        Assert.Equal(["Example Import, Export", "entity", "", "KH"], Csv.Split("\"Example Import, Export\",entity,,KH"));
        Assert.Equal(["say \"hi\""], Csv.Split("\"say \"\"hi\"\"\""));
        Assert.Equal(20, Csv.Split(string.Concat(Enumerable.Repeat("a,", 100))).Count);
    }
}
