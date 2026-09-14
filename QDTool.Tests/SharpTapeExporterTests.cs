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
