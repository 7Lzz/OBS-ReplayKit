using System;
using System.IO;

namespace ReplayKitHelper
{
    // a port of rnnoise (xiph, bsd-3-clause, see Rnnoise/COPYING) as compiled into obs studio, with the same frame analysis, pitch search and network, so the mic test hears the suppression the obs noise suppression filter records. one instance per channel; ProcessFrame takes 480 samples at 48 khz on the 16 bit scale, in place.
    internal sealed class Rnnoise
    {
        public const int FrameSize = 480;

        private const int WindowSize = 2 * FrameSize;
        private const int FreqSize = FrameSize + 1;
        private const int PitchMinPeriod = 60;
        private const int PitchMaxPeriod = 768;
        private const int PitchFrameSize = 960;
        private const int PitchBufSize = PitchMaxPeriod + PitchFrameSize;
        private const int NbBands = 22;
        private const int CepsMem = 8;
        private const int NbDeltaCeps = 6;
        private const int NbFeatures = NbBands + 3 * NbDeltaCeps + 2;

        private static readonly int[] BandEdges = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100 };
        private static readonly int[] SecondCheck = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };
        private static readonly float[] HalfWindow = BuildHalfWindow();
        private static readonly float[] DctTable = BuildDctTable();
        private static readonly RnnoiseModel Model = RnnoiseModel.Load();

        private readonly RnnoiseFft _fft = new RnnoiseFft();
        private readonly float[] _analysisMem = new float[FrameSize];
        private readonly float[][] _cepstralMem = NewRows(CepsMem, NbBands);
        private readonly float[] _synthesisMem = new float[FrameSize];
        private readonly float[] _pitchBuf = new float[PitchBufSize];
        private readonly float[] _memHpX = new float[2];
        private readonly float[] _lastGain = new float[NbBands];
        private readonly float[] _vadGruState = new float[24];
        private readonly float[] _noiseGruState = new float[48];
        private readonly float[] _denoiseGruState = new float[96];
        private int _memId;
        private float _lastPitchGain;
        private int _lastPeriod;

        private readonly float[] _xRe = new float[FreqSize], _xIm = new float[FreqSize];
        private readonly float[] _pRe = new float[FreqSize], _pIm = new float[FreqSize];
        private readonly float[] _windowed = new float[WindowSize];
        private readonly float[] _pitchWindow = new float[WindowSize];
        private readonly float[] _hp = new float[FrameSize];
        private readonly float[] _ex = new float[NbBands], _ep = new float[NbBands], _exp = new float[NbBands];
        private readonly float[] _ly = new float[NbBands];
        private readonly float[] _features = new float[NbFeatures];
        private readonly float[] _gains = new float[NbBands];
        private readonly float[] _bandGain = new float[FreqSize];
        private readonly float[] _tmpBands = new float[NbBands];
        private readonly float[] _bandSum = new float[NbBands];
        private readonly float[] _ratio = new float[NbBands], _norm = new float[NbBands], _newE = new float[NbBands];
        private readonly float[] _ratioGain = new float[FreqSize], _normGain = new float[FreqSize];
        private readonly float[] _pitchDown = new float[PitchBufSize >> 1];
        private readonly float[] _yyLookup = new float[PitchMaxPeriod / 2 + 1];
        private readonly float[] _xLp4 = new float[PitchFrameSize >> 2];
        private readonly float[] _yLp4 = new float[(PitchFrameSize + PitchMaxPeriod - 3 * PitchMinPeriod) >> 2];
        private readonly float[] _xcorr = new float[(PitchMaxPeriod - 3 * PitchMinPeriod) >> 1];
        private readonly float[] _ac = new float[5], _lpc = new float[4], _lpc2 = new float[5];
        private readonly float[] _denseOut = new float[24];
        private readonly float[] _noiseInput = new float[90];
        private readonly float[] _denoiseInput = new float[114];
        private readonly float[] _gruZ = new float[96], _gruR = new float[96], _gruH = new float[96];
        private readonly float[] _vadOut = new float[1];

        // runs one 10 ms frame in place and returns the voice probability the network reported
        public float ProcessFrame(float[] frame)
        {
            Biquad(_hp, _memHpX, frame);
            bool silence = ComputeFrameFeatures(_hp);
            float vad = 0;
            if (!silence)
            {
                ComputeRnn(_gains, _vadOut, _features);
                vad = _vadOut[0];
                PitchFilter();
                for (int i = 0; i < NbBands; i++)
                {
                    float g = Math.Max(_gains[i], .6f * _lastGain[i]);
                    _gains[i] = g;
                    _lastGain[i] = g;
                }
                // the band gains stop at 20 khz, so everything above that is zeroed, exactly as in the reference
                InterpBandGain(_bandGain, _gains);
                for (int i = 0; i < FreqSize; i++)
                {
                    _xRe[i] *= _bandGain[i];
                    _xIm[i] *= _bandGain[i];
                }
            }
            FrameSynthesis(frame);
            return vad;
        }

        private static float[][] NewRows(int rows, int cols)
        {
            var result = new float[rows][];
            for (int i = 0; i < rows; i++) result[i] = new float[cols];
            return result;
        }

        private static float[] BuildHalfWindow()
        {
            var w = new float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
                w[i] = (float)Math.Sin(.5 * Math.PI * Math.Sin(.5 * Math.PI * (i + .5) / FrameSize) * Math.Sin(.5 * Math.PI * (i + .5) / FrameSize));
            return w;
        }

        private static float[] BuildDctTable()
        {
            var t = new float[NbBands * NbBands];
            for (int i = 0; i < NbBands; i++)
            {
                for (int j = 0; j < NbBands; j++)
                {
                    t[i * NbBands + j] = (float)Math.Cos((i + .5) * j * Math.PI / NbBands);
                    if (j == 0) t[i * NbBands + j] *= (float)Math.Sqrt(.5);
                }
            }
            return t;
        }

        private static void Dct(float[] output, float[] input)
        {
            for (int i = 0; i < NbBands; i++)
            {
                float sum = 0;
                for (int j = 0; j < NbBands; j++) sum += input[j] * DctTable[j * NbBands + i];
                output[i] = (float)(sum * Math.Sqrt(2.0 / 22));
            }
        }

        private static void ApplyWindow(float[] x)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                x[i] *= HalfWindow[i];
                x[WindowSize - 1 - i] *= HalfWindow[i];
            }
        }

        private void ComputeBandEnergy(float[] bandE, float[] re, float[] im)
        {
            var sum = _bandSum;
            Array.Clear(sum, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (BandEdges[i + 1] - BandEdges[i]) << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    int k = (BandEdges[i] << 2) + j;
                    float tmp = re[k] * re[k];
                    tmp += im[k] * im[k];
                    sum[i] += (1 - frac) * tmp;
                    sum[i + 1] += frac * tmp;
                }
            }
            sum[0] *= 2;
            sum[NbBands - 1] *= 2;
            Array.Copy(sum, bandE, NbBands);
        }

        private void ComputeBandCorr(float[] bandE)
        {
            var sum = _bandSum;
            Array.Clear(sum, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (BandEdges[i + 1] - BandEdges[i]) << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    int k = (BandEdges[i] << 2) + j;
                    float tmp = _xRe[k] * _pRe[k];
                    tmp += _xIm[k] * _pIm[k];
                    sum[i] += (1 - frac) * tmp;
                    sum[i + 1] += frac * tmp;
                }
            }
            sum[0] *= 2;
            sum[NbBands - 1] *= 2;
            Array.Copy(sum, bandE, NbBands);
        }

        // the last band edge is 20 khz, so bins from there up are left at zero
        private static void InterpBandGain(float[] g, float[] bandE)
        {
            Array.Clear(g, 0, g.Length);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int bandSize = (BandEdges[i + 1] - BandEdges[i]) << 2;
                for (int j = 0; j < bandSize; j++)
                {
                    float frac = (float)j / bandSize;
                    g[(BandEdges[i] << 2) + j] = (1 - frac) * bandE[i] + frac * bandE[i + 1];
                }
            }
        }

        private void ForwardTransform(float[] re, float[] im, float[] input)
        {
            _fft.Forward(input);
            Array.Copy(_fft.OutRe, re, FreqSize);
            Array.Copy(_fft.OutIm, im, FreqSize);
        }

        // the reference runs the forward transform on a conjugate symmetric spectrum and reads the result backwards, which is an unscaled inverse
        private void InverseTransform(float[] output, float[] re, float[] im)
        {
            var inRe = _fft.InRe;
            var inIm = _fft.InIm;
            for (int i = 0; i < FreqSize; i++)
            {
                inRe[i] = re[i];
                inIm[i] = im[i];
            }
            for (int i = FreqSize; i < WindowSize; i++)
            {
                inRe[i] = inRe[WindowSize - i];
                inIm[i] = -inIm[WindowSize - i];
            }
            _fft.ForwardPrepared();
            output[0] = WindowSize * _fft.OutRe[0];
            for (int i = 1; i < WindowSize; i++) output[i] = WindowSize * _fft.OutRe[WindowSize - i];
        }

        private void FrameAnalysis(float[] ex, float[] input)
        {
            var x = _windowed;
            Array.Copy(_analysisMem, 0, x, 0, FrameSize);
            Array.Copy(input, 0, x, FrameSize, FrameSize);
            Array.Copy(input, 0, _analysisMem, 0, FrameSize);
            ApplyWindow(x);
            ForwardTransform(_xRe, _xIm, x);
            ComputeBandEnergy(ex, _xRe, _xIm);
        }

        private bool ComputeFrameFeatures(float[] input)
        {
            var ly = _ly;
            var p = _pitchWindow;
            var ex = _ex;
            var features = _features;
            float e = 0;
            float specVariability = 0;

            FrameAnalysis(ex, input);
            Array.Copy(_pitchBuf, FrameSize, _pitchBuf, 0, PitchBufSize - FrameSize);
            Array.Copy(input, 0, _pitchBuf, PitchBufSize - FrameSize, FrameSize);
            PitchDownsample(_pitchBuf, _pitchDown, PitchBufSize);
            int pitchIndex = PitchSearch(_pitchDown, PitchMaxPeriod >> 1, PitchFrameSize, PitchMaxPeriod - 3 * PitchMinPeriod);
            pitchIndex = PitchMaxPeriod - pitchIndex;

            float gain = RemoveDoubling(_pitchDown, PitchMaxPeriod, PitchMinPeriod, PitchFrameSize, ref pitchIndex, _lastPeriod, _lastPitchGain);
            _lastPeriod = pitchIndex;
            _lastPitchGain = gain;
            for (int i = 0; i < WindowSize; i++) p[i] = _pitchBuf[PitchBufSize - WindowSize - pitchIndex + i];
            ApplyWindow(p);
            ForwardTransform(_pRe, _pIm, p);
            ComputeBandEnergy(_ep, _pRe, _pIm);
            ComputeBandCorr(_exp);
            for (int i = 0; i < NbBands; i++) _exp[i] = (float)(_exp[i] / Math.Sqrt(.001 + ex[i] * _ep[i]));
            Dct(_tmpBands, _exp);
            for (int i = 0; i < NbDeltaCeps; i++) features[NbBands + 2 * NbDeltaCeps + i] = _tmpBands[i];
            features[NbBands + 2 * NbDeltaCeps] -= 1.3f;
            features[NbBands + 2 * NbDeltaCeps + 1] -= 0.9f;
            features[NbBands + 3 * NbDeltaCeps] = (float)(.01 * (pitchIndex - 300));

            float logMax = -2;
            float follow = -2;
            for (int i = 0; i < NbBands; i++)
            {
                ly[i] = (float)Math.Log10(1e-2 + ex[i]);
                double inner = follow - 1.5;
                inner = inner > ly[i] ? inner : ly[i];
                double outer = logMax - 7;
                outer = outer > inner ? outer : inner;
                ly[i] = (float)outer;
                logMax = Math.Max(logMax, ly[i]);
                double lagged = follow - 1.5;
                follow = (float)(lagged > ly[i] ? lagged : ly[i]);
                e += ex[i];
            }
            if (e < 0.04)
            {
                // with no audio there is nothing to learn from, so the state is left alone
                Array.Clear(features, 0, NbFeatures);
                return true;
            }
            Dct(features, ly);
            features[0] -= 12;
            features[1] -= 4;
            float[] ceps0 = _cepstralMem[_memId];
            float[] ceps1 = _cepstralMem[_memId < 1 ? CepsMem + _memId - 1 : _memId - 1];
            float[] ceps2 = _cepstralMem[_memId < 2 ? CepsMem + _memId - 2 : _memId - 2];
            for (int i = 0; i < NbBands; i++) ceps0[i] = features[i];
            _memId++;
            for (int i = 0; i < NbDeltaCeps; i++)
            {
                features[i] = ceps0[i] + ceps1[i] + ceps2[i];
                features[NbBands + i] = ceps0[i] - ceps2[i];
                features[NbBands + NbDeltaCeps + i] = ceps0[i] - 2 * ceps1[i] + ceps2[i];
            }
            if (_memId == CepsMem) _memId = 0;
            for (int i = 0; i < CepsMem; i++)
            {
                float minDist = 1e15f;
                for (int j = 0; j < CepsMem; j++)
                {
                    float dist = 0;
                    for (int k = 0; k < NbBands; k++)
                    {
                        float tmp = _cepstralMem[i][k] - _cepstralMem[j][k];
                        dist += tmp * tmp;
                    }
                    if (j != i) minDist = Math.Min(minDist, dist);
                }
                specVariability += minDist;
            }
            features[NbBands + 3 * NbDeltaCeps + 1] = (float)(specVariability / CepsMem - 2.1);
            return false;
        }

        private void FrameSynthesis(float[] output)
        {
            var x = _windowed;
            InverseTransform(x, _xRe, _xIm);
            ApplyWindow(x);
            for (int i = 0; i < FrameSize; i++) output[i] = x[i] + _synthesisMem[i];
            Array.Copy(x, FrameSize, _synthesisMem, 0, FrameSize);
        }

        private static void Biquad(float[] y, float[] mem, float[] x)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                float xi = x[i];
                float yi = x[i] + mem[0];
                mem[0] = (float)(mem[1] + (-2 * (double)xi - -1.99599f * (double)yi));
                mem[1] = (float)(1 * (double)xi - 0.99600f * (double)yi);
                y[i] = yi;
            }
        }

        // adds back the harmonics the pitch predictor found, then rescales each band so its energy is what the analysis measured
        private void PitchFilter()
        {
            var r = _ratio;
            var g = _gains;
            for (int i = 0; i < NbBands; i++)
            {
                if (_exp[i] > g[i]) r[i] = 1;
                else r[i] = (float)(_exp[i] * _exp[i] * (1 - g[i] * g[i]) / (.001 + g[i] * g[i] * (1 - _exp[i] * _exp[i])));
                r[i] = (float)Math.Sqrt(Math.Min(1, Math.Max(0, r[i])));
                r[i] *= (float)Math.Sqrt(_ex[i] / (1e-8 + _ep[i]));
            }
            InterpBandGain(_ratioGain, r);
            for (int i = 0; i < FreqSize; i++)
            {
                _xRe[i] += _ratioGain[i] * _pRe[i];
                _xIm[i] += _ratioGain[i] * _pIm[i];
            }
            ComputeBandEnergy(_newE, _xRe, _xIm);
            for (int i = 0; i < NbBands; i++) _norm[i] = (float)Math.Sqrt(_ex[i] / (1e-8 + _newE[i]));
            InterpBandGain(_normGain, _norm);
            for (int i = 0; i < FreqSize; i++)
            {
                _xRe[i] *= _normGain[i];
                _xIm[i] *= _normGain[i];
            }
        }

        // pitch analysis, translated from the reference in its float configuration; arrays plus offsets stand in for its pointers
        private static float InnerProd(float[] x, int xOff, float[] y, int yOff, int n)
        {
            float xy = 0;
            for (int i = 0; i < n; i++) xy += x[xOff + i] * y[yOff + i];
            return xy;
        }

        private static void FindBestPitch(float[] xcorr, float[] y, int len, int maxPitch, out int best0, out int best1)
        {
            float syy = 1;
            float bestNum0 = -1, bestNum1 = -1;
            float bestDen0 = 0, bestDen1 = 0;
            best0 = 0;
            best1 = 1;
            for (int j = 0; j < len; j++) syy += y[j] * y[j];
            for (int i = 0; i < maxPitch; i++)
            {
                if (xcorr[i] > 0)
                {
                    float xcorr16 = xcorr[i];
                    xcorr16 *= 1e-12f;
                    float num = xcorr16 * xcorr16;
                    if (num * bestDen1 > bestNum1 * syy)
                    {
                        if (num * bestDen0 > bestNum0 * syy)
                        {
                            bestNum1 = bestNum0;
                            bestDen1 = bestDen0;
                            best1 = best0;
                            bestNum0 = num;
                            bestDen0 = syy;
                            best0 = i;
                        }
                        else
                        {
                            bestNum1 = num;
                            bestDen1 = syy;
                            best1 = i;
                        }
                    }
                }
                syy += y[i + len] * y[i + len] - y[i] * y[i];
                syy = Math.Max(1, syy);
            }
        }

        // halves the rate and whitens the signal a little, so the search sees the pitch structure rather than the spectral tilt
        private void PitchDownsample(float[] x, float[] xLp, int len)
        {
            int n = len >> 1;
            for (int i = 1; i < n; i++) xLp[i] = .5f * (.5f * (x[2 * i - 1] + x[2 * i + 1]) + x[2 * i]);
            xLp[0] = .5f * (.5f * x[1] + x[0]);

            var ac = _ac;
            int fastN = n - 4;
            for (int k = 0; k <= 4; k++)
            {
                ac[k] = InnerProd(xLp, 0, xLp, k, fastN);
                float d = 0;
                for (int i = k + fastN; i < n; i++) d += xLp[i] * xLp[i - k];
                ac[k] += d;
            }
            ac[0] *= 1.0001f;
            for (int i = 1; i <= 4; i++) ac[i] -= ac[i] * (.008f * i) * (.008f * i);

            var lpc = _lpc;
            CeltLpc(lpc, ac, 4);
            float tmp = 1.0f;
            for (int i = 0; i < 4; i++)
            {
                tmp = .9f * tmp;
                lpc[i] = lpc[i] * tmp;
            }
            const float c1 = .8f;
            var lpc2 = _lpc2;
            lpc2[0] = lpc[0] + .8f;
            lpc2[1] = lpc[1] + c1 * lpc[0];
            lpc2[2] = lpc[2] + c1 * lpc[1];
            lpc2[3] = lpc[3] + c1 * lpc[2];
            lpc2[4] = c1 * lpc[3];

            float mem0 = 0, mem1 = 0, mem2 = 0, mem3 = 0, mem4 = 0;
            for (int i = 0; i < n; i++)
            {
                float sum = xLp[i];
                sum += lpc2[0] * mem0;
                sum += lpc2[1] * mem1;
                sum += lpc2[2] * mem2;
                sum += lpc2[3] * mem3;
                sum += lpc2[4] * mem4;
                mem4 = mem3;
                mem3 = mem2;
                mem2 = mem1;
                mem1 = mem0;
                mem0 = xLp[i];
                xLp[i] = sum;
            }
        }

        private static void CeltLpc(float[] lpc, float[] ac, int p)
        {
            float error = ac[0];
            Array.Clear(lpc, 0, p);
            if (ac[0] != 0)
            {
                for (int i = 0; i < p; i++)
                {
                    float rr = 0;
                    for (int j = 0; j < i; j++) rr += lpc[j] * ac[i - j];
                    rr += ac[i + 1];
                    float r = -rr / error;
                    lpc[i] = r;
                    for (int j = 0; j < (i + 1) >> 1; j++)
                    {
                        float tmp1 = lpc[j];
                        float tmp2 = lpc[i - 1 - j];
                        lpc[j] = tmp1 + r * tmp2;
                        lpc[i - 1 - j] = tmp2 + r * tmp1;
                    }
                    error = error - r * r * error;
                    if (error < .001f * ac[0]) break;
                }
            }
        }

        // buf is the downsampled history; the newest frame starts at xOff inside it and the lags are read back from its start
        private int PitchSearch(float[] buf, int xOff, int len, int maxPitch)
        {
            int lag = len + maxPitch;
            var xLp4 = _xLp4;
            var yLp4 = _yLp4;
            var xcorr = _xcorr;

            for (int j = 0; j < len >> 2; j++) xLp4[j] = buf[xOff + 2 * j];
            for (int j = 0; j < lag >> 2; j++) yLp4[j] = buf[2 * j];

            for (int i = 0; i < maxPitch >> 2; i++) xcorr[i] = InnerProd(xLp4, 0, yLp4, i, len >> 2);
            FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, out int best0, out int best1);

            for (int i = 0; i < maxPitch >> 1; i++)
            {
                xcorr[i] = 0;
                if (Math.Abs(i - 2 * best0) > 2 && Math.Abs(i - 2 * best1) > 2) continue;
                float sum = InnerProd(buf, xOff, buf, i, len >> 1);
                xcorr[i] = Math.Max(-1, sum);
            }
            FindBestPitch(xcorr, buf, len >> 1, maxPitch >> 1, out best0, out best1);

            int offset;
            if (best0 > 0 && best0 < (maxPitch >> 1) - 1)
            {
                float a = xcorr[best0 - 1];
                float b = xcorr[best0];
                float c = xcorr[best0 + 1];
                if ((c - a) > .7f * (b - a)) offset = 1;
                else if ((a - c) > .7f * (b - c)) offset = -1;
                else offset = 0;
            }
            else offset = 0;
            return 2 * best0 - offset;
        }

        private static float PitchGain(float xy, float xx, float yy)
        {
            return (float)(xy / Math.Sqrt(1.0 + xx * yy));
        }

        private float RemoveDoubling(float[] buf, int maxPeriod, int minPeriod, int n, ref int t0Ref, int prevPeriod, float prevGain)
        {
            int minPeriod0 = minPeriod;
            maxPeriod /= 2;
            minPeriod /= 2;
            int t0 = t0Ref / 2;
            prevPeriod /= 2;
            n /= 2;
            int x = maxPeriod;
            if (t0 >= maxPeriod) t0 = maxPeriod - 1;

            int t = t0;
            var yyLookup = _yyLookup;
            float xx = 0, xy = 0;
            for (int i = 0; i < n; i++)
            {
                xx += buf[x + i] * buf[x + i];
                xy += buf[x + i] * buf[x + i - t0];
            }
            yyLookup[0] = xx;
            float yy = xx;
            for (int i = 1; i <= maxPeriod; i++)
            {
                yy = yy + buf[x - i] * buf[x - i] - buf[x + n - i] * buf[x + n - i];
                yyLookup[i] = Math.Max(0, yy);
            }
            yy = yyLookup[t0];
            float bestXy = xy;
            float bestYy = yy;
            float g0 = PitchGain(xy, xx, yy);
            float g = g0;
            for (int k = 2; k <= 15; k++)
            {
                int t1 = (2 * t0 + k) / (2 * k);
                if (t1 < minPeriod) break;
                int t1b;
                if (k == 2) t1b = t1 + t0 > maxPeriod ? t0 : t0 + t1;
                else t1b = (2 * SecondCheck[k] * t0 + k) / (2 * k);
                float xy1 = 0, xy2 = 0;
                for (int i = 0; i < n; i++)
                {
                    xy1 += buf[x + i] * buf[x + i - t1];
                    xy2 += buf[x + i] * buf[x + i - t1b];
                }
                xy = .5f * (xy1 + xy2);
                yy = .5f * (yyLookup[t1] + yyLookup[t1b]);
                float g1 = PitchGain(xy, xx, yy);
                float cont;
                if (Math.Abs(t1 - prevPeriod) <= 1) cont = prevGain;
                else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < t0) cont = .5f * prevGain;
                else cont = 0;
                float thresh = Math.Max(.3f, .7f * g0 - cont);
                // the second branch can never be reached, but it is kept because the reference has it
                if (t1 < 3 * minPeriod) thresh = Math.Max(.4f, .85f * g0 - cont);
                else if (t1 < 2 * minPeriod) thresh = Math.Max(.5f, .9f * g0 - cont);
                if (g1 > thresh)
                {
                    bestXy = xy;
                    bestYy = yy;
                    t = t1;
                    g = g1;
                }
            }
            bestXy = Math.Max(0, bestXy);
            float pg = bestYy <= bestXy ? 1.0f : bestXy / (bestYy + 1);

            float xc0 = InnerProd(buf, x, buf, x - (t - 1), n);
            float xc1 = InnerProd(buf, x, buf, x - t, n);
            float xc2 = InnerProd(buf, x, buf, x - (t + 1), n);
            int offset;
            if ((xc2 - xc0) > .7f * (xc1 - xc0)) offset = 1;
            else if ((xc0 - xc2) > .7f * (xc1 - xc2)) offset = -1;
            else offset = 0;
            if (pg > g) pg = g;
            t0Ref = 2 * t + offset;
            if (t0Ref < minPeriod0) t0Ref = minPeriod0;
            return pg;
        }

        // the network: a dense input layer, a voice activity gru feeding a noise gru and a denoise gru, and a dense layer of band gains
        private void ComputeRnn(float[] gains, float[] vad, float[] input)
        {
            var m = Model;
            Dense(m.InputDense, _denseOut, input);
            Gru(m.VadGru, _vadGruState, _denseOut);
            Dense(m.VadOutput, vad, _vadGruState);
            Array.Copy(_denseOut, 0, _noiseInput, 0, 24);
            Array.Copy(_vadGruState, 0, _noiseInput, 24, 24);
            Array.Copy(input, 0, _noiseInput, 48, NbFeatures);
            Gru(m.NoiseGru, _noiseGruState, _noiseInput);
            Array.Copy(_vadGruState, 0, _denoiseInput, 0, 24);
            Array.Copy(_noiseGruState, 0, _denoiseInput, 24, 48);
            Array.Copy(input, 0, _denoiseInput, 72, NbFeatures);
            Gru(m.DenoiseGru, _denoiseGruState, _denoiseInput);
            Dense(m.DenoiseOutput, gains, _denoiseGruState);
        }

        private static float TansigApprox(float x)
        {
            // the tests are written so that a nan falls into the first one
            if (!(x < 8)) return 1;
            if (!(x > -8)) return -1;
            float sign = 1;
            if (x < 0)
            {
                x = -x;
                sign = -1;
            }
            int i = (int)Math.Floor((double)(.5f + 25 * x));
            x -= .04f * i;
            float y = RnnoiseModel.Tansig[i];
            float dy = 1 - y * y;
            y = y + x * dy * (1 - y * x);
            return sign * y;
        }

        private static float SigmoidApprox(float x) => (float)(.5 + .5 * TansigApprox(.5f * x));

        private static float Relu(float x) => x < 0 ? 0 : x;

        private static float Activate(int activation, float x)
        {
            if (activation == RnnoiseModel.ActivationSigmoid) return SigmoidApprox(x);
            if (activation == RnnoiseModel.ActivationTanh) return TansigApprox(x);
            return Relu(x);
        }

        private static void Dense(RnnoiseModel.DenseLayer layer, float[] output, float[] input)
        {
            int m = layer.Inputs;
            int n = layer.Neurons;
            for (int i = 0; i < n; i++)
            {
                float sum = layer.Bias[i];
                int row = i * m;
                for (int j = 0; j < m; j++) sum += layer.Weights[row + j] * input[j];
                output[i] = RnnoiseModel.WeightsScale * sum;
            }
            for (int i = 0; i < n; i++) output[i] = Activate(layer.Activation, output[i]);
        }

        private void Gru(RnnoiseModel.GruLayer gru, float[] state, float[] input)
        {
            int m = gru.Inputs;
            int n = gru.Neurons;
            var z = _gruZ;
            var r = _gruR;
            var h = _gruH;
            for (int i = 0; i < n; i++)
            {
                float sum = gru.Bias[i];
                int rowIn = i * m;
                int rowRec = i * n;
                for (int j = 0; j < m; j++) sum += gru.InputWeights[rowIn + j] * input[j];
                for (int j = 0; j < n; j++) sum += gru.RecurrentWeights[rowRec + j] * state[j];
                z[i] = SigmoidApprox(RnnoiseModel.WeightsScale * sum);
            }
            for (int i = 0; i < n; i++)
            {
                float sum = gru.Bias[n + i];
                int rowIn = (n + i) * m;
                int rowRec = (n + i) * n;
                for (int j = 0; j < m; j++) sum += gru.InputWeights[rowIn + j] * input[j];
                for (int j = 0; j < n; j++) sum += gru.RecurrentWeights[rowRec + j] * state[j];
                r[i] = SigmoidApprox(RnnoiseModel.WeightsScale * sum);
            }
            for (int i = 0; i < n; i++)
            {
                float sum = gru.Bias[2 * n + i];
                int rowIn = (2 * n + i) * m;
                int rowRec = (2 * n + i) * n;
                for (int j = 0; j < m; j++) sum += gru.InputWeights[rowIn + j] * input[j];
                for (int j = 0; j < n; j++) sum += gru.RecurrentWeights[rowRec + j] * state[j] * r[j];
                sum = Activate(gru.Activation, RnnoiseModel.WeightsScale * sum);
                h[i] = z[i] * state[i] + (1 - z[i]) * sum;
            }
            for (int i = 0; i < n; i++) state[i] = h[i];
        }
    }

    // the 960 point complex fft the analysis and synthesis use, scaled by 1/960 like the reference. 960 = 4 * 4 * 4 * 3 * 5, done as a recursive decimation in time with hand written radix 4, 3 and 5 butterflies.
    internal sealed class RnnoiseFft
    {
        public const int Size = 960;

        private static readonly int[] Factors = { 4, 4, 4, 3, 5 };
        private static readonly float[][] TwRe = new float[Factors.Length][];
        private static readonly float[][] TwIm = new float[Factors.Length][];

        private const float Sin60 = 0.8660254037844386f;
        private const float CosA = 0.30901699437494745f, CosB = -0.8090169943749475f;
        private const float SinA = 0.9510565162951535f, SinB = 0.5877852522924731f;

        public readonly float[] InRe = new float[Size], InIm = new float[Size];
        public readonly float[] OutRe = new float[Size], OutIm = new float[Size];

        static RnnoiseFft()
        {
            int n = Size;
            for (int level = 0; level < Factors.Length; level++)
            {
                int p = Factors[level];
                int m = n / p;
                var re = new float[p * m];
                var im = new float[p * m];
                for (int r = 0; r < p; r++)
                {
                    for (int k = 0; k < m; k++)
                    {
                        double angle = -2.0 * Math.PI * r * k / n;
                        re[r * m + k] = (float)Math.Cos(angle);
                        im[r * m + k] = (float)Math.Sin(angle);
                    }
                }
                TwRe[level] = re;
                TwIm[level] = im;
                n = m;
            }
        }

        // real input; the imaginary part is taken as zero
        public void Forward(float[] input)
        {
            Array.Copy(input, InRe, Size);
            Array.Clear(InIm, 0, Size);
            ForwardPrepared();
        }

        // transforms whatever is in InRe and InIm
        public void ForwardPrepared()
        {
            Recurse(0, 0, 1, 0, Size);
            const float scale = 1f / Size;
            for (int i = 0; i < Size; i++)
            {
                OutRe[i] *= scale;
                OutIm[i] *= scale;
            }
        }

        private void Recurse(int level, int inOff, int inStride, int outOff, int n)
        {
            if (n == 1)
            {
                OutRe[outOff] = InRe[inOff];
                OutIm[outOff] = InIm[inOff];
                return;
            }
            int p = Factors[level];
            int m = n / p;
            for (int r = 0; r < p; r++) Recurse(level + 1, inOff + r * inStride, inStride * p, outOff + r * m, m);
            Combine(level, outOff, p, m);
        }

        private void Combine(int level, int outOff, int p, int m)
        {
            float[] twr = TwRe[level], twi = TwIm[level];
            float[] re = OutRe, im = OutIm;
            for (int k = 0; k < m; k++)
            {
                int i0 = outOff + k;
                int i1 = i0 + m;
                int i2 = i1 + m;
                float t0r = re[i0], t0i = im[i0];
                float t1r = re[i1] * twr[m + k] - im[i1] * twi[m + k];
                float t1i = re[i1] * twi[m + k] + im[i1] * twr[m + k];
                float t2r = re[i2] * twr[2 * m + k] - im[i2] * twi[2 * m + k];
                float t2i = re[i2] * twi[2 * m + k] + im[i2] * twr[2 * m + k];
                if (p == 3)
                {
                    float sr = t1r + t2r, si = t1i + t2i;
                    float dr = t1r - t2r, di = t1i - t2i;
                    float mr = t0r - .5f * sr, mi = t0i - .5f * si;
                    re[i0] = t0r + sr;
                    im[i0] = t0i + si;
                    re[i1] = mr + Sin60 * di;
                    im[i1] = mi - Sin60 * dr;
                    re[i2] = mr - Sin60 * di;
                    im[i2] = mi + Sin60 * dr;
                    continue;
                }
                int i3 = i2 + m;
                float t3r = re[i3] * twr[3 * m + k] - im[i3] * twi[3 * m + k];
                float t3i = re[i3] * twi[3 * m + k] + im[i3] * twr[3 * m + k];
                if (p == 4)
                {
                    float ar = t0r + t2r, ai = t0i + t2i;
                    float br = t0r - t2r, bi = t0i - t2i;
                    float cr = t1r + t3r, ci = t1i + t3i;
                    float dr = t1r - t3r, di = t1i - t3i;
                    re[i0] = ar + cr;
                    im[i0] = ai + ci;
                    re[i2] = ar - cr;
                    im[i2] = ai - ci;
                    re[i1] = br + di;
                    im[i1] = bi - dr;
                    re[i3] = br - di;
                    im[i3] = bi + dr;
                    continue;
                }
                int i4 = i3 + m;
                float t4r = re[i4] * twr[4 * m + k] - im[i4] * twi[4 * m + k];
                float t4i = re[i4] * twi[4 * m + k] + im[i4] * twr[4 * m + k];
                float s1r = t1r + t4r, s1i = t1i + t4i, d1r = t1r - t4r, d1i = t1i - t4i;
                float s2r = t2r + t3r, s2i = t2i + t3i, d2r = t2r - t3r, d2i = t2i - t3i;
                re[i0] = t0r + s1r + s2r;
                im[i0] = t0i + s1i + s2i;
                float a1r = t0r + CosA * s1r + CosB * s2r, a1i = t0i + CosA * s1i + CosB * s2i;
                float a2r = t0r + CosB * s1r + CosA * s2r, a2i = t0i + CosB * s1i + CosA * s2i;
                float u1r = SinA * d1r + SinB * d2r, u1i = SinA * d1i + SinB * d2i;
                float u2r = SinB * d1r - SinA * d2r, u2i = SinB * d1i - SinA * d2i;
                re[i1] = a1r + u1i;
                im[i1] = a1i - u1r;
                re[i4] = a1r - u1i;
                im[i4] = a1i + u1r;
                re[i2] = a2r + u2i;
                im[i2] = a2i - u2r;
                re[i3] = a2r - u2i;
                im[i3] = a2i + u2r;
            }
        }
    }

    // the network weights, read from the embedded resource that was extracted from rnn_data.c of the rnnoise copy in obs studio, and laid out so each neuron reads its weights one after another
    internal sealed class RnnoiseModel
    {
        public const float WeightsScale = 1f / 256;
        public const int ActivationTanh = 0;
        public const int ActivationSigmoid = 1;
        public const int ActivationRelu = 2;

        private const string ResourceName = "ReplayKitHelper.rnnoise_model.bin";
        private const int ExpectedBytes = 87503;

        public static readonly float[] Tansig =
        {
            0.000000f, 0.039979f, 0.079830f, 0.119427f, 0.158649f, 0.197375f, 0.235496f, 0.272905f,
            0.309507f, 0.345214f, 0.379949f, 0.413644f, 0.446244f, 0.477700f, 0.507977f, 0.537050f,
            0.564900f, 0.591519f, 0.616909f, 0.641077f, 0.664037f, 0.685809f, 0.706419f, 0.725897f,
            0.744277f, 0.761594f, 0.777888f, 0.793199f, 0.807569f, 0.821040f, 0.833655f, 0.845456f,
            0.856485f, 0.866784f, 0.876393f, 0.885352f, 0.893698f, 0.901468f, 0.908698f, 0.915420f,
            0.921669f, 0.927473f, 0.932862f, 0.937863f, 0.942503f, 0.946806f, 0.950795f, 0.954492f,
            0.957917f, 0.961090f, 0.964028f, 0.966747f, 0.969265f, 0.971594f, 0.973749f, 0.975743f,
            0.977587f, 0.979293f, 0.980869f, 0.982327f, 0.983675f, 0.984921f, 0.986072f, 0.987136f,
            0.988119f, 0.989027f, 0.989867f, 0.990642f, 0.991359f, 0.992020f, 0.992631f, 0.993196f,
            0.993718f, 0.994199f, 0.994644f, 0.995055f, 0.995434f, 0.995784f, 0.996108f, 0.996407f,
            0.996682f, 0.996937f, 0.997172f, 0.997389f, 0.997590f, 0.997775f, 0.997946f, 0.998104f,
            0.998249f, 0.998384f, 0.998508f, 0.998623f, 0.998728f, 0.998826f, 0.998916f, 0.999000f,
            0.999076f, 0.999147f, 0.999213f, 0.999273f, 0.999329f, 0.999381f, 0.999428f, 0.999472f,
            0.999513f, 0.999550f, 0.999585f, 0.999617f, 0.999646f, 0.999673f, 0.999699f, 0.999722f,
            0.999743f, 0.999763f, 0.999781f, 0.999798f, 0.999813f, 0.999828f, 0.999841f, 0.999853f,
            0.999865f, 0.999875f, 0.999885f, 0.999893f, 0.999902f, 0.999909f, 0.999916f, 0.999923f,
            0.999929f, 0.999934f, 0.999939f, 0.999944f, 0.999948f, 0.999952f, 0.999956f, 0.999959f,
            0.999962f, 0.999965f, 0.999968f, 0.999970f, 0.999973f, 0.999975f, 0.999977f, 0.999978f,
            0.999980f, 0.999982f, 0.999983f, 0.999984f, 0.999986f, 0.999987f, 0.999988f, 0.999989f,
            0.999990f, 0.999990f, 0.999991f, 0.999992f, 0.999992f, 0.999993f, 0.999994f, 0.999994f,
            0.999994f, 0.999995f, 0.999995f, 0.999996f, 0.999996f, 0.999996f, 0.999997f, 0.999997f,
            0.999997f, 0.999997f, 0.999997f, 0.999998f, 0.999998f, 0.999998f, 0.999998f, 0.999998f,
            0.999998f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f,
            0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 0.999999f, 1.000000f, 1.000000f,
            1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f, 1.000000f,
            1.000000f,
        };

        public sealed class DenseLayer
        {
            public int Inputs, Neurons, Activation;
            public float[] Bias, Weights;
        }

        public sealed class GruLayer
        {
            public int Inputs, Neurons, Activation;
            public float[] Bias, InputWeights, RecurrentWeights;
        }

        public DenseLayer InputDense, DenoiseOutput, VadOutput;
        public GruLayer VadGru, NoiseGru, DenoiseGru;

        public static RnnoiseModel Load()
        {
            byte[] blob;
            using (Stream stream = typeof(RnnoiseModel).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidOperationException("The noise suppression model is missing from this build.");
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    blob = copy.ToArray();
                }
            }
            if (blob.Length != ExpectedBytes) throw new InvalidOperationException("The noise suppression model is the wrong size.");

            int at = 0;
            var model = new RnnoiseModel();
            model.InputDense = ReadDense(blob, ref at, 42, 24, ActivationTanh);
            model.VadGru = ReadGru(blob, ref at, 24, 24, ActivationRelu);
            model.NoiseGru = ReadGru(blob, ref at, 90, 48, ActivationRelu);
            model.DenoiseGru = ReadGru(blob, ref at, 114, 96, ActivationRelu);
            model.DenoiseOutput = ReadDense(blob, ref at, 96, 22, ActivationSigmoid);
            model.VadOutput = ReadDense(blob, ref at, 24, 1, ActivationSigmoid);
            if (at != ExpectedBytes) throw new InvalidOperationException("The noise suppression model did not parse cleanly.");
            return model;
        }

        // the file stores each weight matrix input major, so neuron i of input j sits at j * neurons + i
        private static DenseLayer ReadDense(byte[] blob, ref int at, int inputs, int neurons, int activation)
        {
            var layer = new DenseLayer { Inputs = inputs, Neurons = neurons, Activation = activation, Weights = new float[inputs * neurons], Bias = new float[neurons] };
            for (int i = 0; i < neurons; i++)
                for (int j = 0; j < inputs; j++)
                    layer.Weights[i * inputs + j] = (sbyte)blob[at + j * neurons + i];
            at += inputs * neurons;
            for (int i = 0; i < neurons; i++) layer.Bias[i] = (sbyte)blob[at + i];
            at += neurons;
            return layer;
        }

        // a gru matrix has one row per input holding the update, reset and output gate weights side by side, so gate g of neuron i for input j sits at g * neurons + j * 3 * neurons + i; the recurrent matrix and then the bias follow the input matrix
        private static GruLayer ReadGru(byte[] blob, ref int at, int inputs, int neurons, int activation)
        {
            var layer = new GruLayer
            {
                Inputs = inputs, Neurons = neurons, Activation = activation,
                InputWeights = new float[3 * neurons * inputs], RecurrentWeights = new float[3 * neurons * neurons], Bias = new float[3 * neurons],
            };
            int stride = 3 * neurons;
            for (int gate = 0; gate < 3; gate++)
            {
                for (int i = 0; i < neurons; i++)
                {
                    for (int j = 0; j < inputs; j++) layer.InputWeights[(gate * neurons + i) * inputs + j] = (sbyte)blob[at + gate * neurons + j * stride + i];
                }
            }
            at += 3 * neurons * inputs;
            for (int gate = 0; gate < 3; gate++)
            {
                for (int i = 0; i < neurons; i++)
                {
                    for (int j = 0; j < neurons; j++) layer.RecurrentWeights[(gate * neurons + i) * neurons + j] = (sbyte)blob[at + gate * neurons + j * stride + i];
                }
            }
            at += 3 * neurons * neurons;
            for (int i = 0; i < 3 * neurons; i++) layer.Bias[i] = (sbyte)blob[at + i];
            at += 3 * neurons;
            return layer;
        }
    }
}
