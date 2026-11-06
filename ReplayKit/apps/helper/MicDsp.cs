using System;
using System.Threading;

namespace ReplayKitHelper
{
    // the slice of the obs mic chain the mic test reproduces, in obs order: noise suppression, then the noise gate, then the volume fader. one capture thread calls Process; the setters are safe to call from request threads.
    internal sealed class MicChain
    {
        private readonly int _channels;
        private readonly MicNoiseSuppressor _suppressor;
        private readonly MicNoiseGate _gate;
        private volatile bool _noiseSuppression;
        private int _sensitivityDb;

        public MicChain(int sampleRate, int channels, bool noiseSuppression, int sensitivityDb)
        {
            _channels = channels;
            _suppressor = new MicNoiseSuppressor(channels);
            _gate = new MicNoiseGate(sampleRate, channels);
            _noiseSuppression = noiseSuppression;
            _sensitivityDb = sensitivityDb;
        }

        public bool NoiseSuppression => _noiseSuppression;

        public int SensitivityDb => Volatile.Read(ref _sensitivityDb);

        public void SetNoiseSuppression(bool on) => _noiseSuppression = on;

        public void SetSensitivity(int db) => Volatile.Write(ref _sensitivityDb, db);

        // gatePeak is the loudest sample entering the gate (after suppression, before the fader), which is the level obs compares to its open threshold; peak and sumSquares describe what leaves after the fader, ramped from the old gain to the new one so a step in a live signal is not a click.
        public void Process(float[] samples, int frames, double fromGain, double toGain, out float gatePeak, out float peak, out double sumSquares)
        {
            int count = frames * _channels;
            _suppressor.Process(samples, frames, _noiseSuppression);
            gatePeak = 0;
            for (int i = 0; i < count; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > gatePeak) gatePeak = abs;
            }
            _gate.Process(samples, frames, Volatile.Read(ref _sensitivityDb));

