namespace QDTool.Tests;

public class QuickDiskMfmCodecTests
{
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x16)]
    [InlineData(0xA5)]
    [InlineData(0x55)]
    [InlineData(0xAA)]
    [InlineData(0xFF)]
    public void Byte_RoundTripsThroughMfm(byte value)
    {
        byte[] encoded = QuickDiskMfmCodec.Encode([value]);
        Assert.Equal(new byte[] { value }, QuickDiskMfmCodec.Decode(encoded, 1));
    }

    [Fact]
    public void Buffer_RoundTripsThroughMfm()
    {
        var random = new Random(123456);
        byte[] input = new byte[4096];
        random.NextBytes(input);

        Assert.Equal(input, QuickDiskMfmCodec.Decode(QuickDiskMfmCodec.Encode(input), 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void FrameFinder_HandlesDifferentInitialBitAlignments(int shift)
    {
        byte[] stream = new byte[16];
        stream[0] = 0x00;
        for (int i = 1; i <= 10; i++) stream[i] = 0x16;
        SharpQdFrameCodec.EncodeCountFrame(0).CopyTo(stream, 11);
        byte[] encoded = QuickDiskMfmCodec.Encode(stream);
        byte[] shifted = ShiftBits(encoded, shift);

        SharpQdFrame frame = Assert.Single(QuickDiskMfmCodec.FindSharpFrames(shifted));
        Assert.Equal((byte)0x02, frame.Type);
        Assert.True(SharpQdFrameCodec.HasValidCrc(frame.Bytes));
    }

    private static byte[] ShiftBits(byte[] source, int shift)
    {
        byte[] result = new byte[source.Length + 2];
        for (int bit = 0; bit < source.Length * 8; bit++)
        {
            if ((source[bit >> 3] & (1 << (bit & 7))) != 0)
            {
                int target = bit + shift;
                result[target >> 3] |= (byte)(1 << (target & 7));
            }
        }
        return result;
    }
}
