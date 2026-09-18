using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace QDTool
{
    internal enum WavPulseMode
    {
        ZeroCrossing,
        Schmitt
    }

    internal enum SharpBlockKind
    {
        Header,
        Payload
    }

    internal sealed class SharpBlockCandidate
    {
        internal required SharpBlockKind Kind { get; init; }
        internal required int Channel { get; init; }
        internal required int CopyIndex { get; init; }
        internal required bool Inverted { get; init; }
        internal required WavPulseMode PulseMode { get; init; }
        internal required byte[] Data { get; init; }
        internal required bool ChecksumValid { get; init; }
        internal required ushort RecordedChecksum { get; init; }
        internal required ushort CalculatedChecksum { get; init; }
        internal required long StartSample { get; init; }
        internal required long EndSample { get; init; }
        internal required double LeaderAverage { get; init; }
        internal required double LeaderStdDev { get; init; }
        internal required double PulseConfidence { get; init; }
        internal double PolarityConfidence { get; init; }
        internal TapeProfile Profile { get; init; } = TapeProfile.Normal1_1;
    }

    internal sealed class WavBlockRecoveryInfo
    {
        internal required SharpBlockCandidate Candidate { get; init; }
    }

    internal sealed class WavRecoveryInfo
    {
        internal required WavBlockRecoveryInfo Header { get; init; }
        internal required WavBlockRecoveryInfo Payload { get; init; }
        internal required bool ReconstructionUsed { get; init; }
    }

    internal readonly record struct WavAnalysisProgress(
        string Stage,
        double Fraction,
        long ProcessedFrames,
        long TotalFrames,
        int CandidateCount);

    internal sealed class WavHeuristicStatistics
    {
        internal required PcmAudioFormat Format { get; init; }
        internal required string SourceFormat { get; init; }
        internal required int HeaderCandidates { get; init; }
        internal required int PayloadCandidates { get; init; }
        internal required int ValidPayloadCandidates { get; init; }
        internal required int ResultRecords { get; init; }
        internal required int ReconstructedRecords { get; init; }
        internal required bool SelectiveRecoveryUsed { get; init; }
        internal required IReadOnlyList<WavRecoveryInfo> RecordRecoveries { get; init; }
        internal double DurationSeconds => Format.FrameCount / (double)Format.SampleRate;
    }

    internal sealed class WavHeuristicAnalysisResult
    {
        internal required IReadOnlyList<TapeRecord> Records { get; init; }
        internal required WavHeuristicStatistics Statistics { get; init; }
    }

    internal static class WavHeuristicAnalyzer
    {
        internal static IReadOnlyList<TapeRecord> ReadFile(string filePath) =>
            AnalyzeFile(filePath).Records;

        internal static WavHeuristicAnalysisResult AnalyzeFile(
            string filePath,
            IProgress<WavAnalysisProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using IPcmAudioStreamReader reader = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".wav" => new WavPcmStreamReader(filePath),
                ".flac" => new FlacPcmStreamReader(filePath),
                _ => throw new ArgumentException(
                    $"Unsupported heuristic audio extension: {Path.GetExtension(filePath)}",
                    nameof(filePath))
            };
            var candidates = new List<SharpBlockCandidate>();
            var expectedLengths = new HashSet<int>();
            var channels = Enumerable.Range(0, reader.Format.Channels)
                .Select(channel => new ChannelAnalyzer(
                    channel,
                    reader.Format.SampleRate,
                    candidates,
                    expectedLengths))
                .ToArray();

            long reportInterval = Math.Max(1, reader.Format.SampleRate / 5);
            progress?.Report(new WavAnalysisProgress(
                $"Scanning {reader.SourceFormat}",
                0,
                0,
                reader.Format.FrameCount,
                0));
            reader.ReadFrames((sample, left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                channels[0].Process(sample, left);
                if (channels.Length == 2)
                {
                    channels[1].Process(sample, right);
                }
                if ((sample % reportInterval) == 0)
                {
                    progress?.Report(new WavAnalysisProgress(
                        $"Scanning {reader.SourceFormat}",
                        reader.Format.FrameCount == 0
                            ? 0
                            : sample / (double)reader.Format.FrameCount,
                        sample,
                        reader.Format.FrameCount,
                        candidates.Count));
                }
            });
            foreach (ChannelAnalyzer channel in channels)
            {
                channel.Flush(reader.Format.FrameCount);
            }

            bool selectiveRecoveryUsed = false;
            IReadOnlyList<TapeRecord> records;
            IReadOnlyList<WavRecoveryInfo> recordRecoveries;
            int reconstructedRecords;
            try
            {
                progress?.Report(new WavAnalysisProgress(
                    "Selecting best blocks",
                    1,
                    reader.Format.FrameCount,
                    reader.Format.FrameCount,
                    candidates.Count));
                records = AssembleRecords(
                    candidates,
                    reader.Format.SampleRate,
                    out reconstructedRecords,
                    out recordRecoveries);
            }
            catch (InvalidDataException) when (
                candidates.Any(value => value.Kind == SharpBlockKind.Header && value.ChecksumValid) &&
                candidates.Any(value => value.Kind == SharpBlockKind.Payload && !value.ChecksumValid))
            {
                selectiveRecoveryUsed = true;
                records = SelectiveRecovery(
                    reader,
                    candidates,
                    progress,
                    cancellationToken,
                    out reconstructedRecords,
                    out recordRecoveries);
            }

            progress?.Report(new WavAnalysisProgress(
                "Completed",
                1,
                reader.Format.FrameCount,
                reader.Format.FrameCount,
                candidates.Count));
            return new WavHeuristicAnalysisResult
            {
                Records = records,
                Statistics = new WavHeuristicStatistics
                {
                    Format = reader.Format,
                    SourceFormat = reader.SourceFormat,
                    HeaderCandidates = candidates.Count(value => value.Kind == SharpBlockKind.Header),
                    PayloadCandidates = candidates.Count(value => value.Kind == SharpBlockKind.Payload),
                    ValidPayloadCandidates = candidates.Count(value =>
                        value.Kind == SharpBlockKind.Payload && value.ChecksumValid),
                    ResultRecords = records.Count,
                    ReconstructedRecords = reconstructedRecords,
                    SelectiveRecoveryUsed = selectiveRecoveryUsed,
                    RecordRecoveries = recordRecoveries
                }
            };
        }

        private static IReadOnlyList<TapeRecord> SelectiveRecovery(
            IPcmAudioStreamReader reader,
            List<SharpBlockCandidate> discoveryCandidates,
            IProgress<WavAnalysisProgress>? progress,
            CancellationToken cancellationToken,
            out int reconstructedRecords,
            out IReadOnlyList<WavRecoveryInfo> recordRecoveries)
        {
            List<SharpBlockCandidate> failures = discoveryCandidates
                .Where(value => value.Kind == SharpBlockKind.Payload && !value.ChecksumValid)
                .ToList();
            long context = reader.Format.SampleRate * 3L;
            long firstFrame = Math.Max(0, failures.Min(value => value.StartSample) - context);
            long lastFrame = Math.Min(
                reader.Format.FrameCount,
                failures.Max(value => value.EndSample) + context);
            var expectedLengths = discoveryCandidates
                .Where(value => value.Kind == SharpBlockKind.Header && value.ChecksumValid)
                .Select(value => (int)BinaryPrimitives.ReadUInt16LittleEndian(value.Data.AsSpan(18, 2)))
                .Concat(failures.Select(value => value.Data.Length))
                .Where(value => value > 0)
                .Distinct()
                .ToHashSet();

            var recoveryCandidates = new List<SharpBlockCandidate>();
            var channels = Enumerable.Range(0, reader.Format.Channels)
                .Select(channel => new ChannelAnalyzer(
                    channel,
                    reader.Format.SampleRate,
                    recoveryCandidates,
                    expectedLengths,
                    schmittScale: 0.65))
                .ToArray();
            progress?.Report(new WavAnalysisProgress(
                "Selective recovery",
                firstFrame / (double)reader.Format.FrameCount,
                firstFrame,
                reader.Format.FrameCount,
                discoveryCandidates.Count));
            reader.ReadFrames(firstFrame, lastFrame - firstFrame, (sample, left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                channels[0].Process(sample, left);
                if (channels.Length == 2)
                {
                    channels[1].Process(sample, right);
                }
            });
            foreach (ChannelAnalyzer channel in channels)
            {
                channel.Flush(lastFrame);
            }

            discoveryCandidates.AddRange(recoveryCandidates);
            return AssembleRecords(
                discoveryCandidates,
                reader.Format.SampleRate,
                out reconstructedRecords,
                out recordRecoveries);
        }

        private static IReadOnlyList<TapeRecord> AssembleRecords(
            List<SharpBlockCandidate> candidates,
            uint sampleRate,
            out int reconstructedRecords,
            out IReadOnlyList<WavRecoveryInfo> recordRecoveries)
        {
            reconstructedRecords = 0;
            var recoveries = new List<WavRecoveryInfo>();
            List<SharpBlockCandidate> headers = candidates
                .Where(value => value.Kind == SharpBlockKind.Header && value.ChecksumValid)
                .OrderBy(value => value.EndSample)
                .ToList();
            List<SharpBlockCandidate> payloads = candidates
                .Where(value => value.Kind == SharpBlockKind.Payload)
                .OrderBy(value => value.StartSample)
                .ToList();

            if (headers.Count == 0)
            {
                throw new InvalidDataException("No Sharp MZ signal found: no checksum-valid header was detected.");
            }

            var groups = new List<List<SharpBlockCandidate>>();
            foreach (SharpBlockCandidate header in headers)
            {
                List<SharpBlockCandidate>? current = groups.LastOrDefault();
                bool sameRecording = current != null &&
                    header.EndSample - current[^1].EndSample <= sampleRate * 30L &&
                    header.Data.AsSpan().SequenceEqual(current[0].Data);
                if (!sameRecording)
                {
                    current = [];
                    groups.Add(current);
                }
                current!.Add(header);
            }

            var records = new List<TapeRecord>();
            for (int index = 0; index < groups.Count; index++)
            {
                List<SharpBlockCandidate> headerGroup = groups[index];
                SharpBlockCandidate header = headerGroup
                    .OrderByDescending(Score)
                    .First();
                int expectedLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Data.AsSpan(18, 2));
                // Do not mistake a checksum-valid duplicate header for a payload
                // when the real payload also happens to be 128 bytes long.
                long afterHeader = headerGroup.Max(value => value.EndSample);
                long beforeNextHeader = index + 1 < groups.Count
                    ? groups[index + 1].Min(value => value.EndSample)
                    : long.MaxValue;
                List<SharpBlockCandidate> matchingPayloads = payloads
                    .Where(value => value.Data.Length == expectedLength &&
                        value.StartSample >= afterHeader && value.StartSample < beforeNextHeader)
                    .ToList();
                if (matchingPayloads.Count == 0)
                {
                    continue;
                }

                SharpBlockCandidate? payload = matchingPayloads
                    .Where(value => value.ChecksumValid)
                    .OrderByDescending(value => value.Inverted == header.Inverted)
                    .ThenByDescending(value => value.Channel == header.Channel)
                    .ThenByDescending(Score)
                    .FirstOrDefault();
                bool reconstructed = false;
                if (payload == null)
                {
                    payload = TryConsensus(matchingPayloads);
                    reconstructed = payload != null;
                    if (reconstructed)
                    {
                        reconstructedRecords++;
                    }
                }
                if (payload == null)
                {
                    throw new InvalidDataException(
                        "Checksum mismatch with no safe recovery: payload copies disagree or remain ambiguous.");
                }

                byte[] mzf = new byte[TapeRecord.HeaderLength + payload.Data.Length];
                header.Data.CopyTo(mzf, 0);
                payload.Data.CopyTo(mzf, TapeRecord.HeaderLength);
                using var stream = new MemoryStream(mzf, writable: false);
                using var binary = new BinaryReader(stream);
                TapeRecord record = new MZTFileReader().ReadMzfRecord(binary);
                record.Profile = header.Profile;
                record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                records.Add(record);

                recoveries.Add(new WavRecoveryInfo
                {
                    Header = new WavBlockRecoveryInfo { Candidate = header },
                    Payload = new WavBlockRecoveryInfo { Candidate = payload },
                    ReconstructionUsed = reconstructed
                });
            }

            if (records.Count == 0)
            {
                throw new InvalidDataException("Sharp MZ header found, but no matching payload was detected.");
            }
            recordRecoveries = recoveries;
            return records;
        }

        private static double Score(SharpBlockCandidate candidate)
        {
            double score = candidate.ChecksumValid ? 1_000_000 : 0;
            score += candidate.PulseConfidence * 1000;
            score += candidate.PolarityConfidence * 10;
            score -= candidate.LeaderStdDev;
            if (candidate.PulseMode == WavPulseMode.ZeroCrossing)
            {
                score += 2;
            }
            if (!candidate.Inverted)
            {
                score += 1;
            }
            return score;
        }

        private static SharpBlockCandidate? TryConsensus(List<SharpBlockCandidate> candidates)
        {
            if (candidates.Count < 2)
            {
                return null;
            }
            var checksumGroup = candidates
                .GroupBy(value => value.RecordedChecksum)
                .OrderByDescending(group => group.Count())
                .First();
            if (checksumGroup.Count() < 2)
            {
                return null;
            }

            int length = candidates[0].Data.Length;
            byte[] data = new byte[length];
            for (int byteIndex = 0; byteIndex < length; byteIndex++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    int mask = 1 << bit;
                    double balance = 0;
                    double weight = 0;
                    foreach (SharpBlockCandidate candidate in candidates)
                    {
                        double confidence = Math.Max(0.05, candidate.PulseConfidence);
                        weight += confidence;
                        balance += (candidate.Data[byteIndex] & mask) != 0 ? confidence : -confidence;
                    }
                    if (Math.Abs(balance) < weight * 0.25)
                    {
                        return null;
                    }
                    if (balance > 0)
                    {
                        data[byteIndex] |= (byte)mask;
                    }
                }
            }

            ushort calculated = CalculateChecksum(data);
            if (calculated != checksumGroup.Key)
            {
                return null;
            }
            SharpBlockCandidate best = candidates.OrderByDescending(Score).First();
            return new SharpBlockCandidate
            {
                Kind = SharpBlockKind.Payload,
                Channel = best.Channel,
                CopyIndex = best.CopyIndex,
                Inverted = best.Inverted,
                PulseMode = best.PulseMode,
                Data = data,
                ChecksumValid = true,
                RecordedChecksum = checksumGroup.Key,
                CalculatedChecksum = calculated,
                StartSample = candidates.Min(value => value.StartSample),
                EndSample = candidates.Max(value => value.EndSample),
                LeaderAverage = candidates.Average(value => value.LeaderAverage),
                LeaderStdDev = candidates.Average(value => value.LeaderStdDev),
                PulseConfidence = candidates.Average(value => value.PulseConfidence)
            };
        }

        private static ushort CalculateChecksum(ReadOnlySpan<byte> data)
        {
            uint checksum = 0;
            foreach (byte value in data)
            {
                checksum += (uint)BitOperations.PopCount(value);
            }
            return unchecked((ushort)checksum);
        }

        private sealed class ChannelAnalyzer
        {
            private readonly SignalPreprocessor preprocessor;
            private readonly PulseDetector zeroCrossing;
            private readonly PulseDetector schmitt;
            private readonly SharpTapeCandidateScanner zeroNormal;
            private readonly SharpTapeCandidateScanner zeroInverted;
            private readonly SharpTapeCandidateScanner schmittNormal;
            private readonly SharpTapeCandidateScanner schmittInverted;
            private readonly double schmittScale;

            internal ChannelAnalyzer(
                int channel,
                uint sampleRate,
                List<SharpBlockCandidate> output,
                HashSet<int> expectedLengths,
                double schmittScale = 1.0)
            {
                this.schmittScale = schmittScale;
                preprocessor = new SignalPreprocessor(sampleRate);
                zeroNormal = new SharpTapeCandidateScanner(channel, false, WavPulseMode.ZeroCrossing, sampleRate, output, expectedLengths);
                zeroInverted = new SharpTapeCandidateScanner(channel, true, WavPulseMode.ZeroCrossing, sampleRate, output, expectedLengths);
                schmittNormal = new SharpTapeCandidateScanner(channel, false, WavPulseMode.Schmitt, sampleRate, output, expectedLengths);
                schmittInverted = new SharpTapeCandidateScanner(channel, true, WavPulseMode.Schmitt, sampleRate, output, expectedLengths);
                zeroCrossing = new PulseDetector((level, duration, end, confidence) =>
                {
                    zeroNormal.Feed(duration, level, end, confidence);
                    zeroInverted.Feed(duration, !level, end, confidence);
                });
                schmitt = new PulseDetector((level, duration, end, confidence) =>
                {
                    schmittNormal.Feed(duration, level, end, confidence);
                    schmittInverted.Feed(duration, !level, end, confidence);
                });
                foreach (int length in expectedLengths)
                {
                    zeroNormal.AddExpectedBlockLength(length);
                    zeroInverted.AddExpectedBlockLength(length);
                    schmittNormal.AddExpectedBlockLength(length);
                    schmittInverted.AddExpectedBlockLength(length);
                }
            }

            internal void Process(long sampleIndex, int pcm)
            {
                (double filtered, double threshold) = preprocessor.Process(pcm / 8388608.0);
                zeroCrossing.Process(sampleIndex, filtered >= 0);
                schmitt.ProcessSchmitt(sampleIndex, filtered, threshold * schmittScale);
            }

            internal void Flush(long endSample)
            {
                zeroCrossing.Flush(endSample);
                schmitt.Flush(endSample);
            }
        }

        private sealed class SignalPreprocessor
        {
            private readonly double dcAlpha;
            private readonly double highPassAlpha;
            private readonly double envelopeAttack;
            private readonly double envelopeRelease;
            private double dc;
            private double previousCentered;
            private double highPassed;
            private double envelope;

            internal SignalPreprocessor(uint sampleRate)
            {
                dcAlpha = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.75));
                highPassAlpha = Math.Exp(-2.0 * Math.PI * 20.0 / sampleRate);
                envelopeAttack = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.005));
                envelopeRelease = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.250));
            }

            internal (double Filtered, double Threshold) Process(double sample)
            {
                dc += (sample - dc) * dcAlpha;
                double centered = sample - dc;
                highPassed = highPassAlpha * (highPassed + centered - previousCentered);
                previousCentered = centered;
                double magnitude = Math.Abs(highPassed);
                envelope += (magnitude - envelope) *
                    (magnitude > envelope ? envelopeAttack : envelopeRelease);
                return (highPassed, Math.Max(0.0025, envelope * 0.22));
            }
        }

        private sealed class PulseDetector
        {
            private readonly Action<bool, long, long, double> emit;
            private readonly WavPulseHistogram histogram = new();
            private bool? level;
            private long edgeSample;

            internal PulseDetector(Action<bool, long, long, double> emit) => this.emit = emit;

            internal void Process(long sampleIndex, bool current)
            {
                if (level == current)
                {
                    return;
                }
                if (level.HasValue && sampleIndex > edgeSample)
                {
                    long duration = sampleIndex - edgeSample;
                    histogram.Observe(duration, level.Value);
                    emit(level.Value, duration, sampleIndex, histogram.Confidence);
                }
                level = current;
                edgeSample = sampleIndex;
            }

            internal void ProcessSchmitt(long sampleIndex, double sample, double threshold)
            {
                bool current = level ?? sample >= 0;
                if (!current && sample >= threshold)
                {
                    current = true;
                }
                else if (current && sample <= -threshold)
                {
                    current = false;
                }
                Process(sampleIndex, current);
            }

            internal void Flush(long endSample)
            {
                if (level.HasValue && endSample > edgeSample)
                {
                    long duration = endSample - edgeSample;
                    histogram.Observe(duration, level.Value);
                    emit(level.Value, duration, endSample, histogram.Confidence);
                    edgeSample = endSample;
                }
            }
        }

        private sealed class WavPulseHistogram
        {
            private const int MaximumTrackedSamples = 2048;
            private readonly int[] high = new int[MaximumTrackedSamples + 1];
            private readonly int[] low = new int[MaximumTrackedSamples + 1];
            private int observations;

            internal double Confidence { get; private set; } = 0.5;

            internal void Observe(long duration, bool physicalHigh)
            {
                if (duration is <= 0 or > MaximumTrackedSamples)
                {
                    return;
                }
                int[] bins = physicalHigh ? high : low;
                if (bins[duration] < int.MaxValue)
                {
                    bins[duration]++;
                }
                observations++;
                if ((observations & 255) == 0)
                {
                    Confidence = CalculateClusterConfidence(high, low);
                }
            }

            private static double CalculateClusterConfidence(int[] high, int[] low)
            {
                int first = 0;
                int firstCount = 0;
                for (int index = 1; index < high.Length; index++)
                {
                    int count = high[index] + low[index];
                    if (count > firstCount)
                    {
                        first = index;
                        firstCount = count;
                    }
                }
                if (first == 0 || firstCount == 0)
                {
                    return 0.25;
                }

                int secondCount = 0;
                int minimumSecond = Math.Min(high.Length - 1, (first * 4 + 2) / 3);
                for (int index = minimumSecond; index < high.Length; index++)
                {
                    secondCount = Math.Max(secondCount, high[index] + low[index]);
                }
                double support = Math.Min(1.0, (firstCount + secondCount) / 64.0);
                double separation = secondCount == 0 ? 0.25 : 1.0;
                return 0.25 + (0.75 * support * separation);
            }
        }
    }

    internal sealed class SharpTapeCandidateScanner
    {
        private readonly int channel;
        private readonly bool inverted;
        private readonly WavPulseMode pulseMode;
        private readonly uint sampleRate;
        private readonly List<SharpBlockCandidate> output;
        private readonly HashSet<int> expectedLengths;
        private readonly SharpMzPulseDecoder headerDecoder = new();
        private readonly Dictionary<int, RawBlockScanner> blocks = [];
        private readonly List<(byte[] Header, double PolarityConfidence)> turboCopyHeaders = [];
        private double pulseConfidence = 0.5;

        internal SharpTapeCandidateScanner(
            int channel,
            bool inverted,
            WavPulseMode pulseMode,
            uint sampleRate,
            List<SharpBlockCandidate> output,
            HashSet<int> expectedLengths)
        {
            this.channel = channel;
            this.inverted = inverted;
            this.pulseMode = pulseMode;
            this.sampleRate = sampleRate;
            this.output = output;
            this.expectedLengths = expectedLengths;
            headerDecoder.BeginHeader();
        }

        internal void Feed(
            long durationSamples,
            bool physicalHigh,
            long endSample,
            double confidence)
        {
            pulseConfidence = confidence;
            foreach (int byteCount in expectedLengths)
            {
                EnsureBlockScanner(byteCount);
            }
            headerDecoder.FeedInterval(durationSamples, physicalHigh);
            while (headerDecoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
            {
                if (decoderEvent.Type == SharpMzDecoderEventType.HeaderValid)
                {
                    AcceptHeader(decoderEvent, endSample);
                    headerDecoder.BeginHeader();
                }
                else if (decoderEvent.Type == SharpMzDecoderEventType.HeaderInvalid)
                {
                    // Discovery must continue after every candidate instead of
                    // committing to the decoder's conventional duplicate-copy path.
                    headerDecoder.BeginHeader();
                }
            }

            foreach (RawBlockScanner block in blocks.Values.ToArray())
            {
                SharpBlockCandidate? candidate = block.Feed(
                    durationSamples,
                    physicalHigh,
                    endSample,
                    pulseConfidence);
                if (candidate == null)
                {
                    continue;
                }
                output.Add(candidate);
                if (candidate.ChecksumValid && candidate.Data.Length == 90 &&
                    SharpTapeProfileEncoder.IsTurboCopyLoader(candidate.Data))
                {
                    foreach ((byte[] Header, double PolarityConfidence) pending in turboCopyHeaders.ToArray())
                    {
                        byte[] recovered = SharpTapeImporter.RecoverTurboCopyHeader(
                            pending.Header,
                            candidate.Data,
                            out TapeProfile profile);
                        AddHeaderCandidate(
                            recovered,
                            profile,
                            decoderEvent: default,
                            endSample,
                            pending.PolarityConfidence);
                        AddExpectedBlockLength(BinaryPrimitives.ReadUInt16LittleEndian(recovered.AsSpan(18, 2)));
                        turboCopyHeaders.Remove(pending);
                    }
                }
            }
        }

        internal void AddExpectedBlockLength(int byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }
            expectedLengths.Add(byteCount);
            EnsureBlockScanner(byteCount);
        }

        private void AcceptHeader(SharpMzDecoderEvent decoderEvent, long endSample)
        {
            byte[] encoded = headerDecoder.ValidatedHeader!;
            if (SharpTapeImporter.IsTurboCopyHeader(encoded))
            {
                turboCopyHeaders.Add((
                    encoded,
                    headerDecoder.HeaderShortPhysicalHighX8 -
                        headerDecoder.HeaderShortPhysicalLowX8));
                AddExpectedBlockLength(90);
                return;
            }

            byte[] header = encoded;
            TapeProfile profile = SharpTapeImporter.ProfileFromWavTone(
                headerDecoder.HeaderShortPhysicalLowX8,
                headerDecoder.HeaderShortPhysicalHighX8,
                decoderEvent.LeaderPulses,
                sampleRate);
            if (SharpTapeImporter.TryRecoverIntercopyHeader(encoded, out byte[]? recoveredIc, out TapeProfile icProfile))
            {
                header = recoveredIc!;
                profile = icProfile;
            }
            else if (SharpTapeImporter.TryRecoverMz700FastHeader(encoded, out byte[]? recoveredMz700))
            {
                header = recoveredMz700!;
                profile = TapeProfile.Mz700_1_3;
            }
            else if (header[0] == 0 || (header[17] != 0x0D && header[17] != 0x00))
            {
                return;
            }

            AddHeaderCandidate(header, profile, decoderEvent, endSample);
            AddExpectedBlockLength(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)));
        }

        private void AddHeaderCandidate(
            byte[] header,
            TapeProfile profile,
            SharpMzDecoderEvent decoderEvent,
            long endSample,
            double? polarityConfidence = null)
        {
            output.Add(new SharpBlockCandidate
            {
                Kind = SharpBlockKind.Header,
                Channel = channel,
                CopyIndex = decoderEvent.CopyIndex,
                Inverted = inverted,
                PulseMode = pulseMode,
                Data = (byte[])header.Clone(),
                ChecksumValid = true,
                RecordedChecksum = decoderEvent.RecordedChecksum,
                CalculatedChecksum = decoderEvent.CalculatedChecksum,
                StartSample = endSample,
                EndSample = endSample,
                LeaderAverage = headerDecoder.LeaderAverage,
                LeaderStdDev = headerDecoder.LeaderStdDev,
                PulseConfidence = pulseConfidence * headerDecoder.PulseConfidence *
                    Math.Min(1.0, Math.Max(0.25, decoderEvent.LeaderPulses / 5500.0)),
                PolarityConfidence = polarityConfidence ??
                    (headerDecoder.HeaderShortPhysicalHighX8 -
                        headerDecoder.HeaderShortPhysicalLowX8),
                Profile = profile
            });
        }

        private void EnsureBlockScanner(int byteCount)
        {
            if (byteCount <= 0 || blocks.ContainsKey(byteCount))
            {
                return;
            }
            blocks.Add(byteCount, new RawBlockScanner(
                byteCount,
                channel,
                inverted,
                pulseMode,
                sampleRate));
        }

        private sealed class RawBlockScanner
        {
            private readonly int byteCount;
            private readonly int channel;
            private readonly bool inverted;
            private readonly WavPulseMode pulseMode;
            private readonly uint sampleRate;
            private readonly SharpMzPulseDecoder decoder = new();
            private readonly List<byte> data;
            private long startSample;
            private int copyIndex;

            internal RawBlockScanner(
                int byteCount,
                int channel,
                bool inverted,
                WavPulseMode pulseMode,
                uint sampleRate)
            {
                this.byteCount = byteCount;
                this.channel = channel;
                this.inverted = inverted;
                this.pulseMode = pulseMode;
                this.sampleRate = sampleRate;
                data = new List<byte>(byteCount);
                decoder.BeginRawBlock(byteCount);
            }

            internal SharpBlockCandidate? Feed(
                long duration,
                bool physicalHigh,
                long endSample,
                double pulseConfidence)
            {
                decoder.FeedInterval(duration, physicalHigh);
                while (decoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
                {
                    if (decoderEvent.Type == SharpMzDecoderEventType.DataByte)
                    {
                        if (decoderEvent.ByteIndex == 0)
                        {
                            data.Clear();
                            startSample = endSample;
                        }
                        if (decoderEvent.ByteIndex == data.Count)
                        {
                            data.Add(decoderEvent.Value);
                        }
                        continue;
                    }
                    if (decoderEvent.Type is not SharpMzDecoderEventType.BlockValid and
                        not SharpMzDecoderEventType.BlockInvalid)
                    {
                        continue;
                    }

                    byte[] bytes = data.Count == byteCount ? data.ToArray() : new byte[byteCount];
                    var candidate = new SharpBlockCandidate
                    {
                        Kind = SharpBlockKind.Payload,
                        Channel = channel,
                        CopyIndex = copyIndex++,
                        Inverted = inverted,
                        PulseMode = pulseMode,
                        Data = bytes,
                        ChecksumValid = decoderEvent.Type == SharpMzDecoderEventType.BlockValid,
                        RecordedChecksum = decoderEvent.RecordedChecksum,
                        CalculatedChecksum = decoderEvent.CalculatedChecksum,
                        StartSample = startSample,
                        EndSample = endSample,
                        LeaderAverage = decoder.LeaderAverage,
                        LeaderStdDev = decoder.LeaderStdDev,
                        PulseConfidence = pulseConfidence * decoder.PulseConfidence *
                            (decoderEvent.Type == SharpMzDecoderEventType.BlockValid ? 1.0 : 0.45)
                    };
                    data.Clear();
                    decoder.BeginRawBlock(byteCount);
                    return candidate;
                }
                return null;
            }
        }
    }
}
