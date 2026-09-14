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
    [InlineData(0, 28116L)]
    [InlineData(1, 68740L)]
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
        Assert.Equal(112, stages[1].Pulses.ShortHighMicroseconds);
        Assert.Equal(192, stages[1].Pulses.LongLowMicroseconds);
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

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
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
            Assert.All(paths, path => Assert.Equal(28116L, new FileInfo(path).Length));
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

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"qdtool-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string wavPath = Path.Combine(tempDirectory, "roundtrip.wav");
        string decodedPath = Path.Combine(tempDirectory, "decoded.mzf");

        try
        {
            SharpTapeExporter.Export(wavPath, [(header, body)], SharpTapeOutputFormat.Wav, machine);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = decoderPath,
                ArgumentList =
                {
                    wavPath,
                    "-o", decodedPath,
                    "--output-format", "mzf",
                    "--overwrite-mzf"
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

            byte[] expected = CreateMzfBytes(header, body.MzfBody);
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
