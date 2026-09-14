using System;
using System.Buffers.Binary;
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
        private const int WavSampleRate = 44100;

        // NORMAL 1:1 monitor profiles from MZ-SD2CMT2-Reborn.
        private static readonly TapeProfile Mz800Profile = new(
            ShortHighMicroseconds: 250,
            ShortLowMicroseconds: 250,
            LongHighMicroseconds: 500,
            LongLowMicroseconds: 500,
            HeaderLeaderPulses: 6344,
            DataLeaderPulses: 6344,
            HeaderMarkLongPulses: 40,
            HeaderMarkShortPulses: 40,
            DataMarkLongPulses: 20,
            DataMarkShortPulses: 20,
            FinalMarkLongPulses: 2,
            TrailingLongPulses: 2);

        private static readonly TapeProfile Mz700Profile = new(
            ShortHighMicroseconds: 240,
            ShortLowMicroseconds: 264,
            LongHighMicroseconds: 464,
            LongLowMicroseconds: 494,
            HeaderLeaderPulses: 22000,
            DataLeaderPulses: 11000,
            HeaderMarkLongPulses: 40,
            HeaderMarkShortPulses: 40,
            DataMarkLongPulses: 20,
            DataMarkShortPulses: 20,
            FinalMarkLongPulses: 2,
            TrailingLongPulses: 2);

        public static void Export(
            string filePath,
            IReadOnlyList<(MZQFileHeader Header, MZQFileBody Body)> blocks,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            ExportCore(
                filePath,
                blocks.Select(block => (CreateMzfHeader(block.Header), block.Body.MzfBody)).ToList(),
                format,
                machine);
        }

        public static void Export(
            string filePath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(records);
            ExportCore(
                filePath,
                records.Select(record => (record.GetSerializedHeader(), record.Body.MzfBody)).ToList(),
                format,
                machine);
        }

        private static void ExportCore(
            string filePath,
            IReadOnlyList<(byte[] Header, byte[] Body)> blocks,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (blocks.Count == 0)
            {
                throw new InvalidOperationException("There are no MZF files to export.");
            }

            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
            using TapeSink sink = format switch
            {
                SharpTapeOutputFormat.Lep => new EdgeDurationSink(fileStream, 50),
                SharpTapeOutputFormat.L16 => new EdgeDurationSink(fileStream, 16),
                SharpTapeOutputFormat.Wav => new WavSink(fileStream, WavSampleRate),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            TapeProfile profile = machine switch
            {
                SharpTapeMachine.Mz800 => Mz800Profile,
                SharpTapeMachine.Mz700 => Mz700Profile,
                _ => throw new ArgumentOutOfRangeException(nameof(machine))
            };

            foreach (var (header, body) in blocks)
            {
                WriteConventionalRecord(sink, profile, header, body);
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

        private static void WriteConventionalRecord(
            TapeSink sink,
            TapeProfile profile,
            byte[] header,
            byte[] body)
        {
            WriteBlock(
                sink,
                profile,
                header,
                profile.HeaderLeaderPulses,
                profile.HeaderMarkLongPulses,
                profile.HeaderMarkShortPulses);
            WriteBlock(
                sink,
                profile,
                body,
                profile.DataLeaderPulses,
                profile.DataMarkLongPulses,
                profile.DataMarkShortPulses);
        }

        private static void WriteBlock(
            TapeSink sink,
            TapeProfile profile,
            byte[] data,
            int leaderPulses,
            int markLongPulses,
            int markShortPulses)
        {
            WritePulses(sink, profile, isLong: false, leaderPulses);
            WritePulses(sink, profile, isLong: true, markLongPulses);
            WritePulses(sink, profile, isLong: false, markShortPulses);
            WritePulses(sink, profile, isLong: true, profile.FinalMarkLongPulses);
            WriteData(sink, profile, data);
            WriteChecksum(sink, profile, ComputeChecksum(data));
            WritePulses(sink, profile, isLong: true, profile.TrailingLongPulses);
        }

        private static void WriteData(TapeSink sink, TapeProfile profile, byte[] data)
        {
            foreach (byte value in data)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    WritePulse(sink, profile, (value & (1 << bit)) != 0);
                }

                WritePulse(sink, profile, isLong: true);
            }
        }

        private static void WriteChecksum(TapeSink sink, TapeProfile profile, ushort checksum)
        {
            WriteData(sink, profile, new[] { (byte)(checksum >> 8), (byte)checksum });
        }

        private static void WritePulses(TapeSink sink, TapeProfile profile, bool isLong, int count)
        {
            for (int i = 0; i < count; i++)
            {
                WritePulse(sink, profile, isLong);
            }
        }

        private static void WritePulse(TapeSink sink, TapeProfile profile, bool isLong)
        {
            int highDuration = isLong ? profile.LongHighMicroseconds : profile.ShortHighMicroseconds;
            int lowDuration = isLong ? profile.LongLowMicroseconds : profile.ShortLowMicroseconds;
            sink.WriteInterval(high: true, highDuration);
            sink.WriteInterval(high: false, lowDuration);
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

        private static byte[] CreateMzfHeader(MZQFileHeader header)
        {
            byte[] data = new byte[128];
            data[0] = header.MzfFtype;
            Array.Copy(header.MzfFname, 0, data, 1, 16);
            data[17] = header.MzfFnameEnd;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22, 2), header.MzfExec);
            Array.Copy(header.MzfHeaderDescription, 0, data, 24, 104);
            return data;
        }

        private readonly record struct TapeProfile(
            int ShortHighMicroseconds,
            int ShortLowMicroseconds,
            int LongHighMicroseconds,
            int LongLowMicroseconds,
            int HeaderLeaderPulses,
            int DataLeaderPulses,
            int HeaderMarkLongPulses,
            int HeaderMarkShortPulses,
            int DataMarkLongPulses,
            int DataMarkShortPulses,
            int FinalMarkLongPulses,
            int TrailingLongPulses);

        private abstract class TapeSink : IDisposable
        {
            public abstract void WriteInterval(bool high, int durationMicroseconds);

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

            public override void WriteInterval(bool high, int durationMicroseconds)
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

            public override void WriteInterval(bool high, int durationMicroseconds)
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
