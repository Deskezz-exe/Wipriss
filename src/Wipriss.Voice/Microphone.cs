using System.Runtime.InteropServices;

namespace Wipriss.Voice;

/// <summary>
/// Records the default microphone as 16 kHz mono through the Windows waveIn API (the audio engine
/// converts from the device's own format). No library, no polling: a worker thread sleeps on an
/// event that Windows sets when a 100 ms buffer is full. The device is open only between
/// <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
internal sealed unsafe partial class Microphone : IDisposable
{
    public const int SampleRate = 16000;
    private const int BufferSamples = SampleRate / 10; // 100 ms
    private const int BufferCount = 4;

    private readonly int _maxSamples;
    private readonly Lock _gate = new();
    private nint _device;
    private WaveHeader* _headers;
    private short* _memory;
    private nint _event;
    private Thread? _worker;
    private volatile bool _recording;
    private float[] _samples = [];
    private int _count;
    private int _next;

    /// <param name="maxSeconds">Recording stops by itself after this long.</param>
    /// <param name="limitReached">Called on the worker thread when the limit stops the recording.</param>
    /// <param name="level">Called on the worker thread about 10 times a second with the loudness, 0..100.</param>
    public Microphone(int maxSeconds, Action? limitReached = null, Action<int>? level = null)
    {
        _maxSamples = maxSeconds * SampleRate;
        LimitReached = limitReached;
        Level = level;
    }

    private Action? LimitReached { get; }

    private Action<int>? Level { get; }

    /// <summary>Loudness of the newest audio for the waves on screen: -50 dBFS → 0, -10 dBFS → 100.</summary>
    internal static int LevelOf(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        double rms = Math.Sqrt(sum / samples.Length);
        if (rms <= 0) return 0;
        double db = 20 * Math.Log10(rms);
        return (int)Math.Round(Math.Clamp((db + 50) / 40, 0, 1) * 100);
    }

    public bool IsRecording => _recording;

    /// <summary>Opens the microphone. Returns <see cref="VoiceError.None"/> or why it could not.</summary>
    public VoiceError Start()
    {
        lock (_gate)
        {
            if (_recording) return VoiceError.None;
            _count = 0;
            _next = 0;
            if (_samples.Length == 0) _samples = new float[SampleRate * 30];

            var format = new WaveFormat
            {
                FormatTag = 1, // PCM
                Channels = 1,
                SamplesPerSec = SampleRate,
                AvgBytesPerSec = SampleRate * 2,
                BlockAlign = 2,
                BitsPerSample = 16,
            };
            _event = CreateEventW(0, 0, 0, 0);
            if (_event == 0) return VoiceError.EngineFailed;

            int result = waveInOpen(out _device, WaveMapper, ref format, _event, 0, CallbackEvent);
            if (result != 0)
            {
                _device = 0;
                CloseEvent();
                return result switch
                {
                    MmAllocated => VoiceError.MicrophoneBusy,
                    _ => VoiceError.NoMicrophone,
                };
            }

            _memory = (short*)NativeMemory.AllocZeroed((nuint)(BufferSamples * BufferCount), sizeof(short));
            _headers = (WaveHeader*)NativeMemory.AllocZeroed(BufferCount, (nuint)sizeof(WaveHeader));
            for (int i = 0; i < BufferCount; i++)
            {
                var header = &_headers[i];
                header->Data = (nint)(_memory + i * BufferSamples);
                header->BufferLength = BufferSamples * sizeof(short);
                if (waveInPrepareHeader(_device, header, sizeof(WaveHeader)) != 0 ||
                    waveInAddBuffer(_device, header, sizeof(WaveHeader)) != 0)
                {
                    CloseDevice();
                    return VoiceError.NoMicrophone;
                }
            }
            if (waveInStart(_device) != 0)
            {
                CloseDevice();
                return VoiceError.NoMicrophone;
            }
            _recording = true;
            _worker = new Thread(Collect) { IsBackground = true, Name = "Microphone" };
            _worker.Start();
            return VoiceError.None;
        }
    }

