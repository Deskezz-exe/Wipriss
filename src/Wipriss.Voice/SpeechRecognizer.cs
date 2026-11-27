using System.Text;
using SherpaOnnx;

namespace Wipriss.Voice;

/// <summary>GigaAM v3 through sherpa-onnx on the processor. One instance per process.</summary>
internal sealed class SpeechRecognizer : IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    public SpeechRecognizer(ModelStore model)
    {
        // sherpa-onnx's C# layer passes paths as ANSI strings, which breaks Cyrillic folder names
        // ("Служебное"). The model folder becomes the current one and only plain ASCII file names
        // cross the boundary.
        Environment.CurrentDirectory = model.Folder;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = "encoder.int8.onnx";
        config.ModelConfig.Transducer.Decoder = "decoder.onnx";
        config.ModelConfig.Transducer.Joiner = "joiner.onnx";
        config.ModelConfig.Tokens = "tokens.txt";
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Provider = "cpu";
        // Half of the cores at most: the owner keeps working while the phrase is recognized.
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    /// <summary>Recognizes 16 kHz mono audio. Returns "" for silence.</summary>
    public string Recognize(float[] samples)
    {
        var pieces = SpeechChunker.Split(samples);
        if (pieces.Count == 0) return "";
        var streams = new List<OfflineStream>(pieces.Count);
        try
        {
            foreach (var (start, length) in pieces)
            {
                var stream = _recognizer.CreateStream();
                streams.Add(stream);
                stream.AcceptWaveform(Microphone.SampleRate, samples.AsSpan(start, length).ToArray());
            }
            _recognizer.Decode(streams);
            var text = new StringBuilder();
            foreach (var stream in streams)
            {
                var part = stream.Result.Text.Trim();
                if (part.Length == 0) continue;
                if (text.Length > 0) text.Append(' ');
                text.Append(part);
            }
            return text.ToString();
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    public void Dispose() => _recognizer.Dispose();
}
