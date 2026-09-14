using System.Buffers.Binary;

namespace QDTool.Tests;

public class QuickDiskImageTests
{
    [Theory]
    [InlineData("Old_MZ700.qd", QdImageFormat.SharpLegacyLogical)]
    [InlineData("HxC_DSKA0000_Blank.QD", QdImageFormat.HxcPhysical)]
    [InlineData("hxc_Mario.qd", QdImageFormat.HxcPhysical)]
    [InlineData("Flashfloppy_Blank.qd", QdImageFormat.FlashFloppyPhysical)]
    public void Detector_RecognizesGoldenImages(string name, QdImageFormat expected)
    {
        Assert.Equal(expected, QdFormatDetector.Detect(ReadQd(name)));
    }

    [Fact]
    public void Detector_RejectsRandomData()
    {
        Assert.Equal(QdImageFormat.Unknown, QdFormatDetector.Detect(new byte[1024]));
    }

    [Fact]
    public void SaveAs_UsesFilterIndexToDistinguishThreeQdWriters()
    {
        Assert.Equal(TapeDocumentFormat.QdHxc, MainWindow.ResolveSaveFormat(".qd", 3));
        Assert.Equal(TapeDocumentFormat.QdFlashFloppy, MainWindow.ResolveSaveFormat(".qd", 4));
        Assert.Equal(TapeDocumentFormat.QdSharpLegacy, MainWindow.ResolveSaveFormat(".qd", 5));
        Assert.Equal(3, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdHxc));
        Assert.Equal(4, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdFlashFloppy));
        Assert.Equal(5, MainWindow.GetSaveFilterIndex(TapeDocumentFormat.QdSharpLegacy));
    }

    [Theory]
    [InlineData("Old_MZ700.qd")]
    [InlineData("HxC_DSKA0000_Blank.QD")]
    [InlineData("Flashfloppy_Blank.qd")]
    public void GoldenBlankImages_OpenWithNoRecords(string name)
    {
        Assert.Empty(QdImageReaderWriter.Read(ReadQd(name)).Records);
    }

    [Fact]
    public void LegacyBlankWriter_ReproducesGoldenImage()
    {
        byte[] written = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), QdImageFormat.SharpLegacyLogical);

        Assert.Equal(0xF00F, written.Length);
        Assert.Equal(ReadQd("Old_MZ700.qd"), written);
    }

    [Fact]
    public void MarioGolden_DecodesAllFramesAndRecord()
    {
        byte[] image = ReadQd("hxc_Mario.qd");
        QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(image, QdImageFormat.HxcPhysical);
        IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
        QdReadResult result = QdImageReaderWriter.Read(image);

        Assert.Contains(frames, frame => frame.Type == 0x02 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0xFA, 0x91 }));
        Assert.Contains(frames, frame => frame.Type == 0x00 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0x0F, 0x72 }));
        Assert.Contains(frames, frame => frame.Type == 0x05 && frame.Bytes[^2..].SequenceEqual(new byte[] { 0xD3, 0x5C }));
        Assert.True(frames.Select(frame => (int)(frame.Position & 15)).Distinct().Count() >= 3);

        TapeRecord record = Assert.Single(result.Records);
        Assert.Equal((byte)0x01, record.Header.MzfFtype);
        Assert.Equal("MARIO SPECIAL", Utility.ConvertMzfNameToASCIIString(record.Header.MzfFname));
        Assert.Equal((ushort)43202, record.Header.MzfSize);
        Assert.Equal((ushort)0x1200, record.Header.MzfStart);
        Assert.Equal((ushort)0x1200, record.Header.MzfExec);
        Assert.Equal(43202, record.Body.MzfBody.Length);
        Assert.Equal(new byte[]
        {
            0xF3, 0x31, 0x00, 0xD0, 0xD3, 0xE6, 0xD3, 0xE0,
            0xD3, 0xE3, 0x3E, 0x01, 0xD3, 0xF0, 0x21, 0x00,
            0xD0, 0x11, 0x01, 0xD0, 0x01, 0xE7, 0x03, 0x36,
            0x00, 0xED, 0xB0, 0x21, 0x00, 0xD8, 0x11, 0x01
        }, record.Body.MzfBody[..32]);
        Assert.Equal(new byte[]
        {
            0x18, 0x18, 0x00, 0x00, 0x30, 0x0C, 0x00, 0x00,
            0x30, 0x0C, 0x00, 0x00, 0x30, 0x0C, 0x00, 0x00,
            0x18, 0x18, 0x00, 0x00, 0x0F, 0xF0, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF
        }, record.Body.MzfBody[^32..]);
        Assert.All(record.Header.MzfHeaderDescription[38..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void Mario_RoundTripsThroughEveryQdWriter(QdImageFormat format)
    {
        TapeRecord original = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);

        byte[] output = QdImageReaderWriter.Write([original], format);
        QdReadResult reread = QdImageReaderWriter.Read(output);
        TapeRecord actual = Assert.Single(reread.Records);

        AssertRecordEquivalent(original, actual);
        Assert.Equal(format, reread.Format);
        Assert.Equal(format == QdImageFormat.SharpLegacyLogical ? 0xF00F : 0x32000, output.Length);
    }

    [Theory]
    [InlineData(".qdf")]
    [InlineData(".mzq")]
    public void Mario_RoundTripsThroughExistingLogicalFormats(string extension)
    {
        TapeRecord original = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        string path = Path.Combine(Path.GetTempPath(), $"qdtool-{Guid.NewGuid():N}{extension}");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (extension == ".qdf")
                {
                    var writer = new QDFFileReader();
                    writer.WriteQDFHeaderToFile(stream, 2);
                    writer.WriteQDFFileHeaderToFile(stream, original.Header);
                    writer.WriteQDFFileBodyToFile(stream, original.Body);
                    writer.WriteBytesToStream(stream, 0, 81936 - stream.Length);
                }
                else
                {
                    var writer = new MZQFileReader();
                    writer.WriteMZQHeaderToFile(stream, 2);
                    writer.WriteMZQFileHeaderToFile(stream, original.Header);
                    writer.WriteMZQFileBodyToFile(stream, original.Body);
                }
            }

            (MZQFileHeader, MZQFileBody) block = extension == ".qdf"
                ? Assert.Single(new QDFFileReader().ReadFile(path))
                : Assert.Single(new MZQFileReader().ReadFile(path));
            AssertRecordEquivalent(original, TapeRecord.FromLegacy(block.Item1, block.Item2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void StandaloneMzf_RoundTripsRepresentableDataAndDropsTrailing(QdImageFormat format)
    {
        string mzfPath = Path.Combine(AppContext.BaseDirectory, "MZF", "Barbar6.mzf");
        TapeRecord original = new MZTFileReader().ReadStandaloneMzf(mzfPath);
        MZQFileBody body = original.Body;
        body.TrailingData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        original.Body = body;

        TapeRecord actual = Assert.Single(QdImageReaderWriter.Read(QdImageReaderWriter.Write([original], format)).Records);

        AssertRecordEquivalent(original, actual);
        Assert.Empty(actual.Body.TrailingData);
        Assert.All(actual.Header.MzfHeaderDescription[38..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(QdImageFormat.HxcPhysical, 0x31C00, 0x3200, 0x25600)]
    [InlineData(QdImageFormat.FlashFloppyPhysical, 0x31A99, 0x31A9, 0x2B745)]
    public void PhysicalWriter_UsesSpecifiedContainerProfile(QdImageFormat format, int length, int start, int end)
    {
        byte[] output = QdImageReaderWriter.Write(Array.Empty<TapeRecord>(), format);
        QuickDiskContainer parsed = HxcFlashFloppyQdContainer.Parse(output, format);

        Assert.Equal(0x32000, output.Length);
        Assert.Equal((uint)0x400, parsed.Descriptor.Offset);
        Assert.Equal((uint)length, parsed.Descriptor.Length);
        Assert.Equal((uint)start, parsed.Descriptor.WindowStart);
        Assert.Equal((uint)end, parsed.Descriptor.WindowEnd);
        if (format == QdImageFormat.HxcPhysical) Assert.Equal((uint)203388, parsed.BitRate);
    }

    [Theory]
    [InlineData(QdImageFormat.SharpLegacyLogical)]
    [InlineData(QdImageFormat.HxcPhysical)]
    [InlineData(QdImageFormat.FlashFloppyPhysical)]
    public void Writers_AreDeterministic(QdImageFormat format)
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);

        Assert.Equal(QdImageReaderWriter.Write([record], format), QdImageReaderWriter.Write([record], format));
    }

    [Fact]
    public void Writers_RejectCapacityOverflow()
    {
        TapeRecord record = Assert.Single(QdImageReaderWriter.Read(ReadQd("hxc_Mario.qd")).Records);
        MZQFileHeader header = record.Header;
        header.MzfSize = 62000;
        record.Header = header;
        MZQFileBody body = record.Body;
        body.DataSize = 62000;
        body.MzfBody = new byte[62000];
        record.Body = body;

        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record], QdImageFormat.SharpLegacyLogical));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record, record], QdImageFormat.HxcPhysical));
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Write([record, record], QdImageFormat.FlashFloppyPhysical));
    }

    [Fact]
    public void ContainerValidation_RejectsTruncatedAndOutOfBoundsDescriptors()
    {
        byte[] truncated = "HXCQDDRV"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(truncated));

        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512, 4), 0x40000000);
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(image));

        image = ReadQd("Flashfloppy_Blank.qd");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(516, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(524, 4), length + 1);
        Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(image));
    }

    [Fact]
    public void ContainerValidation_RejectsUnsupportedGeometry()
    {
        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), 2);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => QdImageReaderWriter.Read(image));
        Assert.Contains("Unsupported QuickDisk geometry", error.Message);
    }

    [Fact]
    public void ValidNonSharpPhysicalImage_IsRejected()
    {
        byte[] image = ReadQd("HxC_DSKA0000_Blank.QD");
        for (int i = 1024; i < 10000; i++) image[i] = (byte)i;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => QdImageReaderWriter.Read(image));
        Assert.Contains("does not contain a supported Sharp MZ", error.Message);
    }

    private static byte[] ReadQd(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "QD", name));

    private static void AssertRecordEquivalent(TapeRecord expected, TapeRecord actual)
    {
        Assert.Equal(expected.Header.MzfFtype, actual.Header.MzfFtype);
        Assert.Equal(expected.Header.MzfFname, actual.Header.MzfFname);
        Assert.Equal(expected.Header.MzfFnameEnd, actual.Header.MzfFnameEnd);
        Assert.Equal(expected.Header.MzfSize, actual.Header.MzfSize);
        Assert.Equal(expected.Header.MzfStart, actual.Header.MzfStart);
        Assert.Equal(expected.Header.MzfExec, actual.Header.MzfExec);
        Assert.Equal(expected.Header.MzfHeaderDescription[..38], actual.Header.MzfHeaderDescription[..38]);
        Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
    }
}
