namespace QDTool.Tests;

public class TapeDocumentTests
{
    [Fact]
    public void StandaloneMzf_CapturesTrailingAndBasicSaveRemovesIt()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            byte[] body = [1, 2, 3];
            byte[] trailing = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            string source = Path.Combine(directory, "source.mzf");
            string target = Path.Combine(directory, "target.mzf");
            File.WriteAllBytes(source, TapeTestData.CreateMzf(body, trailing: trailing));

            TapeRecord record = new MZTFileReader().ReadStandaloneMzf(source);
            Assert.Equal(trailing, record.Body.TrailingData);

            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: false);

            Assert.Equal(128 + body.Length, new FileInfo(target).Length);
            Assert.Empty(record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StandaloneMzf_AdvancedPreserveKeepsTrailingByteForByte()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            byte[] trailing = Enumerable.Range(0, 256).Select(value => (byte)(255 - value)).ToArray();
            string source = Path.Combine(directory, "source.mzf");
            string target = Path.Combine(directory, "target.mzf");
            byte[] expected = TapeTestData.CreateMzf([1, 2, 3], trailing: trailing);
            File.WriteAllBytes(source, expected);
            TapeRecord record = new MZTFileReader().ReadStandaloneMzf(source);

            TapeDocumentWriter.SaveMzf(target, record, preserveTrailing: true);

            Assert.Equal(expected, File.ReadAllBytes(target));
            Assert.Equal(trailing, record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BinaryDescription_RoundTripsByteForByte()
    {
        byte[] description = Enumerable.Range(0, 104).Select(value => (byte)(value * 37)).ToArray();
        byte[] source = TapeTestData.CreateMzf([0xAA, 0x55], description);
        TapeRecord record = TapeTestData.ReadRecord(source);

        byte[] saved = TapeDocumentWriter.SerializeMzf(record, preserveTrailing: false);

        Assert.Equal(source, saved);
        Assert.Equal(description, saved[24..128]);
    }

    [Fact]
    public void Mzt_UsesDeclaredSizesAndSeparatesInvalidContainerTail()
    {
        byte[][] files =
        [
            TapeTestData.CreateMzf([1], name: "ONE"),
            TapeTestData.CreateMzf([2, 3, 4], name: "TWO"),
            TapeTestData.CreateMzf([5, 6, 7, 8, 9], name: "THREE")
        ];
        byte[] tail = [0xDE, 0xAD, 0xBE, 0xEF];
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "test.mzt");
            File.WriteAllBytes(path, files.SelectMany(bytes => bytes).Concat(tail).ToArray());

            MztReadResult result = new MZTFileReader().ReadMzt(path);

            Assert.Equal(new[] { 1, 3, 5 }, result.Records.Select(record => record.Body.MzfBody.Length));
            Assert.Equal(tail, result.ContainerTrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Mzt_AcceptsAFullSixteenByteNameWithNulInsteadOfCr()
    {
        byte[] first = TapeTestData.CreateMzf([1, 2], name: "FIRST");
        byte[] second = TapeTestData.CreateMzf([3, 4, 5], name: "SIXTEEN-CHAR-NAME");
        second[17] = 0x00;
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "full-name.mzt");
            File.WriteAllBytes(path, first.Concat(second).ToArray());

            MztReadResult result = new MZTFileReader().ReadMzt(path);

            Assert.Equal(2, result.Records.Count);
            Assert.Empty(result.ContainerTrailingData);
            Assert.Equal(new byte[] { 3, 4, 5 }, result.Records[1].Body.MzfBody);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MztSave_DropsPerRecordTrailingAndMoveCarriesProfile()
    {
        TapeRecord first = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1], name: "A"));
        TapeRecord second = TapeTestData.ReadRecord(TapeTestData.CreateMzf([2, 3], name: "B"));
        MZQFileBody body = first.Body;
        body.TrailingData = [9, 9, 9];
        first.Body = body;
        second.Profile = TapeProfile.Ic1_3;
        second.MetadataOrigin = MetadataOrigin.LoadedFromMti;
        var records = new List<TapeRecord> { first, second };
        records.RemoveAt(1);
        records.Insert(0, second);

        byte[] saved = TapeDocumentWriter.SerializeMzt(records);

        Assert.Equal(TapeProfile.Ic1_3, records[0].Profile);
        Assert.Equal(128 + 2 + 128 + 1, saved.Length);
        Assert.Equal("B", System.Text.Encoding.ASCII.GetString(saved, 1, 1));
    }

    [Fact]
    public void MztSave_RemovesTrailingFromCurrentRecordModel()
    {
        string directory = TapeTestData.CreateTempDirectory();
        try
        {
            TapeRecord record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2]));
            MZQFileBody body = record.Body;
            body.TrailingData = [3, 4, 5];
            record.Body = body;

            TapeDocumentWriter.SaveMzt(Path.Combine(directory, "saved.mzt"), [record]);

            Assert.Empty(record.Body.TrailingData);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FeatureModeFilters_HideAdvancedFormatsInBasic()
    {
        string basic = FeatureModePolicy.GetSaveFilter(advanced: false).ToLowerInvariant();
        string advanced = FeatureModePolicy.GetSaveFilter(advanced: true).ToLowerInvariant();

        Assert.DoesNotContain(".wav", basic);
        Assert.DoesNotContain(".l16", basic);
        Assert.DoesNotContain(".lep", basic);
        Assert.Contains(".wav", advanced);
        Assert.Contains(".l16", advanced);
        Assert.Contains(".lep", advanced);
    }

    [Fact]
    public void SwitchingFeaturePolicyDoesNotMutateRecordData()
    {
        TapeRecord record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3]));
        MZQFileBody body = record.Body;
        body.TrailingData = [4, 5, 6];
        record.Body = body;
        record.Profile = TapeProfile.Ic1_3;
        record.MetadataOrigin = MetadataOrigin.LoadedFromMfi;

        _ = FeatureModePolicy.GetSaveFilter(false);
        _ = FeatureModePolicy.GetSaveFilter(true);
        _ = FeatureModePolicy.GetSaveFilter(false);

        Assert.Equal(new byte[] { 1, 2, 3 }, record.Body.MzfBody);
        Assert.Equal(new byte[] { 4, 5, 6 }, record.Body.TrailingData);
        Assert.Equal(TapeProfile.Ic1_3, record.Profile);
        Assert.Equal(MetadataOrigin.LoadedFromMfi, record.MetadataOrigin);
    }

    [Fact]
    public void TapeProfileComponents_RoundTripEverySupportedProfile()
    {
        foreach (TapeProfile profile in Enum.GetValues<TapeProfile>())
        {
            (string loaderType, string speed) = TapeProfileComponents.Split(profile);

            Assert.Contains(loaderType, TapeProfileComponents.LoaderTypes);
            Assert.Contains(speed, TapeProfileComponents.GetAvailableSpeeds(loaderType));
            Assert.Equal(profile, TapeProfileComponents.Combine(loaderType, speed));
        }
    }

    [Fact]
    public void TcOffersOnlyAnalyzedTwoAndThreeTimesSpeeds()
    {
        Assert.Equal(
            new[] { "1:2", "1:3" },
            TapeProfileComponents.GetAvailableSpeeds("TC"));
        Assert.Equal("1:2", TapeProfileComponents.NormalizeSpeed("TC", "1:4"));
        Assert.False(SidecarService.TryParseProfile(
            ["TYPE=TC", "SPEED=1:4"], out _));
    }

    [Theory]
    [InlineData("NORMAL", "1:4", "1:4")]
    [InlineData("MZ700", "1:2", "1:1")]
    [InlineData("IC", "1:1", "1:2")]
    [InlineData("UL_MZ800", "1:3", "")]
    public void TapeProfileComponents_NormalizesInvalidSpeeds(
        string loaderType,
        string requestedSpeed,
        string expectedSpeed)
    {
        Assert.Equal(expectedSpeed, TapeProfileComponents.NormalizeSpeed(loaderType, requestedSpeed));
    }
}
