using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wipriss.Core.Storage;

/// <summary>
/// Saves module data encrypted with Windows DPAPI: only the current Windows user on this PC can read it.
/// Writes are atomic (temp file + rename), so a crash never leaves a half-written file.
/// </summary>
public static class SecureStore
{
    // Not a secret (the code is open): it only separates Wipriss data from other DPAPI users.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Wipriss.LocalData.v1");

    public static void WriteBytes(string path, byte[] plain)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns null when the file is missing. Throws when it exists but cannot be decrypted.</summary>
    public static byte[]? ReadBytes(string path)
    {
        if (!File.Exists(path)) return null;
        return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
    }

    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> type) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, type));

    /// <summary>
    /// Reads a JSON file written by <see cref="WriteJson{T}"/>. A damaged file is kept as *.broken
    /// and null is returned, so the module starts empty instead of crashing.
    /// </summary>
    public static T? ReadJson<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            var bytes = ReadBytes(path);
            return bytes is null ? null : JsonSerializer.Deserialize(bytes, type);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { }
            return null;
        }
    }
}
