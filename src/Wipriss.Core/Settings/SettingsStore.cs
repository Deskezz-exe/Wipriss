using System.IO;
using System.Text.Json;

namespace Wipriss.Core.Settings;

/// <summary>Loads and saves settings as JSON. Saves are atomic, so a crash never leaves a half-written file.</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; }

    /// <summary>Raised on the caller's thread after every change.</summary>
    public event Action? Changed;

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
        Changed?.Invoke();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the broken file for inspection and start with defaults.
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch (IOException) { }
        }

        return new AppSettings();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        File.Move(temp, _path, overwrite: true);
    }
}
