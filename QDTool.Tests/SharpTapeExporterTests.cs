namespace QDTool.Tests;

using System.Buffers.Binary;
using System.Diagnostics;

public class SharpTapeExporterTests
{
    [Theory]
    [InlineData(".lep", 0)]
    [InlineData(".L16", 1)]
    [InlineData(".Wav", 2)]
    public void GetFormat_RecognizesSupportedExtension(
        string extension,
        int expected)
    {
        Assert.Equal((SharpTapeOutputFormat)expected, SharpTapeExporter.GetFormat(extension));
    }

    [Fact]
    public void GetFormat_RejectsUnsupportedExtension()
    {
        Assert.Throws<ArgumentException>(() => SharpTapeExporter.GetFormat(".mp3"));
    }

    [Fact]
    public void Export_RejectsEmptyBlockListBeforeCreatingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.wav");

        Assert.Throws<InvalidOperationException>(
            () => SharpTapeExporter.Export(
                path,
                Array.Empty<TapeRecord>(),
                SharpTapeOutputFormat.Wav));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0, 35740L)]
    [InlineData(1, 35740L)]
    public void Export_L16UsesOneHeaderAndOneDataCopy(
        int machineValue,
        long expectedLength)
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.l16");
        var block = CreateTestBlock();
        var machine = (SharpTapeMachine)machineValue;

        try
        {
            SharpTapeExporter.Export(path, [block], SharpTapeOutputFormat.L16, machine);

            Assert.Equal(expectedLength, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Export_UsesExplicitPerRecordSpeed(int formatValue)
    {
        string normalPath = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-normal.tmp");
        string fastPath = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-fast.tmp");
        TapeRecord normal = CreateTestRecord(TapeProfile.Normal1_1);
        TapeRecord fast = CreateTestRecord(TapeProfile.Normal1_2);
        var format = (SharpTapeOutputFormat)formatValue;

        try
        {
            SharpTapeExporter.Export(normalPath, [normal], format, SharpTapeMachine.Mz800);
            SharpTapeExporter.Export(fastPath, [fast], format, SharpTapeMachine.Mz800);

            Assert.NotEqual(File.ReadAllBytes(normalPath), File.ReadAllBytes(fastPath));
            if (format == SharpTapeOutputFormat.Wav)
            {
                Assert.True(new FileInfo(fastPath).Length < new FileInfo(normalPath).Length);
            }
        }
        finally
        {
            File.Delete(normalPath);
            File.Delete(fastPath);
        }
    }

    [Fact]
    public void Export_DoesNotOverrideExplicitProfileWithDialogMachine()
    {
        string mz800Path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-mz800.l16");
        string mz700Path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-mz700.l16");
        TapeRecord record = CreateTestRecord(TapeProfile.Normal1_3);

        try
        {
            SharpTapeExporter.Export(mz800Path, [record], SharpTapeOutputFormat.L16, SharpTapeMachine.Mz800);
            SharpTapeExporter.Export(mz700Path, [record], SharpTapeOutputFormat.L16, SharpTapeMachine.Mz700);

            Assert.Equal(File.ReadAllBytes(mz800Path), File.ReadAllBytes(mz700Path));
        }
        finally
        {
            File.Delete(mz800Path);
            File.Delete(mz700Path);
        }
    }

    [Fact]
    public void UnionExport_UsesProfileOfEveryRecord()
    {
        string mixedPath = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-mixed.l16");
        string normalPath = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}-normal.l16");
        TapeRecord first = CreateTestRecord(TapeProfile.Normal1_1);
        TapeRecord fastSecond = CreateTestRecord(TapeProfile.Normal1_3);
        TapeRecord normalSecond = CreateTestRecord(TapeProfile.Normal1_1);

        try
        {
            SharpTapeExporter.Export(
                mixedPath, [first, fastSecond], SharpTapeOutputFormat.L16, SharpTapeMachine.Mz800);
            SharpTapeExporter.Export(
                normalPath, [first, normalSecond], SharpTapeOutputFormat.L16, SharpTapeMachine.Mz800);

            Assert.NotEqual(File.ReadAllBytes(normalPath), File.ReadAllBytes(mixedPath));
        }
        finally
        {
            File.Delete(mixedPath);
            File.Delete(normalPath);
        }
    }

    [Fact]
    public void ProfileEncoder_GeneratesIcLoaderAndTurboBody()
    {
        TapeRecord record = CreateTestRecord(TapeProfile.Ic1_3);

        IReadOnlyList<SharpTapeStage> stages =
            SharpTapeProfileEncoder.Build(record, SharpTapeMachine.Mz800);

        Assert.Equal(2, stages.Count);
        Assert.Equal(0xBB, stages[0].Data[0]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(18, 2)));
        Assert.Equal(0x1200, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(20, 2)));
        Assert.Equal(0x1110, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(22, 2)));
        Assert.Equal(0x16, stages[0].Data[25]);
        Assert.Equal(record.Body.MzfBody, stages[1].Data);
        Assert.Equal(345, stages[1].DelayBeforeMilliseconds);
        Assert.False(stages[0].InvertSignal);
        Assert.True(stages[1].InvertSignal);
        Assert.Equal(87.965, stages[1].Pulses.ShortHighMicroseconds);
        Assert.Equal(223.577, stages[1].Pulses.LongLowMicroseconds);
    }

    [Fact]
    public void ProfileEncoder_GeneratesTcLoaderAndTurboBody()
    {
        TapeRecord record = CreateTestRecord(TapeProfile.Tc1_2);

        IReadOnlyList<SharpTapeStage> stages =
            SharpTapeProfileEncoder.Build(record, SharpTapeMachine.Mz800);

        Assert.Equal(3, stages.Count);
        Assert.Equal(90, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(18, 2)));
        Assert.Equal(0xD400, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(20, 2)));
        Assert.Equal(90, stages[1].Data.Length);
        Assert.Equal(0x29, stages[1].Data[0x4B]);
        Assert.Equal(record.Header.MzfFtype, stages[1].Data[0x4C]);
        Assert.All(stages, stage => Assert.True(stage.InvertSignal));
        Assert.Equal(110, stages[2].DelayBeforeMilliseconds);
        Assert.Equal(98, stages[2].TrailingPulses);
    }

    [Fact]
    public void ProfileEncoder_GeneratesMz700Fast3Runtime()
    {
        TapeRecord record = CreateTestRecord(TapeProfile.Mz700_1_3);

        IReadOnlyList<SharpTapeStage> stages =
            SharpTapeProfileEncoder.Build(record, SharpTapeMachine.Mz800);

        Assert.Equal(2, stages.Count);
        Assert.Equal(0xD080, BinaryPrimitives.ReadUInt16LittleEndian(stages[0].Data.AsSpan(22, 2)));
        Assert.Equal(400, stages[1].DelayBeforeMilliseconds);
        Assert.Equal(80, stages[1].Pulses.ShortHighMicroseconds);
        Assert.Equal(160, stages[1].Pulses.LongLowMicroseconds);
    }

    [Fact]
    public void ProfileEncoder_UsesCanonicalTimingMatrixAndUnifiedLeaders()
    {
        var cases = new[]
        {
            (TapeProfile.Normal1_1, 1, new SharpPulseProfile(237.956, 255.436, 469.145, 486.626, SharpPulseTimingSource.Mz800Rom1Z013B)),
            (TapeProfile.Normal1_2, 1, new SharpPulseProfile(113.621, 139.278, 234.573, 260.229)),
            (TapeProfile.Normal1_3, 1, new SharpPulseProfile(87.965, 124.617, 175.930, 223.577)),
            (TapeProfile.Normal1_4, 1, new SharpPulseProfile(76.969, 117.286, 157.604, 179.595)),
            (TapeProfile.Mz700_1_1, 1, new SharpPulseProfile(240, 264, 464, 494)),
            (TapeProfile.Mz700_1_3, 1, new SharpPulseProfile(80, 80, 160, 160)),
            (TapeProfile.Ic1_2, 1, new SharpPulseProfile(113.621, 139.278, 234.573, 260.229)),
            (TapeProfile.Ic1_3, 1, new SharpPulseProfile(87.965, 124.617, 175.930, 223.577)),
            (TapeProfile.Ic1_4, 1, new SharpPulseProfile(76.969, 117.286, 157.604, 179.595)),
            (TapeProfile.Tc1_2, 2, new SharpPulseProfile(141.818, 140.909, 282.727, 282.727)),
            (TapeProfile.Tc1_3, 2, new SharpPulseProfile(105.455, 104.545, 210.000, 210.000))
        };

        foreach (var (profile, dataStageIndex, expectedPulses) in cases)
        {
            IReadOnlyList<SharpTapeStage> stages =
                SharpTapeProfileEncoder.Build(CreateTestRecord(profile), SharpTapeMachine.Mz800);

            Assert.Equal(11000, stages[0].LeaderShortPulses);
            Assert.All(stages.Skip(1), stage => Assert.Equal(5500, stage.LeaderShortPulses));
            AssertPulseProfile(expectedPulses, stages[dataStageIndex].Pulses);
        }

        SharpPulseProfile expectedIcHeader = new(234.573, 263.894, 469.145, 494.802);
        AssertPulseProfile(
            expectedIcHeader,
            SharpTapeProfileEncoder.Build(CreateTestRecord(TapeProfile.Ic1_2), SharpTapeMachine.Mz800)[0].Pulses);
    }

    [Theory]
    [InlineData(0, 237.956, 258.819, 469.145, 487.189)]
    [InlineData(1, 113.621, 139.278, 234.573, 260.229)]
    [InlineData(2, 87.965, 124.617, 175.930, 223.577)]
    [InlineData(3, 76.969, 117.286, 157.604, 179.595)]
    public void Export_WavNormalLeaderEdgesMatchCanonicalTiming(
        int profileValue,
        double expectedHigh,
        double expectedLow,
        double expectedLongHigh,
        double expectedLongLow)
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.wav");
        try
        {
            SharpTapeExporter.Export(
                path,
                [CreateTestRecord((TapeProfile)profileValue)],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);

            byte[] wav = File.ReadAllBytes(path);
            Assert.Equal(
                (uint)SharpTapeExporter.WavSampleRate,
                BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)));
            IReadOnlyList<WavRun> runs = ReadWavRuns(wav);
            AssertEdgeAverages(runs, 0, 200, expectedHigh, expectedLow, inverted: false);
            AssertEdgeAverages(
                runs, 22000, 40, expectedLongHigh, expectedLongLow, inverted: false);
            Assert.True(runs[21999].Samples < runs[22000].Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_WavNormal1xUsesRomContextDependentLowWidths()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.wav");
        try
        {
            SharpTapeExporter.Export(
                path,
                [CreateTestRecord(TapeProfile.Normal1_1)],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);

            IReadOnlyList<WavRun> runs = ReadWavRuns(File.ReadAllBytes(path));
            AssertEdgeAverages(runs, 22000, 40, 469.145, 487.189, inverted: false);
            AssertEdgeAverages(runs, 22080, 40, 237.956, 256.000, inverted: false);

            const int dataStart = 22164;
            AssertDuration(runs[dataStart + 1], 255.436); // SHORT -> SHORT
            AssertDuration(runs[dataStart + (6 * 2) + 1], 252.617); // SHORT -> LONG
            AssertDuration(runs[dataStart + (7 * 2) + 1], 483.806); // LONG -> LONG
            AssertDuration(runs[dataStart + (8 * 2) + 1], 486.626); // LONG -> SHORT
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(6, 113.621, 139.278, 234.573, 260.229)]
    [InlineData(7, 87.965, 124.617, 175.930, 223.577)]
    [InlineData(8, 76.969, 117.286, 157.604, 179.595)]
    [InlineData(9, 141.818, 140.909, 282.727, 282.727)]
    [InlineData(10, 105.455, 104.545, 210.000, 210.000)]
    public void Export_WavTurboLeaderEdgesMatchCanonicalTiming(
        int profileValue,
        double shortHigh,
        double shortLow,
        double longHigh,
        double longLow)
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.wav");
        try
        {
            SharpTapeExporter.Export(
                path,
                [CreateTestRecord((TapeProfile)profileValue)],
                SharpTapeOutputFormat.Wav,
                SharpTapeMachine.Mz800);

            IReadOnlyList<WavRun> runs = ReadWavRuns(File.ReadAllBytes(path));
            int delayRun = FindDelayRun(runs);
            int turboStart = delayRun + 1;
            AssertEdgeAverages(runs, turboStart, 200, shortHigh, shortLow, inverted: true);
            AssertEdgeAverages(
                runs, turboStart + 11000, 20, longHigh, longLow, inverted: true);

            // The long timing is also checked from the immutable stage plan;
            // leader edges above verify the actual WAV quantizer and polarity.
            IReadOnlyList<SharpTapeStage> stages = SharpTapeProfileEncoder.Build(
                CreateTestRecord((TapeProfile)profileValue), SharpTapeMachine.Mz800);
            SharpPulseProfile turbo = stages[^1].Pulses;
            Assert.Equal(longHigh, turbo.LongHighMicroseconds, 3);
            Assert.Equal(longLow, turbo.LongLowMicroseconds, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Export_AllCanonicalProfilesWorkInEveryOutputFormat(int formatValue)
    {
        TapeProfile[] profiles =
        [
            TapeProfile.Normal1_1, TapeProfile.Normal1_2, TapeProfile.Normal1_3, TapeProfile.Normal1_4,
            TapeProfile.Mz700_1_1, TapeProfile.Mz700_1_3,
            TapeProfile.Ic1_2, TapeProfile.Ic1_3, TapeProfile.Ic1_4,
            TapeProfile.Tc1_2, TapeProfile.Tc1_3
        ];

        foreach (TapeProfile profile in profiles)
        {
            string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.tmp");
            try
            {
                SharpTapeExporter.Export(
                    path,
                    [CreateTestRecord(profile)],
                    (SharpTapeOutputFormat)formatValue,
                    SharpTapeMachine.Mz800);
                Assert.True(new FileInfo(path).Length > 0, $"Empty export for {profile}.");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Export_WavRoundTripsEveryCanonicalLoaderAndSpeed(int profileValue)
    {
        TapeRecord record = CreateTestRecord((TapeProfile)profileValue);
        IReadOnlyList<SharpTapeStage> expectedStages =
            SharpTapeProfileEncoder.Build(record, SharpTapeMachine.Mz800);
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.wav");

        try
        {
            SharpTapeExporter.Export(
                path, [record], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800);
            IReadOnlyList<WavRun> runs = ReadWavRuns(File.ReadAllBytes(path));
            int stageStart = 0;
            SharpTapeStage? previousStage = null;

            foreach (SharpTapeStage stage in expectedStages)
            {
                if (previousStage is SharpTapeStage previous)
                {
                    stageStart += StageRunCount(previous);
                    if (stage.DelayBeforeMilliseconds > 0 &&
                        previous.InvertSignal != stage.InvertSignal)
                    {
                        stageStart++;
                    }
                }

                Assert.Equal(stage.Data, DecodeStageData(runs, stageStart, stage));
                previousStage = stage;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 16)]
    public void Export_EdgeStreamsPreserveCanonicalNormalPulseWidths(int formatValue, int unitMicroseconds)
    {
        var cases = new[]
        {
            (TapeProfile.Normal1_1, 237.956, 258.819, 469.145, 487.189),
            (TapeProfile.Normal1_2, 113.621, 139.278, 234.573, 260.229),
            (TapeProfile.Normal1_3, 87.965, 124.617, 175.930, 223.577),
            (TapeProfile.Normal1_4, 76.969, 117.286, 157.604, 179.595)
        };

        foreach (var (profile, expectedHigh, expectedLow, expectedLongHigh, expectedLongLow) in cases)
        {
            string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.tmp");
            try
            {
                SharpTapeExporter.Export(
                    path,
                    [CreateTestRecord(profile)],
                    (SharpTapeOutputFormat)formatValue,
                    SharpTapeMachine.Mz800);
                byte[] intervals = File.ReadAllBytes(path);
                double high = Enumerable.Range(0, 200)
                    .Average(index => Math.Abs(unchecked((sbyte)intervals[index * 2]))) * unitMicroseconds;
                double low = Enumerable.Range(0, 200)
                    .Average(index => Math.Abs(unchecked((sbyte)intervals[(index * 2) + 1]))) * unitMicroseconds;
                double longHigh = Enumerable.Range(0, 40)
                    .Average(index => Math.Abs(unchecked((sbyte)intervals[22000 + (index * 2)]))) * unitMicroseconds;
                double longLow = Enumerable.Range(0, 40)
                    .Average(index => Math.Abs(unchecked((sbyte)intervals[22001 + (index * 2)]))) * unitMicroseconds;

                Assert.InRange(high, expectedHigh - unitMicroseconds, expectedHigh + unitMicroseconds);
                Assert.InRange(low, expectedLow - unitMicroseconds, expectedLow + unitMicroseconds);
                Assert.InRange(longHigh, expectedLongHigh - unitMicroseconds, expectedLongHigh + unitMicroseconds);
                Assert.InRange(longLow, expectedLongLow - unitMicroseconds, expectedLongLow + unitMicroseconds);
                Assert.NotEqual(
                    Math.Abs(unchecked((sbyte)intervals[21998])),
                    Math.Abs(unchecked((sbyte)intervals[22000])));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Export_RejectsLiveHandshakeProfileBeforeCreatingFile(int profileValue)
    {
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}.l16");
        TapeRecord record = CreateTestRecord((TapeProfile)profileValue);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SharpTapeExporter.Export(path, [record], SharpTapeOutputFormat.L16));

        Assert.Contains("WRITE/SENSE", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SeparateExport_CreatesOneNumberedFilePerRecord()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            var (header, body) = CreateTestBlock();
            TapeRecord first = TapeRecord.FromLegacy(header, body);
            MZQFileHeader secondHeader = header;
            secondHeader.MzfFname = CreateFileName("SECOND");
            TapeRecord second = TapeRecord.FromLegacy(secondHeader, body);
            string selectedPath = Path.Combine(directory, "collection.l16");

            IReadOnlyList<string> paths = SharpTapeExporter.ExportSeparate(
                selectedPath,
                [first, second],
                SharpTapeOutputFormat.L16,
                SharpTapeMachine.Mz800,
                overwrite: false);

            Assert.Equal(2, paths.Count);
            Assert.EndsWith("collection_01_ROUNDTRIP.l16", paths[0], StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("collection_02_SECOND.l16", paths[1], StringComparison.OrdinalIgnoreCase);
            Assert.All(paths, path => Assert.True(File.Exists(path)));
            Assert.All(paths, path => Assert.Equal(35740L, new FileInfo(path).Length));
            Assert.False(File.Exists(selectedPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Export_WavRoundTripsThroughTapeMzWhenDecoderIsConfigured(int machineValue)
    {
        string? decoderPath = Environment.GetEnvironmentVariable("TAPEMZ_WAV2TMZ");
        if (string.IsNullOrWhiteSpace(decoderPath))
        {
            return;
        }

        Assert.True(File.Exists(decoderPath), $"TapeMZ decoder was not found: {decoderPath}");

        var (header, body) = CreateTestBlock();
        var machine = (SharpTapeMachine)machineValue;

        AssertWavRoundTrip(
            decoderPath,
            wavPath => SharpTapeExporter.Export(
                wavPath, [(header, body)], SharpTapeOutputFormat.Wav, machine),
            CreateMzfBytes(header, body.MzfBody));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Export_WavNormalProfilesSupportedByTapeMzRoundTripWhenDecoderIsConfigured(int profileValue)
    {
        string? decoderPath = Environment.GetEnvironmentVariable("TAPEMZ_WAV2TMZ");
        if (string.IsNullOrWhiteSpace(decoderPath))
        {
            return;
        }

        Assert.True(File.Exists(decoderPath), $"TapeMZ decoder was not found: {decoderPath}");

        TapeRecord record = CreateTestRecord((TapeProfile)profileValue);
        AssertWavRoundTrip(
            decoderPath,
            wavPath => SharpTapeExporter.Export(
                wavPath, [record], SharpTapeOutputFormat.Wav, SharpTapeMachine.Mz800),
            CreateMzfBytes(record.Header, record.Body.MzfBody));
    }

    private static void AssertWavRoundTrip(
        string decoderPath,
        Action<string> export,
        byte[] expected)
    {

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"qdtool-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string wavPath = Path.Combine(tempDirectory, "roundtrip.wav");
        string decodedPath = Path.Combine(tempDirectory, "decoded.mzf");

        try
        {
            export(wavPath);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = decoderPath,
                ArgumentList =
                {
                    wavPath,
                    "-o", decodedPath,
                    "--output-format", "mzf",
                    "--overwrite-mzf",
                    "--tolerance", "0.35",
                    "--pulse-mode", "exact"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"TapeMZ failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
            Assert.True(File.Exists(decodedPath), $"TapeMZ did not create an MZF file.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");

            Assert.Equal(expected, File.ReadAllBytes(decodedPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static (MZQFileHeader Header, MZQFileBody Body) CreateTestBlock()
    {
        byte[] bodyBytes = [0x10, 0x20, 0x30, 0x40, 0x55, 0xAA];
        var header = new MZQFileHeader
        {
            MzfFtype = 0x01,
            MzfFname = CreateFileName("ROUNDTRIP"),
            MzfFnameEnd = 0x0D,
            MzfSize = (ushort)bodyBytes.Length,
            MzfStart = 0x1200,
            MzfExec = 0x1200,
            MzfHeaderDescription = new byte[104]
        };
        var body = new MZQFileBody
        {
            DataSize = (ushort)bodyBytes.Length,
            MzfBody = bodyBytes,
            TrailingData = []
        };

        return (header, body);
    }

    private static TapeRecord CreateTestRecord(TapeProfile profile)
    {
        var (header, body) = CreateTestBlock();
        TapeRecord record = TapeRecord.FromLegacy(header, body);
        record.Profile = profile;
        record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        return record;
    }

    private static void AssertPulseProfile(SharpPulseProfile expected, SharpPulseProfile actual)
    {
        Assert.Equal(expected.ShortHighMicroseconds, actual.ShortHighMicroseconds, 3);
        Assert.Equal(expected.ShortLowMicroseconds, actual.ShortLowMicroseconds, 3);
        Assert.Equal(expected.LongHighMicroseconds, actual.LongHighMicroseconds, 3);
        Assert.Equal(expected.LongLowMicroseconds, actual.LongLowMicroseconds, 3);
        Assert.Equal(expected.TimingSource, actual.TimingSource);
    }

    private static IReadOnlyList<WavRun> ReadWavRuns(byte[] wav)
    {
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));

        var runs = new List<WavRun>();
        byte value = wav[44];
        int samples = 1;
        for (int index = 45; index < wav.Length; index++)
        {
            if (wav[index] == value)
            {
                samples++;
                continue;
            }

            runs.Add(new WavRun(value, samples));
            value = wav[index];
            samples = 1;
        }
        runs.Add(new WavRun(value, samples));
        return runs;
    }

    private static void AssertEdgeAverages(
        IReadOnlyList<WavRun> runs,
        int start,
        int pulseCount,
        double expectedHighMicroseconds,
        double expectedLowMicroseconds,
        bool inverted)
    {
        long highSamples = 0;
        long lowSamples = 0;
        for (int pulse = 0; pulse < pulseCount; pulse++)
        {
            WavRun first = runs[start + (pulse * 2)];
            WavRun second = runs[start + (pulse * 2) + 1];
            Assert.Equal(inverted ? (byte)176 : (byte)80, first.Value);
            Assert.Equal(inverted ? (byte)80 : (byte)176, second.Value);
            highSamples += inverted ? second.Samples : first.Samples;
            lowSamples += inverted ? first.Samples : second.Samples;
        }

        double measuredHigh = highSamples * 1_000_000.0 / SharpTapeExporter.WavSampleRate / pulseCount;
        double measuredLow = lowSamples * 1_000_000.0 / SharpTapeExporter.WavSampleRate / pulseCount;
        Assert.InRange(measuredHigh, expectedHighMicroseconds - 3.0, expectedHighMicroseconds + 3.0);
        Assert.InRange(measuredLow, expectedLowMicroseconds - 3.0, expectedLowMicroseconds + 3.0);
    }

    private static void AssertDuration(WavRun run, double expectedMicroseconds)
    {
        double measured = run.Samples * 1_000_000.0 / SharpTapeExporter.WavSampleRate;
        double oneSample = 1_000_000.0 / SharpTapeExporter.WavSampleRate;
        Assert.InRange(measured, expectedMicroseconds - oneSample, expectedMicroseconds + oneSample);
    }

    private static int FindDelayRun(IReadOnlyList<WavRun> runs)
    {
        int index = runs.ToList().FindIndex(run => run.Samples > 4000);
        Assert.True(index >= 0, "The inter-stage delay was not found in the WAV waveform.");
        return index;
    }

    private static int StageRunCount(SharpTapeStage stage) => checked(2 * (
        stage.LeaderShortPulses +
        stage.MarkLongPulses +
        stage.MarkShortPulses +
        stage.FinalMarkLongPulses +
        ((stage.Data.Length + 2) * 9) +
        stage.TrailingPulses));

    private static byte[] DecodeStageData(
        IReadOnlyList<WavRun> runs,
        int stageStart,
        SharpTapeStage stage)
    {
        int preamblePulses = stage.LeaderShortPulses +
            stage.MarkLongPulses + stage.MarkShortPulses + stage.FinalMarkLongPulses;
        int runIndex = stageStart + (preamblePulses * 2);
        byte[] decoded = new byte[stage.Data.Length + 2];
        double shortPeriod = stage.Pulses.ShortHighMicroseconds + stage.Pulses.ShortLowMicroseconds;
        double longPeriod = stage.Pulses.LongHighMicroseconds + stage.Pulses.LongLowMicroseconds;
        double thresholdSamples = ((shortPeriod + longPeriod) / 2.0) *
            SharpTapeExporter.WavSampleRate / 1_000_000;

        for (int byteIndex = 0; byteIndex < decoded.Length; byteIndex++)
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int pulseSamples = runs[runIndex].Samples + runs[runIndex + 1].Samples;
                value = (byte)((value << 1) | (pulseSamples > thresholdSamples ? 1 : 0));
                runIndex += 2;
            }

            int syncSamples = runs[runIndex].Samples + runs[runIndex + 1].Samples;
            Assert.True(syncSamples > thresholdSamples, "Missing LONG byte-sync pulse.");
            runIndex += 2;
            decoded[byteIndex] = value;
        }

        ushort expectedChecksum = ComputeTestChecksum(stage.Data);
        Assert.Equal((byte)(expectedChecksum >> 8), decoded[^2]);
        Assert.Equal((byte)expectedChecksum, decoded[^1]);
        return decoded[..^2];
    }

    private static ushort ComputeTestChecksum(byte[] data)
    {
        uint checksum = 0;
        foreach (byte value in data)
        {
            checksum += (uint)System.Numerics.BitOperations.PopCount(value);
        }
        return unchecked((ushort)checksum);
    }

    private readonly record struct WavRun(byte Value, int Samples);

    private static byte[] CreateFileName(string text)
    {
        byte[] result = new byte[16];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(result, 0);
        return result;
    }

    private static byte[] CreateMzfBytes(MZQFileHeader header, byte[] body)
    {
        byte[] result = new byte[128 + body.Length];
        result[0] = header.MzfFtype;
        header.MzfFname.CopyTo(result, 1);
        result[17] = header.MzfFnameEnd;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), header.MzfSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), header.MzfStart);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), header.MzfExec);
        header.MzfHeaderDescription.CopyTo(result, 24);
        body.CopyTo(result, 128);
        return result;
    }
}
