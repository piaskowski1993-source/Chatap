namespace Chatap.Core.Tests;

public class LogWriterTests
{
    static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "chatap-test-" + Guid.NewGuid());

    static readonly DateTimeOffset Morning = new(2026, 10, 13, 9, 41, 7, TimeSpan.Zero);

    static string LogFile(string folder, string day, string file) => Path.Combine(folder, "logs", day, file);

    [Fact]
    public async Task Many_producers_at_the_same_time_lose_no_line_and_mix_no_line()
    {
        string folder = NewTempFolder();
        var log = new LogWriter(folder, now: () => Morning);

        // 10 producers on different threads, 100 lines each, all at the same time.
        var producers = Enumerable.Range(0, 10).Select(p => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                log.Log($"producer {p} line {i}");
        }));
        await Task.WhenAll(producers);
        await log.DisposeAsync();

        string[] lines = File.ReadAllLines(LogFile(folder, "2026-10-13", "node.txt"));
        var expected = from p in Enumerable.Range(0, 10)
                       from i in Enumerable.Range(0, 100)
                       select $"2026-10-13T09:41:07Z  producer {p} line {i}";
        Assert.Equal(1000, lines.Length);
        Assert.Equal(expected.Order(), lines.Order());
    }

    [Fact]
    public async Task Graceful_shutdown_writes_every_line_still_in_the_queue()
    {
        var written = new List<string>();
        var log = new LogWriter("unused", append: async (path, text) =>
        {
            await Task.Delay(2); // a slow disk: lines wait in the queue
            written.Add(text);
        });

        for (int i = 0; i < 50; i++)
            log.Log($"line {i}");
        await log.DisposeAsync();

        Assert.Equal(50, written.Count);
    }

    [Fact]
    public async Task Disk_full_informs_once_and_the_program_continues()
    {
        var failures = new List<Exception>();
        var log = new LogWriter("unused", append: (path, text) => throw new IOException("No space left on device"));
        log.LogFailed += failures.Add;

        log.Log("one");
        log.Log("two");
        log.Log("three");
        await log.DisposeAsync(); // must not throw

        var failure = Assert.Single(failures);
        Assert.Contains("No space left", failure.Message);
    }

    [Fact]
    public async Task Node_lines_and_conversation_lines_go_to_their_own_files()
    {
        string folder = NewTempFolder();
        var peer = Guid.NewGuid();
        var log = new LogWriter(folder, now: () => Morning);

        log.Log("started");
        log.Log(peer, "hello from peer");
        await log.DisposeAsync();

        Assert.Equal("2026-10-13T09:41:07Z  started\n", File.ReadAllText(LogFile(folder, "2026-10-13", "node.txt")));
        Assert.Equal("2026-10-13T09:41:07Z  hello from peer\n", File.ReadAllText(LogFile(folder, "2026-10-13", $"{peer}.txt")));
    }

    [Fact]
    public async Task A_new_UTC_day_writes_into_a_new_folder()
    {
        string folder = NewTempFolder();
        var clock = new DateTimeOffset(2026, 10, 13, 23, 59, 59, TimeSpan.Zero);
        var log = new LogWriter(folder, now: () => clock);

        log.Log("before midnight");
        clock = clock.AddSeconds(2);
        log.Log("after midnight");
        await log.DisposeAsync();

        Assert.Contains("before midnight", File.ReadAllText(LogFile(folder, "2026-10-13", "node.txt")));
        Assert.Contains("after midnight", File.ReadAllText(LogFile(folder, "2026-10-14", "node.txt")));
    }
}
