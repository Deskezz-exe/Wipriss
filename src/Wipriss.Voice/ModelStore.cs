using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Wipriss.Voice;

/// <summary>One file of a speech model: where it comes from and what it must be.</summary>
internal sealed record ModelFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// The speech model on disk. Files come only from fixed HTTPS links pinned to one revision,
/// are written next to the target as ".part" and moved into place only after the size and
/// SHA-256 match. Before loading, every file is hashed again; a damaged file is deleted.
/// </summary>
internal sealed class ModelStore(string modelsDir, string folderName, IReadOnlyList<ModelFile> files)
{
    /// <summary>
    /// GigaAM v3 (Salute Developers, MIT) with punctuation and capital letters, RNN-T, exported to
    /// ONNX by the sherpa-onnx authors. Russian only.
    /// </summary>
    public static ModelStore GigaAmV3(string modelsDir) => new(modelsDir, "gigaam-v3-ru", GigaAmV3Files);

    private const string GigaAmBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16/resolve/a6039be7cee829a9044a69ac0ebaf1c191217c97/";

    internal static readonly ModelFile[] GigaAmV3Files =
    [
        new("encoder.int8.onnx", GigaAmBase + "encoder.int8.onnx", 224_570_820,
            "369f35a71bf288d3b8e0391fabd8dba5f2314088d440bca474056b7b4b6e66bf"),
        new("decoder.onnx", GigaAmBase + "decoder.onnx", 4_600_132,
            "38fc7475443ea2a26f63211ca350f73ac50fff824ab7a3876ee2bd610c53bbc4"),
        new("joiner.onnx", GigaAmBase + "joiner.onnx", 2_712_896,
            "602ff7017a93311aad34df1437c8d7f49911353c13d6eae7a6ee7b041339465c"),
        new("tokens.txt", GigaAmBase + "tokens.txt", 13_354,
            "39abae20e692998290c574e606f11a9edef2902a1995463fcff63d1490cf22b7"),
        new("LICENSE", GigaAmBase + "LICENSE", 1_070,
            "f00de6715714c7a63d08639cdbfaa40224eefc407302614bd19f1a8b98c875aa"),
    ];

    public string Folder { get; } = Path.Combine(modelsDir, folderName);

    public IReadOnlyList<ModelFile> Files => files;

    public long TotalSize => files.Sum(f => f.Size);

    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>Cheap check: every file is there with the right size (no hashing).</summary>
    public bool LooksInstalled() => files.All(f => SizeOf(PathOf(f.Name)) == f.Size);

    /// <summary>
    /// Hashes every file. Returns <see cref="VoiceError.None"/>, <see cref="VoiceError.ModelMissing"/>
    /// or <see cref="VoiceError.ModelDamaged"/> (the damaged files are deleted).
    /// </summary>
    public VoiceError Verify()
    {
        var result = VoiceError.None;
        foreach (var file in files)
        {
            var path = PathOf(file.Name);
            if (SizeOf(path) != file.Size) { result = Worse(result, VoiceError.ModelMissing); continue; }
            if (HashFile(path) == file.Sha256) continue;
            TryDelete(path);
            result = VoiceError.ModelDamaged;
        }
        return result;
    }

    /// <summary>
    /// Downloads every file that is missing or wrong. <paramref name="progress"/> gets 0..100 for the
    /// whole model. Throws <see cref="ModelDownloadException"/> with the reason.
    /// </summary>
    public async Task DownloadAsync(HttpClient http, Action<int> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        long total = TotalSize, done = 0;
        int lastPercent = -1;
        void Report(long bytes)
        {
            int percent = (int)(bytes * 100 / total);
            if (percent == lastPercent) return;
            lastPercent = percent;
            progress(percent);
        }

        foreach (var file in files)
        {
            var path = PathOf(file.Name);
            if (SizeOf(path) == file.Size && HashFile(path) == file.Sha256)
            {
                done += file.Size;
                Report(done);
                continue;
            }
            long start = done;
            await DownloadFileAsync(http, file, path, got => Report(start + got), cancel).ConfigureAwait(false);
            done += file.Size;
            Report(done);
        }
    }

    private static async Task DownloadFileAsync(HttpClient http, ModelFile file, string path,
        Action<long> got, CancellationToken cancel)
    {
        if (!file.Url.StartsWith("https://", StringComparison.Ordinal))
            throw new ModelDownloadException(VoiceError.BadRequest);
        var part = path + ".part";
        try
        {
            using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);
            // A redirect to plain HTTP is never followed by HttpClient; check the final address anyway.
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new ModelDownloadException(VoiceError.Network);
            if (response.Content.Headers.ContentLength is long length && length != file.Size)
                throw new ModelDownloadException(VoiceError.ModelDamaged);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                             1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    // Never write more than the file may be: a wrong or hostile answer stops here.
                    if (written > file.Size) throw new ModelDownloadException(VoiceError.ModelDamaged);
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    got(written);
                }
                if (written != file.Size) throw new ModelDownloadException(VoiceError.ModelDamaged);
            }
            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != file.Sha256)
                throw new ModelDownloadException(VoiceError.ModelDamaged);
            File.Move(part, path, overwrite: true);
        }
        catch (HttpRequestException) { throw new ModelDownloadException(VoiceError.Network); }
        catch (IOException) when (!cancel.IsCancellationRequested) { throw new ModelDownloadException(VoiceError.Disk); }
        catch (UnauthorizedAccessException) { throw new ModelDownloadException(VoiceError.Disk); }
        finally
        {
            TryDelete(part);
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static long SizeOf(string path)
    {
        var info = new FileInfo(path);
        // A link could point anywhere: model files must be plain files.
        if (!info.Exists || info.LinkTarget is not null) return -1;
        return info.Length;
    }

    private static VoiceError Worse(VoiceError a, VoiceError b) => a == VoiceError.ModelDamaged ? a : b;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ModelDownloadException(VoiceError error) : Exception(error.ToString())
{
    public VoiceError Error { get; } = error;
}
