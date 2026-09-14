using System;
using System.Collections.Generic;
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
}
