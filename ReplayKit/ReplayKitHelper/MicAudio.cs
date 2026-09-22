using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReplayKitHelper
{
    internal sealed class MicDeviceInfo
    {
        public string Id;
        public string Name;
    }

    internal sealed class MicLevel
    {
        public double Peak;
        public double GatePeak;
        public double Rms;
    }

    internal sealed class MicAudioException : Exception
    {
        public MicAudioException(string message) : base(message) { }
    }

    // wasapi capture endpoints as obs sees them: the endpoint id is the exact string obs stores as device_id, and the name is the same friendly name obs and the windows sound panel show.
    internal static class MicDevices
    {
        public const string DefaultId = "default";

        private static readonly Regex EndpointIdRe = new Regex(@"^\{0\.0\.1\.00000000\}\.\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$", RegexOptions.Compiled);

        public static bool IsValidId(string id) => id != null && (id == DefaultId || EndpointIdRe.IsMatch(id));

        // the vb-cable endpoints replaykit installs for discord share audio are inputs to windows too, but never a real microphone.
        private static bool IsReplaykitCable(string name) =>
            name.IndexOf("OBS Stream Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CABLE Out 16ch", StringComparison.OrdinalIgnoreCase);

        public static List<MicDeviceInfo> ListActive()
        {
            var result = new List<MicDeviceInfo>();
            WasapiInterop.IMMDeviceEnumerator enumerator = null;
            WasapiInterop.IMMDeviceCollection devices = null;
            try
            {
                enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumeratorObject();
                WasapiInterop.Check(enumerator.EnumAudioEndpoints(WasapiInterop.ECapture, WasapiInterop.DeviceStateActive, out devices), "list microphones");
                WasapiInterop.Check(devices.GetCount(out int count), "count microphones");
                for (int i = 0; i < count; i++)
                {
                    WasapiInterop.IMMDevice device = null;
                    try
                    {
                        if (devices.Item(i, out device) < 0 || device == null) continue;
                        if (device.GetId(out string id) < 0 || string.IsNullOrEmpty(id)) continue;
                        string name = WasapiInterop.ReadFriendlyName(device) ?? id;
                        if (IsReplaykitCable(name)) continue;
                        result.Add(new MicDeviceInfo { Id = id, Name = name });
                    }
                    finally { WasapiInterop.Release(device); }
                }
            }
            finally
            {
                WasapiInterop.Release(devices);
                WasapiInterop.Release(enumerator);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // obs resolves "default" through the communications role for inputs, so the label has to follow that role too or it names a different device than the one being recorded.
        public static string DefaultDeviceName()
        {
            WasapiInterop.IMMDeviceEnumerator enumerator = null;
            WasapiInterop.IMMDevice device = null;
            try
            {
                enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumeratorObject();
                if (enumerator.GetDefaultAudioEndpoint(WasapiInterop.ECapture, WasapiInterop.ECommunications, out device) < 0 || device == null) return null;
                return WasapiInterop.ReadFriendlyName(device);
            }
            finally
            {
                WasapiInterop.Release(device);
                WasapiInterop.Release(enumerator);
            }
        }
    }

    // meters a microphone and, in playback mode, plays it back through the default output so the settings mic test can let someone hear themselves; without playback it is a silent level monitor that opens no output device at all. the audio goes through the same steps obs applies (noise suppression, noise gate, volume) so the test sounds like what gets recorded. runs in the helper because obs cef has no getusermedia permission handler and obs monitoring is wired to the discord share cable. one thread owns capture, processing, meter and render, so there is no cross-thread audio hand-off to get wrong.
    internal sealed class MicLoopback
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int CaptureBufferMs = 200;
        private const int RenderBufferMs = 100;
        private const int CushionMs = 40;
        private const int MaxQueuedMs = 250;
        private const long IdleLimitMs = 3000;

        // a test left running behind a hidden window would echo the microphone until someone noticed, so it ends itself well before that matters.
        public const int MaxRunMinutes = 2;

        private readonly string _deviceId;
        private readonly bool _playback;
        private readonly MicChain _chain;
        private readonly Thread _thread;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private readonly object _levelLock = new object();
        private volatile bool _stop;
        private volatile bool _running;
        private volatile string _endReason;
        private string _startError;
        private double _gain;
        private long _lastTouch;
        private float _peak;
        private float _gatePeak;
        private double _sumSquares;
        private long _sampleCount;
        private long _framesRendered;
        private long _underruns;

        private MicLoopback(string deviceId, double gain, bool noiseSuppression, int sensitivityDb, bool playback)
        {
            _deviceId = deviceId;
            _playback = playback;
            _gain = gain;
            _chain = new MicChain(SampleRate, Channels, noiseSuppression, sensitivityDb);
            _lastTouch = Stopwatch.GetTimestamp();
            _thread = new Thread(Run) { IsBackground = true, Name = "ReplayKit mic test" };
        }

        public bool IsRunning => _running;

        public bool Playback => _playback;

        // what the session is running with right now, so a live change can be confirmed instead of assumed.
        public double Gain => Volatile.Read(ref _gain);
        public bool NoiseSuppression => _chain.NoiseSuppression;
        public int SensitivityDb => _chain.SensitivityDb;

        // why the session ended once it has, null while it is still going.
        public string EndReason => _endReason;

        // frames handed to the playback device and the times it ran dry, so playback smoothness can be checked without listening.
        public long FramesRendered => Interlocked.Read(ref _framesRendered);
        public long Underruns => Interlocked.Read(ref _underruns);

        // returns null with the reason in error when the microphone, or in playback mode the output device, could not be opened.
        public static MicLoopback Start(string deviceId, double gain, bool noiseSuppression, int sensitivityDb, bool playback, out string error)
        {
            var session = new MicLoopback(deviceId, gain, noiseSuppression, sensitivityDb, playback);
            session._thread.Start();
            bool ready = session._ready.WaitOne(4000);
            if (ready && session._startError == null) { error = null; return session; }
            error = session._startError ?? "The microphone did not start in time.";
            session._stop = true;
            return null;
        }

        // all three take effect on the running session, ramped inside the chain where a step would click.
        public void SetParams(double gain, bool noiseSuppression, int sensitivityDb)
        {
            Interlocked.Exchange(ref _gain, gain);
            _chain.SetNoiseSuppression(noiseSuppression);
            _chain.SetSensitivity(sensitivityDb);
            Touch();
        }

        // every poll renews the session, so a page that stops asking (closed, hidden, crashed) lets the test end on its own instead of echoing forever.
        public void Touch() => Interlocked.Exchange(ref _lastTouch, Stopwatch.GetTimestamp());

        public MicLevel ReadLevel()
        {
            Touch();
            lock (_levelLock)
            {
                var level = new MicLevel { Peak = _peak, GatePeak = _gatePeak, Rms = _sampleCount > 0 ? Math.Sqrt(_sumSquares / _sampleCount) : 0 };
                _peak = 0;
                _gatePeak = 0;
                _sumSquares = 0;
                _sampleCount = 0;
                return level;
            }
        }

        public void Stop()
        {
            _stop = true;
            if (Thread.CurrentThread != _thread) _thread.Join(3000);
        }

        private static long ElapsedMs(long since, long now) => (now - since) * 1000 / Stopwatch.Frequency;

        private void Run()
        {
            WasapiInterop.IMMDeviceEnumerator enumerator = null;
            WasapiInterop.IMMDevice captureDevice = null, renderDevice = null;
            WasapiInterop.IAudioClient captureClient = null, renderClient = null;
            WasapiInterop.IAudioCaptureClient capture = null;
            WasapiInterop.IAudioRenderClient render = null;
            IntPtr format = IntPtr.Zero;
            bool captureStarted = false, renderStarted = false;
            try
            {
                enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumeratorObject();
                captureDevice = OpenCaptureDevice(enumerator, _deviceId);
                if (_playback) WasapiInterop.Check(enumerator.GetDefaultAudioEndpoint(WasapiInterop.ERender, WasapiInterop.EConsole, out renderDevice), "find the playback device");
                format = WasapiInterop.AllocFloatFormat(SampleRate, Channels);

                // one fixed float format on both sides with autoconvert on, so the engine does the rate and channel conversion and there is no per-device format branching here.
                int flags = WasapiInterop.StreamFlagsAutoConvertPcm | WasapiInterop.StreamFlagsSrcDefaultQuality;
                captureClient = WasapiInterop.Activate<WasapiInterop.IAudioClient>(captureDevice, WasapiInterop.IidAudioClient);
                WasapiInterop.Check(captureClient.Initialize(WasapiInterop.ShareModeShared, flags, WasapiInterop.HnsFromMs(CaptureBufferMs), 0, format, IntPtr.Zero), "open the microphone");
                capture = WasapiInterop.GetService<WasapiInterop.IAudioCaptureClient>(captureClient, WasapiInterop.IidAudioCaptureClient);

                uint renderFrames = 0;
                if (_playback)
                {
                    renderClient = WasapiInterop.Activate<WasapiInterop.IAudioClient>(renderDevice, WasapiInterop.IidAudioClient);
                    WasapiInterop.Check(renderClient.Initialize(WasapiInterop.ShareModeShared, flags, WasapiInterop.HnsFromMs(RenderBufferMs), 0, format, IntPtr.Zero), "open the playback device");
                    WasapiInterop.Check(renderClient.GetBufferSize(out renderFrames), "size the playback buffer");
                    render = WasapiInterop.GetService<WasapiInterop.IAudioRenderClient>(renderClient, WasapiInterop.IidAudioRenderClient);

                    // silent prefill gives the render side a cushion before the first captured packet lands.
                    uint cushionFrames = (uint)(SampleRate * CushionMs / 1000);
                    WasapiInterop.Check(render.GetBuffer(cushionFrames, out IntPtr cushion), "prime the playback buffer");
                    WasapiInterop.Check(render.ReleaseBuffer(cushionFrames, WasapiInterop.BufferFlagsSilent), "prime the playback buffer");
                }

                WasapiInterop.Check(captureClient.Start(), "start the microphone");
                captureStarted = true;
                if (_playback)
                {
                    WasapiInterop.Check(renderClient.Start(), "start playback");
                    renderStarted = true;
                }
                _running = true;
                _ready.Set();

                LoopUntilDone(capture, render, renderClient, renderFrames);
            }
            catch (Exception ex)
            {
                string message = ex is MicAudioException ? ex.Message : "Microphone test failed: " + ex.Message;
                if (!_running) _startError = message;
                else _endReason = message;
                Log.Write("MicLoopback: " + message);
            }
            finally
            {
                _running = false;
                if (_endReason == null) _endReason = _stop ? "stopped" : "ended";
                if (captureStarted) { try { captureClient.Stop(); } catch { } }
                if (renderStarted) { try { renderClient.Stop(); } catch { } }
                WasapiInterop.Release(render);
                WasapiInterop.Release(renderClient);
                WasapiInterop.Release(capture);
                WasapiInterop.Release(captureClient);
                WasapiInterop.Release(renderDevice);
                WasapiInterop.Release(captureDevice);
                WasapiInterop.Release(enumerator);
                if (format != IntPtr.Zero) Marshal.FreeHGlobal(format);
                _ready.Set();
            }
        }

        private static WasapiInterop.IMMDevice OpenCaptureDevice(WasapiInterop.IMMDeviceEnumerator enumerator, string deviceId)
        {
            WasapiInterop.IMMDevice device;
            if (deviceId == MicDevices.DefaultId)
                WasapiInterop.Check(enumerator.GetDefaultAudioEndpoint(WasapiInterop.ECapture, WasapiInterop.ECommunications, out device), "find the default microphone");
            else
                WasapiInterop.Check(enumerator.GetDevice(deviceId, out device), "find the microphone");
            return device;
        }

        private void LoopUntilDone(WasapiInterop.IAudioCaptureClient capture, WasapiInterop.IAudioRenderClient render, WasapiInterop.IAudioClient renderClient, uint renderFrames)
        {
            bool playback = render != null;
            int maxQueued = SampleRate * MaxQueuedMs / 1000 * Channels;
            var queue = playback ? new float[maxQueued] : null;
            int queued = 0;
            var scratch = new float[SampleRate * Channels];
            double appliedGain = Volatile.Read(ref _gain);
            long started = Stopwatch.GetTimestamp();

            while (!_stop)
            {
                long now = Stopwatch.GetTimestamp();
                if (ElapsedMs(Interlocked.Read(ref _lastTouch), now) > IdleLimitMs) { _endReason = "idle"; return; }
                if (playback && ElapsedMs(started, now) > MaxRunMinutes * 60L * 1000) { _endReason = "time limit"; return; }

                double targetGain = Volatile.Read(ref _gain);
                while (true)
                {
                    WasapiInterop.Check(capture.GetNextPacketSize(out uint packetFrames), "read the microphone");
                    if (packetFrames == 0) break;
                    WasapiInterop.Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out ulong _, out ulong _), "read the microphone");
                    int samples = (int)frames * Channels;
                    if (samples > scratch.Length) scratch = new float[samples];
                    if ((bufferFlags & WasapiInterop.BufferFlagsSilent) != 0) Array.Clear(scratch, 0, samples);
                    else Marshal.Copy(data, scratch, 0, samples);
                    WasapiInterop.Check(capture.ReleaseBuffer(frames), "read the microphone");

                    ProcessAndMeter(scratch, (int)frames, appliedGain, targetGain);
                    appliedGain = targetGain;
                    if (!playback) continue;

                    // drift between the two device clocks slowly grows the queue, so past the cap the oldest audio goes rather than letting the echo lag further behind; every count here is a whole number of frames so left and right never swap.
                    int add = Math.Min(samples, maxQueued);
                    int overflow = queued + add - maxQueued;
                    if (overflow > 0)
                    {
                        Array.Copy(queue, overflow, queue, 0, queued - overflow);
                        queued -= overflow;
                    }
                    Array.Copy(scratch, samples - add, queue, queued, add);
                    queued += add;
                }

                if (playback)
                {
                    WasapiInterop.Check(renderClient.GetCurrentPadding(out uint padding), "write playback");
                    if (padding == 0) Interlocked.Increment(ref _underruns);
                    uint writable = Math.Min(renderFrames - padding, (uint)(queued / Channels));
                    if (writable > 0)
                    {
                        int writeSamples = (int)writable * Channels;
                        WasapiInterop.Check(render.GetBuffer(writable, out IntPtr dest), "write playback");
                        Marshal.Copy(queue, 0, dest, writeSamples);
                        WasapiInterop.Check(render.ReleaseBuffer(writable, 0), "write playback");
                        Array.Copy(queue, writeSamples, queue, 0, queued - writeSamples);
                        queued -= writeSamples;
                        Interlocked.Add(ref _framesRendered, writable);
                    }
                }

                Thread.Sleep(10);
            }
        }

        // the gate peak is what reaches the noise gate (after suppression, before the fader) since that is the level obs compares to its open threshold; the output peak and rms are after the gate and the fader, so the meter shows what would be recorded.
        private void ProcessAndMeter(float[] samples, int frames, double fromGain, double toGain)
        {
            _chain.Process(samples, frames, fromGain, toGain, out float gatePeak, out float peak, out double sumSquares);
            lock (_levelLock)
            {
                if (peak > _peak) _peak = peak;
                if (gatePeak > _gatePeak) _gatePeak = gatePeak;
                _sumSquares += sumSquares;
                _sampleCount += frames * Channels;
            }
        }
    }

    // com declarations for the windows core audio apis. hand-written because the vtable order of each interface has to match the windows headers exactly or a call lands on the wrong method; only the members this file calls are meaningful, the rest are kept for ordering.
    internal static class WasapiInterop
    {
        internal const int ERender = 0;
        internal const int ECapture = 1;
        internal const int EConsole = 0;
        internal const int ECommunications = 2;
        internal const int DeviceStateActive = 0x1;
        internal const int ClsctxAll = 23;
        internal const int ShareModeShared = 0;
        internal const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
        internal const int StreamFlagsSrcDefaultQuality = 0x08000000;
        internal const uint BufferFlagsSilent = 0x2;
        internal const ushort WaveFormatIeeeFloat = 3;
        private const ushort VtLpwstr = 31;

        internal static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        internal static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        internal static readonly Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        private static PropertyKey PkeyDeviceFriendlyName = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumeratorObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        internal interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        internal interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint framesRead);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
        internal interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr data);
            [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        // native propvariant is 16 bytes on x86 and 24 on x64; sized to match so a get never writes past the managed copy.
        [StructLayout(LayoutKind.Sequential)]
        internal struct PropVariant
        {
            public ushort vt;
            public ushort reserved1;
            public ushort reserved2;
            public ushort reserved3;
            public IntPtr pointer1;
            public IntPtr pointer2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort formatTag;
            public ushort channels;
            public uint samplesPerSec;
            public uint avgBytesPerSec;
            public ushort blockAlign;
            public ushort bitsPerSample;
            public ushort extraSize;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        internal static long HnsFromMs(int ms) => ms * 10000L;

        internal static IntPtr AllocFloatFormat(int sampleRate, int channels)
        {
            var format = new WaveFormatEx
            {
                formatTag = WaveFormatIeeeFloat,
                channels = (ushort)channels,
                samplesPerSec = (uint)sampleRate,
                bitsPerSample = 32,
                blockAlign = (ushort)(channels * 4),
                avgBytesPerSec = (uint)(sampleRate * channels * 4),
                extraSize = 0,
            };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatEx)));
            Marshal.StructureToPtr(format, ptr, false);
            return ptr;
        }

        internal static T Activate<T>(IMMDevice device, Guid iid) where T : class
        {
            Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object iface), "open the audio device");
            return (T)iface;
        }

        internal static T GetService<T>(IAudioClient client, Guid iid) where T : class
        {
            Check(client.GetService(ref iid, out object service), "open the audio stream");
            return (T)service;
        }

        internal static string ReadFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(0, out store) < 0 || store == null) return null;
                if (store.GetValue(ref PkeyDeviceFriendlyName, out PropVariant value) < 0) return null;
                try { return value.vt == VtLpwstr ? Marshal.PtrToStringUni(value.pointer1) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Release(store); }
        }

        internal static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        // failure hresults are negative; s_false style success codes above zero are fine here.
        internal static void Check(int hresult, string action)
        {
            if (hresult >= 0) return;
            string reason;
            switch (unchecked((uint)hresult))
            {
                case 0x80070005: reason = "Windows is blocking microphone access for desktop apps (Settings > Privacy > Microphone)."; break;
                case 0x8889000A: reason = "another app is using this device exclusively."; break;
                case 0x88890004: reason = "the audio device was unplugged or changed."; break;
                case 0x88890008: reason = "this device does not support the test format."; break;
                case 0x80070490: reason = "the device was not found."; break;
                default: reason = "error 0x" + ((uint)hresult).ToString("X8") + "."; break;
            }
            throw new MicAudioException("Could not " + action + ": " + reason);
        }
    }
}
