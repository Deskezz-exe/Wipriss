using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using Wipriss.Core.Storage;
using Wipriss.Core.Text;
using Wipriss.Core.Timing;

namespace Wipriss.Tests;

public sealed class Sample
{
    public string Secret { get; set; } = "";
}

[JsonSerializable(typeof(Sample))]
internal sealed partial class TestJson : JsonSerializerContext;

public class CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wipriss-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(1, "заметка")]
    [InlineData(2, "заметки")]
    [InlineData(4, "заметки")]
    [InlineData(5, "заметок")]
    [InlineData(11, "заметок")]
    [InlineData(14, "заметок")]
    [InlineData(21, "заметка")]
    [InlineData(22, "заметки")]
    [InlineData(111, "заметок")]
    public void Russian_plural(int n, string expected) =>
        Assert.Equal(expected, RuText.Plural(n, "заметка", "заметки", "заметок"));

    [Fact]
    public void Secure_store_round_trip_and_file_is_not_plain_text()
    {
        var path = Path.Combine(_dir, "sample.dat");
        SecureStore.WriteJson(path, new Sample { Secret = "пароль-от-почты-123" }, TestJson.Default.Sample);

        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("пароль", Encoding.UTF8.GetString(raw));
        Assert.Equal("пароль-от-почты-123", SecureStore.ReadJson(path, TestJson.Default.Sample)?.Secret);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Damaged_file_is_kept_aside_and_reads_as_empty()
    {
        var path = Path.Combine(_dir, "sample.dat");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Null(SecureStore.ReadJson(path, TestJson.Default.Sample));
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Missing_file_reads_as_empty() =>
        Assert.Null(SecureStore.ReadJson(Path.Combine(_dir, "none.dat"), TestJson.Default.Sample));

    [Fact]
    public async Task Absolute_timer_fires_at_the_moment_and_can_be_moved()
    {
        var fired = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new AbsoluteTimer(() => fired.TrySetResult(DateTime.UtcNow));

        timer.Schedule(DateTime.UtcNow.AddHours(1));    // far away...
        var due = DateTime.UtcNow.AddMilliseconds(300); // ...then moved closer
        timer.Schedule(due);

        var at = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(at >= due.AddMilliseconds(-20), "fired too early");
        Assert.True(at <= due.AddSeconds(1), "fired too late");
    }

    [Fact]
    public async Task Cancelled_timer_does_not_fire()
    {
        var fired = false;
        using var timer = new AbsoluteTimer(() => fired = true);
        timer.Schedule(DateTime.UtcNow.AddMilliseconds(200));
        timer.Cancel();
        await Task.Delay(600);
        Assert.False(fired);
    }
}