            peak = 0;
            sumSquares = 0;
            for (int i = 0; i < count; i++)
            {
                double gain = fromGain + (toGain - fromGain) * ((i / _channels) + 1) / frames;
                float value = (float)(samples[i] * gain);
                samples[i] = value;
                float abs = Math.Abs(value);
                if (abs > peak) peak = abs;
                sumSquares += (double)value * value;
            }
        }
    }

    // the obs noise gate: opens when a sample is louder than the open threshold, closes once the level has stayed under the close threshold for the hold time, and fades in over the attack and out over the release using the cube obs applies. attack, hold, release and the gap between the two thresholds are the obs filter defaults. the floor of the slider means off; the state machine keeps tracking and only the effect is faded out, so turning it back on never starts from a stale state.
    internal sealed class MicNoiseGate
    {
        public const int CloseGapDb = 5;
        private const double AttackMs = 25;
        private const double HoldMs = 200;
        private const double ReleaseMs = 150;
        private const double MixRampMs = 10;

        private readonly int _channels;
        private readonly float _sampleInv;
        private readonly float _attackRate;
        private readonly float _releaseRate;
        private readonly float _holdSeconds;
        private readonly float _mixStep;
        private float _level;
        private float _attenuation;
        private float _held;
        private float _mix;
        private bool _open;

        public MicNoiseGate(int sampleRate, int channels)
        {
            _channels = channels;
            _sampleInv = 1f / sampleRate;
            _attackRate = (float)(1.0 / (AttackMs / 1000.0 * sampleRate));
            _releaseRate = (float)(1.0 / (ReleaseMs / 1000.0 * sampleRate));
            _holdSeconds = (float)(HoldMs / 1000.0);
            _mixStep = (float)(1.0 / (MixRampMs / 1000.0 * sampleRate));
        }

        public void Process(float[] samples, int frames, int sensitivityDb)
        {
            float mixTarget = sensitivityDb > MicTest.SensitivityMin ? 1f : 0f;
            float openThreshold = (float)Math.Pow(10.0, sensitivityDb / 20.0);
            float closeThreshold = (float)Math.Pow(10.0, Math.Max(sensitivityDb - CloseGapDb, MicTest.SensitivityMin) / 20.0);
            for (int f = 0; f < frames; f++)
            {
                int at = f * _channels;
                float current = Math.Abs(samples[at]);
                for (int c = 1; c < _channels; c++) current = Math.Max(current, Math.Abs(samples[at + c]));

                if (current > openThreshold && !_open) _open = true;
                if (_level < closeThreshold && _open)
                {
                    _held += _sampleInv;
                    if (_held > _holdSeconds) _open = false;
                }
                else _held = 0f;

                _level = Math.Max(_level, current) - _releaseRate;
                _attenuation = _open ? Math.Min(1f, _attenuation + _attackRate) : Math.Max(0f, _attenuation - _releaseRate);

                if (_mix < mixTarget) _mix = Math.Min(mixTarget, _mix + _mixStep);
                else if (_mix > mixTarget) _mix = Math.Max(mixTarget, _mix - _mixStep);
                float gain = 1f + _mix * (_attenuation * _attenuation * _attenuation - 1f);
                for (int c = 0; c < _channels; c++) samples[at + c] *= gain;
            }
        }
    }

    // rnnoise per channel, the suppressor obs runs in its noise suppression filter, with one state per channel like obs. it always runs so the network state is warm, and the output is a ramped mix of the aligned dry signal and the suppressed one, so switching it on or off never clicks and the delay never changes.
    internal sealed class MicNoiseSuppressor
    {
        // rnnoise hands a frame back one frame late and a frame has to be collected before it can run, so the wet path trails the input by two frames; the dry path is delayed by the same amount.
        public const int Latency = 2 * Rnnoise.FrameSize;

        private const float RampSeconds = 0.03f;
        private const int SampleRate = 48000;
        private const float Int16Scale = 32768f;

        private readonly int _channels;
        private readonly SuppressedChannel[] _state;
        private readonly float _step = 1f / (RampSeconds * SampleRate);
        private float _wet;

        public MicNoiseSuppressor(int channels)
        {
            _channels = channels;
            _state = new SuppressedChannel[channels];
            for (int c = 0; c < channels; c++) _state[c] = new SuppressedChannel();
        }

        public void Process(float[] samples, int frames, bool enabled)
        {
            float target = enabled ? 1f : 0f;
            for (int f = 0; f < frames; f++)
            {
                if (_wet < target) _wet = Math.Min(target, _wet + _step);
                else if (_wet > target) _wet = Math.Max(target, _wet - _step);
                for (int c = 0; c < _channels; c++)
                {
                    int at = f * _channels + c;
                    float suppressed = _state[c].Process(samples[at], out float dry);
                    samples[at] = dry + _wet * (suppressed - dry);
                }
            }
        }

        // collects one frame at a time for rnnoise, which works on the 16 bit scale like obs feeds it, and returns the suppressed sample along with the input delayed by the same Latency.
        private sealed class SuppressedChannel
        {
            private readonly Rnnoise _denoiser = new Rnnoise();
            private readonly float[] _collected = new float[Rnnoise.FrameSize];
            private readonly float[] _processed = new float[Rnnoise.FrameSize];
            private readonly float[] _delay = new float[Latency];
            private int _fill;
            private int _delayPos;

            public float Process(float x, out float dry)
            {
                dry = _delay[_delayPos];
                _delay[_delayPos] = x;
                if (++_delayPos == Latency) _delayPos = 0;

                float wet = _processed[_fill] / Int16Scale;
                _collected[_fill] = x * Int16Scale;
                if (++_fill == Rnnoise.FrameSize)
                {
                    Array.Copy(_collected, _processed, Rnnoise.FrameSize);
                    _denoiser.ProcessFrame(_processed);
                    _fill = 0;
                }
                return wet;
            }
        }
    }
}
