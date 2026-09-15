using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal enum SharpTapeOutputFormat
    {
        Lep,
        L16,
        Wav
    }

    internal enum SharpTapeMachine
    {
        Mz800,
        Mz700
    }

    internal static class SharpTapeExporter
    {
        // Required output rate. Fractional edge durations are preserved over
        // time by the WavSink quantization-error accumulator.
        internal const int WavSampleRate = 44100;

        private enum PulseRegion
        {
            Leader,
            TapeMark,
            Data
        }

        public static void Export(
            string filePath,
            IReadOnlyList<(MZQFileHeader Header, MZQFileBody Body)> blocks,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            List<TapeRecord> records = blocks.Select(block =>
            {
                TapeRecord record = TapeRecord.FromLegacy(block.Header, block.Body);
                record.Profile = machine == SharpTapeMachine.Mz700
                    ? TapeProfile.Mz700_1_1
                    : TapeProfile.Normal1_1;
                record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                return record;
            }).ToList();
            Export(filePath, records, format, machine);
        }

        public static IReadOnlyList<string> GetSeparateOutputPaths(
            string selectedPath,
            IReadOnlyList<TapeRecord> records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
            ArgumentNullException.ThrowIfNull(records);
            if (records.Count == 0)
            {
                throw new InvalidOperationException("There are no MZF files to export.");
            }

            string fullPath = Path.GetFullPath(selectedPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The output path has no directory.");
            string extension = Path.GetExtension(fullPath);
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            var result = new List<string>(records.Count);
            for (int index = 0; index < records.Count; index++)
            {
                string recordName = Utility.ConvertMzfNameToASCIIString(records[index].Header.MzfFname);
                string safeRecordName = SanitizeFilePart(recordName);
                string fileName = $"{baseName}_{index + 1:D2}_{safeRecordName}{extension}";
                result.Add(Path.Combine(directory, fileName));
            }
            return result;
        }

        public static IReadOnlyList<string> ExportSeparate(
            string selectedPath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine,
            bool overwrite)
        {
            IReadOnlyList<string> paths = GetSeparateOutputPaths(selectedPath, records);
            if (!overwrite)
            {
                string? existingPath = paths.FirstOrDefault(File.Exists);
                if (existingPath != null)
                {
                    throw new IOException($"The separate output file already exists: {existingPath}");
                }
            }

            for (int index = 0; index < records.Count; index++)
            {
                if (overwrite && File.Exists(paths[index]))
                {
                    File.Delete(paths[index]);
                }
                Export(paths[index], new[] { records[index] }, format, machine);
            }
            return paths;
        }

        public static void Export(
            string filePath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(records);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (records.Count == 0)
            {
                throw new InvalidOperationException("There are no MZF files to export.");
            }

            // Build every plan before creating the destination so an invalid
            // loader cannot leave a truncated output file behind.
            List<IReadOnlyList<SharpTapeStage>> plans = records
                .Select(record => SharpTapeProfileEncoder.Build(record, machine))
                .ToList();

            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
            using TapeSink sink = format switch
            {
                SharpTapeOutputFormat.Lep => new EdgeDurationSink(fileStream, 50),
                SharpTapeOutputFormat.L16 => new EdgeDurationSink(fileStream, 16),
                SharpTapeOutputFormat.Wav => new WavSink(fileStream, WavSampleRate),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            foreach (IReadOnlyList<SharpTapeStage> plan in plans)
            {
                foreach (SharpTapeStage stage in plan)
                {
                    WriteStage(sink, stage);
                }
            }

            sink.Complete();
        }

        public static SharpTapeOutputFormat GetFormat(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".lep" => SharpTapeOutputFormat.Lep,
                ".l16" => SharpTapeOutputFormat.L16,
                ".wav" => SharpTapeOutputFormat.Wav,
                _ => throw new ArgumentException($"Unsupported tape output extension: {extension}", nameof(extension))
            };
        }

        private static string SanitizeFilePart(string value)
        {
            value = value.Trim().TrimEnd('\0', '\r', '\n', '.');
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = new string(value
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray())
                .Trim()
                .TrimEnd('.');
            if (string.IsNullOrWhiteSpace(result))
            {
                return "unnamed";
            }
            return result.Length <= 64 ? result : result[..64];
        }

        private static void WriteStage(TapeSink sink, SharpTapeStage stage)
        {
            WriteDelay(sink, stage.InvertSignal, stage.DelayBeforeMilliseconds);
            WriteBlock(
                sink,
                stage.Pulses,
                stage.Data,
                stage.LeaderShortPulses,
                stage.MarkLongPulses,
                stage.MarkShortPulses,
                stage.FinalMarkLongPulses,
                stage.TrailingPulses,
                stage.TrailingPulseIsLong,
                stage.InvertSignal);
        }

        private static void WriteBlock(
            TapeSink sink,
            SharpPulseProfile profile,
            byte[] data,
            int leaderPulses,
            int markLongPulses,
            int markShortPulses,
            int finalMarkLongPulses,
            int trailingPulses,
            bool trailingPulseIsLong,
            bool invertSignal)
        {
            WritePulses(
                sink, profile, isLong: false, leaderPulses, invertSignal, PulseRegion.Leader);
            WritePulses(
                sink, profile, isLong: true, markLongPulses, invertSignal, PulseRegion.TapeMark);
            WritePulses(
                sink, profile, isLong: false, markShortPulses, invertSignal, PulseRegion.TapeMark);
            WritePulses(
                sink, profile, isLong: true, finalMarkLongPulses, invertSignal, PulseRegion.TapeMark);
            WriteFramedData(
                sink, profile, data, ComputeChecksum(data), trailingPulses, trailingPulseIsLong, invertSignal);
        }

        private static void WriteFramedData(
            TapeSink sink,
            SharpPulseProfile profile,
            byte[] data,
            ushort checksum,
            int trailingPulses,
            bool trailingPulseIsLong,
            bool invertSignal)
        {
            int framedPulseCount = checked((data.Length + 2) * 9);
            int totalPulseCount = checked(framedPulseCount + trailingPulses);
            for (int pulseIndex = 0; pulseIndex < totalPulseCount; pulseIndex++)
            {
                bool isLong = GetFramedPulse(data, checksum, framedPulseCount, pulseIndex, trailingPulseIsLong);
                bool nextIsLong = pulseIndex + 1 < totalPulseCount &&
                    GetFramedPulse(data, checksum, framedPulseCount, pulseIndex + 1, trailingPulseIsLong);
                WritePulse(sink, profile, isLong, invertSignal, PulseRegion.Data, nextIsLong);
            }
        }

        private static bool GetFramedPulse(
            byte[] data,
            ushort checksum,
            int framedPulseCount,
            int pulseIndex,
            bool trailingPulseIsLong)
        {
            if (pulseIndex >= framedPulseCount)
            {
                return trailingPulseIsLong;
            }

            int byteIndex = pulseIndex / 9;
            int pulseInByte = pulseIndex % 9;
            if (pulseInByte == 8)
            {
                return true;
            }

            byte value = byteIndex < data.Length
                ? data[byteIndex]
                : byteIndex == data.Length ? (byte)(checksum >> 8) : (byte)checksum;
            return (value & (1 << (7 - pulseInByte))) != 0;
        }

        private static void WritePulses(
            TapeSink sink,
            SharpPulseProfile profile,
            bool isLong,
            int count,
            bool invertSignal,
            PulseRegion region)
        {
            for (int i = 0; i < count; i++)
            {
                WritePulse(sink, profile, isLong, invertSignal, region, nextIsLong: isLong);
            }
        }

        private static void WritePulse(
            TapeSink sink,
            SharpPulseProfile profile,
            bool isLong,
            bool invertSignal,
            PulseRegion region,
            bool nextIsLong)
        {
            double highDuration = isLong ? profile.LongHighMicroseconds : profile.ShortHighMicroseconds;
            double lowDuration = GetLowDuration(profile, isLong, region, nextIsLong);
            if (invertSignal)
            {
                sink.WriteInterval(high: false, lowDuration);
                sink.WriteInterval(high: true, highDuration);
            }
            else
            {
                sink.WriteInterval(high: true, highDuration);
                sink.WriteInterval(high: false, lowDuration);
            }
        }

        private static double GetLowDuration(
            SharpPulseProfile profile,
            bool isLong,
            PulseRegion region,
            bool nextIsLong)
        {
            if (profile.TimingSource != SharpPulseTimingSource.Mz800Rom1Z013B)
            {
                return isLong ? profile.LongLowMicroseconds : profile.ShortLowMicroseconds;
            }

            return region switch
            {
                PulseRegion.Leader when !isLong => SharpTapeProfileEncoder.RomTicks(918),
                PulseRegion.TapeMark when isLong => SharpTapeProfileEncoder.RomTicks(1728),
                PulseRegion.TapeMark => SharpTapeProfileEncoder.RomTicks(908),
                PulseRegion.Data when isLong && nextIsLong => SharpTapeProfileEncoder.RomTicks(1716),
                PulseRegion.Data when isLong => SharpTapeProfileEncoder.RomTicks(1726),
                PulseRegion.Data when nextIsLong => SharpTapeProfileEncoder.RomTicks(896),
                PulseRegion.Data => SharpTapeProfileEncoder.RomTicks(906),
                _ => throw new InvalidOperationException("Invalid native MZ-800 pulse context.")
            };
        }

        private static void WriteDelay(TapeSink sink, bool invertSignal, int milliseconds)
        {
            for (int index = 0; index < milliseconds; index++)
            {
                sink.WriteInterval(high: invertSignal, durationMicroseconds: 1000);
            }
        }

        private static ushort ComputeChecksum(byte[] data)
        {
            uint checksum = 0;
            foreach (byte value in data)
            {
                byte remaining = value;
                while (remaining != 0)
                {
                    checksum += (uint)(remaining & 1);
                    remaining >>= 1;
                }
            }

            return unchecked((ushort)checksum);
        }

        private abstract class TapeSink : IDisposable
        {
            public abstract void WriteInterval(bool high, double durationMicroseconds);

            public abstract void Complete();

            public abstract void Dispose();
        }

        private sealed class EdgeDurationSink : TapeSink
        {
            private readonly BinaryWriter writer;
            private readonly int unitMicroseconds;
            private double quantizationError;

            public EdgeDurationSink(Stream stream, int unitMicroseconds)
            {
                writer = new BinaryWriter(new BufferedStream(stream, 65536), System.Text.Encoding.UTF8, leaveOpen: true);
                this.unitMicroseconds = unitMicroseconds;
            }

            public override void WriteInterval(bool high, double durationMicroseconds)
            {
                double exactUnits = ((double)durationMicroseconds / unitMicroseconds) + quantizationError;
                int units = Math.Max(1, (int)Math.Round(exactUnits, MidpointRounding.AwayFromZero));
                quantizationError = exactUnits - units;

                if (units > 127)
                {
                    throw new InvalidOperationException("A LEP/L16 interval exceeded the supported 127-unit duration.");
                }

                sbyte signedUnits = checked((sbyte)(high ? units : -units));
                writer.Write(unchecked((byte)signedUnits));
            }

            public override void Complete()
            {
                writer.Flush();
            }

            public override void Dispose()
            {
                writer.Dispose();
            }
        }

        private sealed class WavSink : TapeSink
        {
            private const byte HighSample = 80;
            private const byte LowSample = 176;

            private readonly FileStream stream;
            private readonly BinaryWriter writer;
            private readonly byte[] sampleBuffer = new byte[65536];
            private readonly int sampleRate;
            private int bufferedSamples;
            private long totalSamples;
            private double quantizationError;

            public WavSink(FileStream stream, int sampleRate)
            {
                this.stream = stream;
                this.sampleRate = sampleRate;
                writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                WriteHeader(dataSize: 0);
            }

            public override void WriteInterval(bool high, double durationMicroseconds)
            {
                double exactSamples = ((double)durationMicroseconds * sampleRate / 1_000_000) + quantizationError;
                int samples = Math.Max(1, (int)Math.Round(exactSamples, MidpointRounding.AwayFromZero));
                quantizationError = exactSamples - samples;
                byte value = high ? HighSample : LowSample;

                for (int i = 0; i < samples; i++)
                {
                    sampleBuffer[bufferedSamples++] = value;
                    if (bufferedSamples == sampleBuffer.Length)
                    {
                        FlushSamples();
                    }
                }

                totalSamples += samples;
            }

            public override void Complete()
            {
                FlushSamples();
                if (totalSamples > uint.MaxValue - 36)
                {
                    throw new InvalidOperationException("The generated WAV exceeds the 4 GiB RIFF size limit.");
                }

                stream.Position = 0;
                WriteHeader(checked((uint)totalSamples));
                writer.Flush();
            }

            public override void Dispose()
            {
                writer.Dispose();
            }

            private void FlushSamples()
            {
                if (bufferedSamples == 0)
                {
                    return;
                }

                writer.Write(sampleBuffer, 0, bufferedSamples);
                bufferedSamples = 0;
            }

            private void WriteHeader(uint dataSize)
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36u + dataSize);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16u);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((uint)sampleRate);
                writer.Write((uint)sampleRate);
                writer.Write((ushort)1);
                writer.Write((ushort)8);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
            }
        }
    }

    internal static class SharpTapeImporter
    {
        private readonly record struct SignalRun(bool Level, double Microseconds);
        private enum PendingStage { Body, TurboCopyLoader }
        private static ReadOnlySpan<byte> TurboCopyTag => [0x5B, 0x96, 0xA5, 0x9D, 0x9A, 0xB7, 0x5D, 0x00];

        public static IReadOnlyList<TapeRecord> ReadFile(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            IReadOnlyList<SignalRun> runs = extension switch
            {
                ".lep" => ReadEdgeRuns(File.ReadAllBytes(filePath), 50),
                ".l16" => ReadEdgeRuns(File.ReadAllBytes(filePath), 16),
                ".wav" => ReadWavRuns(File.ReadAllBytes(filePath)),
                _ => throw new ArgumentException($"Unsupported tape input extension: {extension}", nameof(filePath))
            };
            return DecodeRecords(runs);
        }

        private static IReadOnlyList<TapeRecord> DecodeRecords(IReadOnlyList<SignalRun> runs)
        {
            var records = new List<TapeRecord>();
            int runIndex = 0;
            byte[]? header = null;
            TapeProfile profile = TapeProfile.Normal1_1;
            PendingStage pendingStage = PendingStage.Body;

            while (TryFindBlock(runs, runIndex, out int blockStart, out int dataStart, out double threshold, out double shortPeriod))
            {
                bool isHeader = header == null;
                int dataLength = isHeader
                    ? TapeRecord.HeaderLength
                    : pendingStage == PendingStage.TurboCopyLoader
                        ? 90
                        : BinaryPrimitives.ReadUInt16LittleEndian(header!.AsSpan(18, 2));
                byte[] data = DecodeBlockData(runs, dataStart, dataLength, threshold, out int nextRun);

                if (isHeader)
                {
                    if (data[0] == 0 || (data[17] != 0x0D && data[17] != 0x00))
                    {
                        runIndex = blockStart + 2;
                        continue;
                    }
                    header = data;
                    profile = ProfileFromShortPeriod(shortPeriod);
                    if (TryRecoverIntercopyHeader(data, out byte[]? recoveredIc, out TapeProfile icProfile))
                    {
                        header = recoveredIc;
                        profile = icProfile;
                    }
                    else if (IsTurboCopyHeader(data))
                    {
                        pendingStage = PendingStage.TurboCopyLoader;
                    }
                    else if (TryRecoverMz700FastHeader(data, out byte[]? recoveredMz700))
                    {
                        header = recoveredMz700;
                        profile = TapeProfile.Mz700_1_3;
                    }
                }
                else if (pendingStage == PendingStage.TurboCopyLoader)
                {
                    header = RecoverTurboCopyHeader(header!, data, out profile);
                    pendingStage = PendingStage.Body;
                }
                else
                {
                    byte[] mzf = new byte[TapeRecord.HeaderLength + data.Length];
                    header!.CopyTo(mzf, 0);
                    data.CopyTo(mzf, TapeRecord.HeaderLength);
                    using var stream = new MemoryStream(mzf, writable: false);
                    using var reader = new BinaryReader(stream);
                    TapeRecord record = new MZTFileReader().ReadMzfRecord(reader);
                    record.Profile = profile;
                    record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                    records.Add(record);
                    header = null;
                    pendingStage = PendingStage.Body;
                }
                runIndex = nextRun;
            }

            if (header != null)
            {
                ushort expectedBody = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
                throw new InvalidDataException(
                    $"The tape waveform contains a header without its {expectedBody}-byte data block.");
            }
            if (records.Count == 0)
            {
                throw new InvalidDataException("The tape waveform does not contain a complete Sharp MZ file.");
            }
            return records;
        }

        private static bool TryRecoverIntercopyHeader(
            byte[] encoded,
            out byte[]? recovered,
            out TapeProfile profile)
        {
            recovered = null;
            profile = TapeProfile.Normal1_1;
            if (encoded[0] != 0xBB || encoded[24] != 0x01)
            {
                return false;
            }
            profile = encoded[25] switch
            {
                0x20 => TapeProfile.Ic1_2,
                0x16 => TapeProfile.Ic1_3,
                0x11 => TapeProfile.Ic1_4,
                _ => throw new InvalidDataException($"Unsupported Intercopy speed marker ${encoded[25]:X2}.")
            };
            recovered = (byte[])encoded.Clone();
            recovered[0] = 0x01;
            encoded.AsSpan(26, 6).CopyTo(recovered.AsSpan(18, 6));
            recovered.AsSpan(24).Clear();
            return true;
        }

        private static bool IsTurboCopyHeader(byte[] header) =>
            header.AsSpan(24, TurboCopyTag.Length).SequenceEqual(TurboCopyTag);

        private static byte[] RecoverTurboCopyHeader(
            byte[] encodedHeader,
            byte[] loader,
            out TapeProfile profile)
        {
            if (loader.Length != 90)
            {
                throw new InvalidDataException("Invalid TurboCopy loader length.");
            }
            profile = loader[0x4B] switch
            {
                0x29 => TapeProfile.Tc1_2,
                0x1B => TapeProfile.Tc1_3,
                _ => throw new InvalidDataException($"Unsupported TurboCopy speed marker ${loader[0x4B]:X2}.")
            };
            byte[] recovered = (byte[])encodedHeader.Clone();
            recovered[0] = loader[0x4C];
            loader.AsSpan(0x4D, 13).CopyTo(recovered.AsSpan(18, 13));
            recovered[31] = 0;
            return recovered;
        }

        private static bool TryRecoverMz700FastHeader(byte[] encoded, out byte[]? recovered)
        {
            recovered = null;
            if (encoded[0] != 0x01 ||
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(18, 2)) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(22, 2)) != 0xD080)
            {
                return false;
            }

            int sizeInstruction = FindInstruction(encoded, 24, 0x21, 0x22, 0x02, 0x11);
            int loadInstruction = FindInstruction(encoded, 24, 0x21, 0x22, 0x04, 0x11);
            int jump = -1;
            for (int index = 24; index <= encoded.Length - 7; index++)
            {
                if (encoded[index] == 0xAF && encoded[index + 1] == 0xD3 &&
                    encoded[index + 2] == 0xE4 && encoded[index + 3] == 0xC3)
                {
                    jump = index + 3;
                    break;
                }
            }
            if (sizeInstruction < 0 || loadInstruction < 0 || jump < 0 || jump + 3 >= encoded.Length)
            {
                return false;
            }

            recovered = new byte[TapeRecord.HeaderLength];
            recovered[0] = 0x01;
            recovered[17] = 0x0D;
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(18, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(sizeInstruction + 1, 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(20, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(loadInstruction + 1, 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(22, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(jump + 1, 2)));

            int nameLength = Math.Min(16, encoded.Length - (jump + 3));
            for (int index = 0; index < nameLength; index++)
            {
                if (!SharpTapeProfileEncoder.TryDecodeQadcn(encoded[jump + 3 + index], out byte value))
                {
                    break;
                }
                recovered[1 + index] = value;
            }
            return true;
        }

        private static int FindInstruction(
            byte[] data,
            int start,
            byte opcode,
            byte followingOpcode,
            byte addressLow,
            byte addressHigh)
        {
            for (int index = start; index <= data.Length - 6; index++)
            {
                if (data[index] == opcode && data[index + 3] == followingOpcode &&
                    data[index + 4] == addressLow && data[index + 5] == addressHigh)
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool TryFindBlock(
            IReadOnlyList<SignalRun> runs,
            int searchStart,
            out int blockStart,
            out int dataStart,
            out double threshold,
            out double shortPeriod)
        {
            blockStart = dataStart = 0;
            threshold = shortPeriod = 0;
            int currentSearch = Math.Max(0, searchStart);
            while (currentSearch <= runs.Count - 2200)
            {
                int evenCandidate = FindStableLeader(runs, currentSearch, 0);
                int oddCandidate = FindStableLeader(runs, currentSearch, 1);
                int preferred = (currentSearch & 1) == 0 ? evenCandidate : oddCandidate;
                int alternate = (currentSearch & 1) == 0 ? oddCandidate : evenCandidate;
                int candidate = preferred >= 0 ? preferred : alternate;
                if (candidate < 0)
                {
                    return false;
                }

                double sample = AveragePeriod(runs, candidate, 64);

                int leaderPulses = 0;
                while (candidate + ((leaderPulses + 1) * 2) <= runs.Count)
                {
                    double period = Period(runs, candidate + (leaderPulses * 2));
                    if (period < sample * 0.65 || period > sample * 1.35)
                    {
                        break;
                    }
                    leaderPulses++;
                }
                if (leaderPulses < 1000)
                {
                    currentSearch = candidate + 2;
                    continue;
                }

                int markStart = candidate + leaderPulses * 2;
                if (markStart + 24 >= runs.Count)
                {
                    return false;
                }
                double longPeriod = AveragePeriod(runs, markStart, 10);
                double split = (sample + longPeriod) / 2.0;
                if (longPeriod < sample * 1.45)
                {
                    currentSearch = candidate + 2;
                    continue;
                }

                int longMark = CountPeriods(runs, markStart, value => value > split);
                int shortMarkStart = markStart + longMark * 2;
                int shortMark = CountPeriods(runs, shortMarkStart, value => value < split);
                int finalMarkStart = shortMarkStart + shortMark * 2;
                if (longMark < 10 || shortMark < 10 ||
                    finalMarkStart + 4 > runs.Count ||
                    Period(runs, finalMarkStart) <= split ||
                    Period(runs, finalMarkStart + 2) <= split)
                {
                    currentSearch = candidate + 2;
                    continue;
                }

                blockStart = candidate;
                dataStart = finalMarkStart + 4;
                threshold = split;
                shortPeriod = sample;
                return true;
            }
            return false;
        }

        private static int FindStableLeader(IReadOnlyList<SignalRun> runs, int searchStart, int parity)
        {
            int current = searchStart;
            if ((current & 1) != parity)
            {
                current++;
            }

            int sequenceStart = current;
            int count = 0;
            double baseline = 0;
            for (; current + 1 < runs.Count; current += 2)
            {
                double value = Period(runs, current);
                if (value is >= 120 and <= 1200 &&
                    (count == 0 || (value >= baseline * 0.65 && value <= baseline * 1.35)))
                {
                    if (count == 0)
                    {
                        sequenceStart = current;
                        baseline = value;
                    }
                    count++;
                    if (count >= 1000)
                    {
                        return sequenceStart;
                    }
                }
                else
                {
                    count = 0;
                    baseline = 0;
                }
            }
            return -1;
        }

        private static byte[] DecodeBlockData(
            IReadOnlyList<SignalRun> runs,
            int dataStart,
            int dataLength,
            double threshold,
            out int nextRun)
        {
            int encodedLength = checked(dataLength + 2);
            int requiredRuns = checked(encodedLength * 18 + 4);
            if (dataStart > runs.Count - requiredRuns)
            {
                throw new InvalidDataException("The tape waveform ends inside a data block.");
            }

            byte[] decoded = new byte[encodedLength];
            int current = dataStart;
            for (int byteIndex = 0; byteIndex < encodedLength; byteIndex++)
            {
                byte value = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (byte)((value << 1) | (Period(runs, current) > threshold ? 1 : 0));
                    current += 2;
                }
                if (Period(runs, current) <= threshold)
                {
                    throw new InvalidDataException("The tape waveform has a missing LONG byte-sync pulse.");
                }
                current += 2;
                decoded[byteIndex] = value;
            }

            ushort expected = ComputeChecksum(decoded.AsSpan(0, dataLength));
            ushort actual = BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(dataLength, 2));
            if (actual != expected)
            {
                throw new InvalidDataException($"Tape checksum mismatch: expected {expected:X4}, found {actual:X4}.");
            }

            nextRun = current + 4;
            return decoded[..dataLength];
        }

        private static ushort ComputeChecksum(ReadOnlySpan<byte> data)
        {
            uint checksum = 0;
            foreach (byte value in data)
            {
                checksum += (uint)System.Numerics.BitOperations.PopCount(value);
            }
            return unchecked((ushort)checksum);
        }

        private static TapeProfile ProfileFromShortPeriod(double period) => period switch
        {
            < 178 => TapeProfile.Mz700_1_3,
            < 203 => TapeProfile.Normal1_4,
            < 230 => TapeProfile.Normal1_3,
            < 340 => TapeProfile.Normal1_2,
            > 500 => TapeProfile.Mz700_1_1,
            _ => TapeProfile.Normal1_1
        };

        private static int CountPeriods(IReadOnlyList<SignalRun> runs, int start, Func<double, bool> predicate)
        {
            int count = 0;
            while (start + (count + 1) * 2 <= runs.Count && predicate(Period(runs, start + count * 2)))
            {
                count++;
            }
            return count;
        }

        private static double AveragePeriod(IReadOnlyList<SignalRun> runs, int start, int count)
        {
            if (start < 0 || start + count * 2 > runs.Count)
            {
                return double.NaN;
            }
            double total = 0;
            for (int index = 0; index < count; index++)
            {
                total += Period(runs, start + index * 2);
            }
            return total / count;
        }

        private static double Period(IReadOnlyList<SignalRun> runs, int runIndex) =>
            runs[runIndex].Microseconds + runs[runIndex + 1].Microseconds;

        private static IReadOnlyList<SignalRun> ReadEdgeRuns(byte[] bytes, int unitMicroseconds)
        {
            var runs = new List<SignalRun>(bytes.Length);
            foreach (byte raw in bytes)
            {
                int signed = unchecked((sbyte)raw);
                if (signed == 0)
                {
                    throw new InvalidDataException("A LEP/L16 interval cannot be zero.");
                }
                // Every byte is one edge interval. Keep adjacent intervals separate:
                // some real encoders repeat the same polarity around block gaps.
                runs.Add(new SignalRun(signed > 0, Math.Abs(signed) * unitMicroseconds));
            }
            return runs;
        }

        private static IReadOnlyList<SignalRun> ReadWavRuns(byte[] wav)
        {
            if (wav.Length < 12 ||
                !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("The file is not a RIFF/WAVE stream.");
            }

            ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
            uint sampleRate = 0;
            ReadOnlySpan<byte> data = default;
            int position = 12;
            while (position <= wav.Length - 8)
            {
                ReadOnlySpan<byte> id = wav.AsSpan(position, 4);
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4, 4)));
                position += 8;
                if (length < 0 || position > wav.Length - length)
                {
                    throw new InvalidDataException("The WAV chunk extends beyond the end of the file.");
                }
                if (id.SequenceEqual("fmt "u8) && length >= 16)
                {
                    ReadOnlySpan<byte> chunk = wav.AsSpan(position, length);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                }
                else if (id.SequenceEqual("data"u8))
                {
                    data = wav.AsSpan(position, length);
                }
                position += length + (length & 1);
            }

            if (format != 1 || channels == 0 || sampleRate == 0 || blockAlign == 0 ||
                bits is not 8 and not 16 || data.IsEmpty)
            {
                throw new NotSupportedException("Only non-empty 8-bit or 16-bit PCM WAV input is supported.");
            }

            var runs = new List<SignalRun>();
            bool? level = null;
            int samples = 0;
            for (int offset = 0; offset <= data.Length - blockAlign; offset += blockAlign)
            {
                bool current = bits == 8
                    ? data[offset] < 128
                    : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)) >= 0;
                if (level == current)
                {
                    samples++;
                    continue;
                }
                if (level.HasValue)
                {
                    AddRun(runs, level.Value, samples * 1_000_000.0 / sampleRate);
                }
                level = current;
                samples = 1;
            }
            if (level.HasValue)
            {
                AddRun(runs, level.Value, samples * 1_000_000.0 / sampleRate);
            }
            return runs;
        }

        private static void AddRun(List<SignalRun> runs, bool level, double microseconds)
        {
            if (runs.Count > 0 && runs[^1].Level == level)
            {
                SignalRun previous = runs[^1];
                runs[^1] = previous with { Microseconds = previous.Microseconds + microseconds };
            }
            else
            {
                runs.Add(new SignalRun(level, microseconds));
            }
        }
    }
}
