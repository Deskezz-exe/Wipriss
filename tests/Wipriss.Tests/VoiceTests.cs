using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wipriss.Voice;
using Xunit;

namespace Wipriss.Tests;

public sealed class VoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wipriss-voice-" + Guid.NewGuid().ToString("N"));

    public VoiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // --- cutting long recordings ---

    [Fact]
    public void Chunker_SilenceGivesNothing()
    {
        Assert.Empty(SpeechChunker.Split(new float[16000 * 5]));
        Assert.True(SpeechChunker.IsSilence(Noise(16000, 0.001f)));
    }

    [Fact]
    public void Chunker_ShortPhraseIsOnePiece()
    {
        var samples = Noise(16000 * 8, 0.3f);
        var pieces = SpeechChunker.Split(samples);
        Assert.Equal([(0, samples.Length)], pieces);
    }

    [Fact]
    public void Chunker_CutsLongSpeechInQuietPlaces()
    {
        const int rate = 16000;
        var samples = Noise(rate * 50, 0.3f);
        Quiet(samples, 15 * rate, rate / 2);   // a pause at 15 s
        Quiet(samples, 31 * rate, rate / 2);   // and at 31 s

        var pieces = SpeechChunker.Split(samples);

        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, p => Assert.True(p.Length <= SpeechChunker.MaxSeconds * rate));
        Assert.InRange(pieces[1].Start, 15 * rate, 15 * rate + rate / 2);
        Assert.InRange(pieces[2].Start, 31 * rate, 31 * rate + rate / 2);
        // Nothing lost, nothing twice.
        Assert.Equal(0, pieces[0].Start);
        for (int i = 1; i < pieces.Count; i++)
            Assert.Equal(pieces[i - 1].Start + pieces[i - 1].Length, pieces[i].Start);
        Assert.Equal(samples.Length, pieces[^1].Start + pieces[^1].Length);
    }

    [Fact]
    public void Chunker_WithoutPausesStillCutsAtTheLimit()
    {
        var pieces = SpeechChunker.Split(Noise(16000 * 61, 0.3f));
        Assert.All(pieces, p => Assert.True(p.Length <= SpeechChunker.MaxSeconds * 16000));
        Assert.Equal(16000 * 61, pieces.Sum(p => p.Length));
    }

    [Fact]
    public void Level_FollowsLoudness()
    {
        Assert.Equal(0, Microphone.LevelOf([]));
        Assert.Equal(0, Microphone.LevelOf(new float[1600]));
        Assert.Equal(0, Microphone.LevelOf(Noise(1600, 0.001f)));   // room hum
        Assert.Equal(100, Microphone.LevelOf(Noise(1600, 0.9f)));   // shouting
        int normal = Microphone.LevelOf(Noise(1600, 0.05f));        // ordinary speech
        Assert.InRange(normal, 30, 90);
    }

    // --- the model on disk ---

    [Fact]
    public void Store_ReportsMissingAndDeletesDamagedFiles()
    {
        var good = Encoding.UTF8.GetBytes("good model");
        var store = Store(("a.bin", good), ("b.bin", good));
        Assert.Equal(VoiceError.ModelMissing, store.Verify());
        Assert.False(store.LooksInstalled());

        Directory.CreateDirectory(store.Folder);
        File.WriteAllBytes(store.PathOf("a.bin"), good);
        File.WriteAllBytes(store.PathOf("b.bin"), Encoding.UTF8.GetBytes("evil model")); // same size, other bytes
        Assert.True(store.LooksInstalled());
        Assert.Equal(VoiceError.ModelDamaged, store.Verify());
        Assert.False(File.Exists(store.PathOf("b.bin")));
        Assert.True(File.Exists(store.PathOf("a.bin")));

        File.WriteAllBytes(store.PathOf("b.bin"), good);
        Assert.Equal(VoiceError.None, store.Verify());
    }

    [Fact]
    public async Task Store_DownloadsAndChecksEveryFile()
    {
        var a = Encoding.UTF8.GetBytes(new string('a', 200_000));
        var b = Encoding.UTF8.GetBytes("tokens");
        var store = Store(("a.bin", a), ("b.txt", b));
        using var http = new HttpClient(new FakeServer(name => name == "a.bin" ? a : b));
        var seen = new List<int>();

        await store.DownloadAsync(http, seen.Add, CancellationToken.None);

        Assert.Equal(VoiceError.None, store.Verify());
        Assert.Equal(100, seen[^1]);
        Assert.Equal(seen.OrderBy(p => p), seen);
        Assert.Empty(Directory.GetFiles(store.Folder, "*.part"));
    }

    [Theory]
    [InlineData("other bytes")]  // same length, wrong hash
    [InlineData("too long!!!!")] // longer than it may be
    [InlineData("short")]
    public async Task Store_RejectsWrongDownloads(string served)
    {
        var store = Store(("a.bin", Encoding.UTF8.GetBytes("right bytes")));
        using var http = new HttpClient(new FakeServer(_ => Encoding.UTF8.GetBytes(served), sendLength: false));

        var error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => store.DownloadAsync(http, _ => { }, CancellationToken.None));

        Assert.Equal(VoiceError.ModelDamaged, error.Error);
        Assert.Empty(Directory.GetFiles(store.Folder)); // neither the file nor the .part stays
    }

    [Fact]
    public async Task Store_RefusesPlainHttpAndServerErrors()
    {
        var plain = new ModelStore(_dir, "m", [new ModelFile("a.bin", "http://example.invalid/a.bin", 1, new string('0', 64))]);
        using var http = new HttpClient(new FakeServer(_ => [1]));
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => plain.DownloadAsync(http, _ => { }, default));
        Assert.Equal(VoiceError.BadRequest, error.Error);

        var store = Store(("a.bin", [1, 2, 3]));
        using var failing = new HttpClient(new FakeServer(_ => null));
        error = await Assert.ThrowsAsync<ModelDownloadException>(() => store.DownloadAsync(failing, _ => { }, default));
        Assert.Equal(VoiceError.Network, error.Error);
    }

    [Fact]
    public void GigaAmLinks_ArePinnedHttpsWithHashes()
    {
        Assert.All(ModelStore.GigaAmV3Files, f =>
        {
            Assert.StartsWith("https://huggingface.co/", f.Url);
            Assert.Contains("/resolve/a6039be7cee829a9044a69ac0ebaf1c191217c97/", f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Size > 0);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relative\models")]
    [InlineData(@"\\server\share\models")]
    public void ModelsFolder_MustBeAFullLocalPath(string? dir) => Assert.Null(Program.OpenStore(dir));

    [Fact]
    public void ModelsFolder_KeepsTheModelInItsOwnSubfolder()
    {
        var store = Program.OpenStore(@"D:\Profile\Wipriss\Служебное\Модели");
        Assert.Equal(@"D:\Profile\Wipriss\Служебное\Модели\gigaam-v3-ru", store!.Folder);
    }

    // --- protocol ---

    [Fact]
    public void Protocol_RoundTripsAndAlwaysNamesTheKind()
    {
        var json = JsonSerializer.Serialize(new VoiceReply { Kind = VoiceReplyKind.Progress, Percent = 0 }, VoiceJson.Default.VoiceReply);
        Assert.Contains("\"Kind\":\"Progress\"", json);

        var request = JsonSerializer.Deserialize("{\"Command\":\"Load\",\"ModelsDir\":\"D:\\\\M\",\"IdleMinutes\":5}",
            VoiceJson.Default.VoiceRequest)!;
        Assert.Equal(VoiceCommand.Load, request.Command);
        Assert.Equal(@"D:\M", request.ModelsDir);
        Assert.Equal(5, request.IdleMinutes);

        var text = JsonSerializer.Serialize(new VoiceReply { Kind = VoiceReplyKind.Text, Text = "Привет, мир." }, VoiceJson.Default.VoiceReply);
        Assert.Equal("Привет, мир.", JsonSerializer.Deserialize(text, VoiceJson.Default.VoiceReply)!.Text);
    }

    // --- the real model (only where it was downloaded) ---

    /// <summary>
    /// Runs only when WIPRISS_VOICE_MODELS points at a folder with the downloaded model and
    /// WIPRISS_VOICE_WAV at a 16 kHz mono 16-bit WAV; otherwise there is nothing to check.
    /// </summary>
    [Fact]
    public void RealModel_RecognizesRussianSpeech()
    {
        var models = Environment.GetEnvironmentVariable("WIPRISS_VOICE_MODELS");
        var wav = Environment.GetEnvironmentVariable("WIPRISS_VOICE_WAV");
        if (string.IsNullOrEmpty(models) || string.IsNullOrEmpty(wav)) return;

        var store = Program.OpenStore(models)!;
        Assert.Equal(VoiceError.None, store.Verify());
        var keep = Environment.CurrentDirectory;
        try
        {
            using var recognizer = new SpeechRecognizer(store);
            var text = recognizer.Recognize(ReadWav(wav));
            Assert.Matches("[А-Яа-яЁё]{3,}", text);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "wipriss-voice-result.txt"), text);
        }
        finally
        {
            Environment.CurrentDirectory = keep;
        }
    }

    private ModelStore Store(params (string Name, byte[] Bytes)[] files) =>
        new(_dir, "model", files.Select(f => new ModelFile(f.Name, "https://example.invalid/" + f.Name,
            f.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(f.Bytes)))).ToArray());

    private static float[] Noise(int n, float level)
    {
        var random = new Random(7);
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = (float)(random.NextDouble() * 2 - 1) * level;
        return samples;
    }

    private static void Quiet(float[] samples, int from, int length) => Array.Clear(samples, from, length);

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int data = 12;
        while (Encoding.ASCII.GetString(bytes, data, 4) != "data") data += 8 + BitConverter.ToInt32(bytes, data + 4);
        int length = BitConverter.ToInt32(bytes, data + 4) / 2;
        var samples = new float[length];
        for (int i = 0; i < length; i++) samples[i] = BitConverter.ToInt16(bytes, data + 8 + i * 2) / 32768f;
        return samples;
    }

    /// <summary>Answers every request with the bytes for the file name, or 404 for null.</summary>
    private sealed class FakeServer(Func<string, byte[]?> serve, bool sendLength = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var bytes = serve(request.RequestUri!.Segments[^1]);
            if (bytes is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            HttpContent content = sendLength ? new ByteArrayContent(bytes) : new StreamContent(new MemoryStream(bytes));
            if (!sendLength) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}
