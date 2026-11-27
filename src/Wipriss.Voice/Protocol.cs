using System.Text.Json.Serialization;

namespace Wipriss.Voice;

// What Wipriss and Wipriss.Voice.exe say to each other. This file is compiled into both.
// Wipriss writes one VoiceRequest per line to stdin, the engine answers with VoiceReply lines on
// stdout. Closing stdin is always "quit": the microphone is closed and the process exits.

public enum VoiceCommand
{
    /// <summary>Download the model files that are missing or damaged, then exit.</summary>
    Download,
    /// <summary>Check the model and load it into memory (once per process).</summary>
    Load,
    /// <summary>Open the microphone and start collecting speech.</summary>
    Start,
    /// <summary>Close the microphone and recognize what was said.</summary>
    Stop,
    /// <summary>Close the microphone and throw the audio away.</summary>
    Cancel,
    Quit,
}

public enum VoiceReplyKind
{
    /// <summary>Download progress: <see cref="VoiceReply.Percent"/>.</summary>
    Progress,
    /// <summary>Download finished and every file matched its SHA-256.</summary>
    Downloaded,
    /// <summary>The model is loaded and the engine waits for "start".</summary>
    Ready,
    /// <summary>The microphone is open.</summary>
    Listening,
    /// <summary>While listening, about 10 times a second: loudness 0..100 in <see cref="VoiceReply.Percent"/>.</summary>
    Level,
    /// <summary>The microphone is closed, recognition is running.</summary>
    Recognizing,
    /// <summary>The recognized text (may be empty if nothing was said).</summary>
    Text,
    Error,
}

public enum VoiceError
{
    None,
    BadRequest,
    /// <summary>Some model file is missing or has the wrong size: download it first.</summary>
    ModelMissing,
    /// <summary>A model file's SHA-256 did not match; the file was deleted.</summary>
    ModelDamaged,
    Network,
    Disk,
    NoMicrophone,
    MicrophoneBusy,
    EngineFailed,
}

public sealed class VoiceRequest
{
    public VoiceCommand Command { get; set; }

    /// <summary>Folder that holds the model folders (Download, Load).</summary>
    public string? ModelsDir { get; set; }

    /// <summary>Load: exit after this many minutes without dictation, 0 = stay until quit.</summary>
    public int IdleMinutes { get; set; }
}

public sealed class VoiceReply
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public VoiceReplyKind Kind { get; set; }
    public int Percent { get; set; }
    public string? Text { get; set; }
    public VoiceError Error { get; set; }

    /// <summary>Seconds of audio that were recognized (Text), for the owner's statistics only.</summary>
    public double Seconds { get; set; }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(VoiceRequest))]
[JsonSerializable(typeof(VoiceReply))]
internal sealed partial class VoiceJson : JsonSerializerContext;