    /// <summary>Closes the microphone and returns what was recorded (16 kHz mono, -1..1).</summary>
    public float[] Stop()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_device == 0) return [];
            _recording = false;
            worker = _worker;
            _worker = null;
        }
        SetEvent(_event);
        // The limit callback runs on the worker itself and may call Stop: it is about to return.
        if (worker is not null && worker != Thread.CurrentThread) worker.Join();
        lock (_gate)
        {
            waveInReset(_device); // hands back the buffers that are still queued, with what they hold
            Drain(requeue: false);
            CloseDevice();
            var result = _samples.AsSpan(0, _count).ToArray();
            _count = 0;
            return result;
        }
    }

    public void Dispose()
    {
        Stop();
        _samples = [];
    }

    private void Collect()
    {
        while (_recording)
        {
            WaitForSingleObject(_event, Infinite);
            bool full;
            int level = -1;
            lock (_gate)
            {
                if (!_recording) return;
                int before = _count;
                Drain(requeue: true);
                full = _count >= _maxSamples;
                if (Level is not null && _count > before) level = LevelOf(_samples.AsSpan(before, _count - before));
            }
            if (level >= 0) Level!(level);
            if (full)
            {
                LimitReached?.Invoke();
                return;
            }
        }
    }

    /// <summary>Copies every finished buffer and, while recording, gives it back.</summary>
    private void Drain(bool requeue)
    {
        // waveIn fills buffers strictly in the order they were queued, so they finish in a circle.
        for (int taken = 0; taken < BufferCount; taken++)
        {
            var header = &_headers[_next];
            if ((header->Flags & HeaderDone) == 0) break;
            Append((short*)header->Data, (int)header->BytesRecorded / sizeof(short));
            header->Flags &= ~HeaderDone;
            header->BytesRecorded = 0;
            if (requeue && _recording) waveInAddBuffer(_device, header, sizeof(WaveHeader));
            _next = (_next + 1) % BufferCount;
        }
    }

    private void Append(short* data, int n)
    {
        n = Math.Min(n, _maxSamples - _count);
        if (n <= 0) return;
        if (_count + n > _samples.Length)
            Array.Resize(ref _samples, Math.Min(_maxSamples, Math.Max(_samples.Length * 2, _count + n)));
        for (int i = 0; i < n; i++) _samples[_count + i] = data[i] / 32768f;
        _count += n;
    }

    private void CloseDevice()
    {
        if (_device != 0)
        {
            waveInReset(_device);
            for (int i = 0; i < BufferCount && _headers != null; i++)
                waveInUnprepareHeader(_device, &_headers[i], sizeof(WaveHeader));
            waveInClose(_device);
            _device = 0;
        }
        if (_headers != null) { NativeMemory.Free(_headers); _headers = null; }
        if (_memory != null) { NativeMemory.Free(_memory); _memory = null; }
        CloseEvent();
    }

    private void CloseEvent()
    {
        if (_event != 0) { CloseHandle(_event); _event = 0; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    private const uint WaveMapper = unchecked((uint)-1);
    private const uint CallbackEvent = 0x00050000;
    private const uint HeaderDone = 0x00000001;
    private const int MmAllocated = 4;
    private const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("winmm.dll")]
    private static partial int waveInOpen(out nint device, uint deviceId, ref WaveFormat format,
        nint callback, nint instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static partial int waveInPrepareHeader(nint device, WaveHeader* header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveInUnprepareHeader(nint device, WaveHeader* header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveInAddBuffer(nint device, WaveHeader* header, int size);

    [LibraryImport("winmm.dll")]
    private static partial int waveInStart(nint device);

    [LibraryImport("winmm.dll")]
    private static partial int waveInReset(nint device);

    [LibraryImport("winmm.dll")]
    private static partial int waveInClose(nint device);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
