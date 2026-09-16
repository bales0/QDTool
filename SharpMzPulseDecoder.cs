using System;
using System.Numerics;

namespace QDTool
{
    internal enum SharpMzDecoderEventType
    {
        None,
        HeaderValid,
        DataByte,
        BlockValid,
        BlockInvalid
    }

    internal readonly record struct SharpMzDecoderEvent(
        SharpMzDecoderEventType Type,
        byte Value,
        int ByteIndex,
        ushort CalculatedChecksum,
        ushort RecordedChecksum,
        int LeaderPulses,
        int CopyIndex);

    // Behavioral port of MZ-SD2CMT2-Reborn/src/formats/mz_tape_decoder.cpp.
    // Physical HIGH intervals are retained by the importer, but only physical
    // LOW intervals reach the Sharp pulse classifier.
    internal sealed class SharpMzPulseDecoder
    {
        private const int HeaderBytes = 128;
        private const int MinLeaderPulses = 16;
        private const int MinMarkPulses = 12;
        private const int MaxMarkPulses = 48;
        private const int FinalMarkPulses = 2;
        private const int MaxHalfUnits = 1024;
        private const int LeaderLockPulses = 256;

        private enum DecodeState
        {
            SearchLeader,
            MarkLong,
            MarkShort,
            MarkFinal,
            Data,
            DuplicateGap
        }

        private enum DecoderMode
        {
            Stopped,
            Header,
            Data
        }

        private DecoderMode mode;
        private DecodeState state;
        private long shortX8;
        private int leaderPulses;
        private int markPulses;
        private int finalPulses;
        private int bitCount;
        private byte byteValue;
        private int byteIndex;
        private int expectedBytes;
        private ushort checksum;
        private ushort recordedChecksum;
        private int copyIndex;
        private long physicalHighUnitsTotal;
        private int physicalHighIntervals;
        private readonly byte[] headerBuffer = new byte[HeaderBytes];
        private byte[]? validatedHeader;
        private long validatedShortX8;
        private long validatedPhysicalHighX8;
        private SharpMzDecoderEvent? pendingEvent;

        internal byte[]? ValidatedHeader => validatedHeader;
        internal long HeaderShortPhysicalLowX8 => validatedShortX8;
        internal long HeaderShortPhysicalHighX8 => validatedPhysicalHighX8;

        internal void BeginHeader()
        {
            mode = DecoderMode.Header;
            validatedHeader = null;
            validatedShortX8 = 0;
            validatedPhysicalHighX8 = 0;
            pendingEvent = null;
            expectedBytes = HeaderBytes;
            ResetDecoder(0);
        }

        internal void StartData(int byteCount)
        {
            if (validatedHeader is null || byteCount is < 0 or > ushort.MaxValue)
            {
                Stop();
                return;
            }

            mode = DecoderMode.Data;
            pendingEvent = null;
            expectedBytes = byteCount;
            ResetDecoder(0);
        }

        internal void StartRecoveryData(int byteCount)
        {
            if (validatedHeader is null || byteCount is < 0 or > ushort.MaxValue)
            {
                Stop();
                return;
            }

            mode = DecoderMode.Data;
            pendingEvent = null;
            BeginDuplicateGap(byteCount);
        }

        internal void BreakSignal()
        {
            if (mode == DecoderMode.Header)
            {
                expectedBytes = HeaderBytes;
                ResetDecoder(0);
            }
            else if (mode == DecoderMode.Data && validatedHeader is not null)
            {
                ResetDecoder(0);
            }
        }

        internal void Stop()
        {
            mode = DecoderMode.Stopped;
            pendingEvent = null;
        }

        internal bool FeedInterval(long durationUnits, bool physicalHigh)
        {
            if (mode == DecoderMode.Stopped)
            {
                return false;
            }

            if (durationUnits is <= 0 or > MaxHalfUnits)
            {
                BreakSignal();
                return false;
            }

            if (physicalHigh)
            {
                TrackLeaderPhysicalHigh(durationUnits);
                return false;
            }

            FeedPulse(durationUnits);
            return pendingEvent.HasValue;
        }

        internal bool TryTakeEvent(out SharpMzDecoderEvent decoderEvent)
        {
            if (pendingEvent is not SharpMzDecoderEvent value)
            {
                decoderEvent = default;
                return false;
            }

            decoderEvent = value;
            pendingEvent = null;
            return true;
        }

        private void ResetDecoder(long seedUnits)
        {
            state = DecodeState.SearchLeader;
            shortX8 = seedUnits <= MaxHalfUnits ? seedUnits * 8 : 0;
            leaderPulses = shortX8 != 0 ? 1 : 0;
            markPulses = 0;
            finalPulses = 0;
            bitCount = 0;
            byteValue = 0;
            byteIndex = 0;
            checksum = 0;
            recordedChecksum = 0;
            copyIndex = 0;
            physicalHighUnitsTotal = 0;
            physicalHighIntervals = 0;
        }

        private bool AcceptLeaderPulse(long durationUnits)
        {
            if (shortX8 == 0)
            {
                return false;
            }

            long scaled = durationUnits * 8;
            long difference = Math.Abs(scaled - shortX8);
            long tolerance = Math.Max(shortX8 / 4, 8);
            if (difference > tolerance)
            {
                return false;
            }

            shortX8 = scaled >= shortX8
                ? shortX8 + ((difference + 4) >> 3)
                : shortX8 - ((difference + 3) >> 3);
            return true;
        }

        private void LockLeaderWindow()
        {
            // The reference locks only its real-pulse 8-bit hot path.
            if (finalPulses != 0 || shortX8 is <= 0 or > 204)
            {
                return;
            }

            long tolerance = Math.Max(shortX8 / 4, 8);
            long lowScaled = Math.Max(0, shortX8 - tolerance);
            long highScaled = shortX8 + tolerance;
            markPulses = (int)((lowScaled + 7) >> 3);
            finalPulses = (int)(highScaled >> 3);
        }

        private int ClassifyPulse(long durationUnits)
        {
            if (shortX8 == 0)
            {
                return -1;
            }

            long scaled = durationUnits * 8;
            if ((2 * scaled) < shortX8 || scaled > (3 * shortX8))
            {
                return -1;
            }

            return (20 * scaled) < (29 * shortX8) ? 0 : 1;
        }

        private void FeedPulse(long durationUnits)
        {
            if (mode == DecoderMode.Stopped)
            {
                return;
            }

            if (state == DecodeState.DuplicateGap)
            {
                int pulseClass = ClassifyPulse(durationUnits);
                if (pulseClass == 0)
                {
                    if (leaderPulses < 256)
                    {
                        leaderPulses++;
                    }
                    if (leaderPulses == 256)
                    {
                        state = DecodeState.Data;
                        leaderPulses = 0;
                    }
                }
                else if (leaderPulses < 128)
                {
                    leaderPulses = 0;
                }
                else
                {
                    ResetDecoder(durationUnits);
                }
                return;
            }

            if (state == DecodeState.SearchLeader)
            {
                if (shortX8 == 0)
                {
                    ResetDecoder(durationUnits);
                    return;
                }

                if (finalPulses != 0 &&
                    durationUnits >= markPulses &&
                    durationUnits <= finalPulses)
                {
                    if (leaderPulses < ushort.MaxValue)
                    {
                        leaderPulses++;
                    }
                    return;
                }

                if (finalPulses == 0 && AcceptLeaderPulse(durationUnits))
                {
                    if (leaderPulses < ushort.MaxValue)
                    {
                        leaderPulses++;
                    }
                    if (leaderPulses == LeaderLockPulses)
                    {
                        LockLeaderWindow();
                    }
                    return;
                }

                int pulseClass = ClassifyPulse(durationUnits);
                if (leaderPulses >= MinLeaderPulses && pulseClass == 1)
                {
                    state = DecodeState.MarkLong;
                    markPulses = 1;
                    return;
                }

                ResetDecoder(durationUnits);
                return;
            }

            int classified = ClassifyPulse(durationUnits);
            if (classified < 0)
            {
                ResetDecoder(durationUnits);
                return;
            }

            if (state == DecodeState.MarkLong)
            {
                if (classified == 1)
                {
                    if (markPulses < byte.MaxValue)
                    {
                        markPulses++;
                    }
                    return;
                }
                if (markPulses is >= MinMarkPulses and <= MaxMarkPulses)
                {
                    state = DecodeState.MarkShort;
                    markPulses = 1;
                    return;
                }
                ResetDecoder(durationUnits);
                return;
            }

            if (state == DecodeState.MarkShort)
            {
                if (classified == 0)
                {
                    if (markPulses < byte.MaxValue)
                    {
                        markPulses++;
                    }
                    return;
                }
                if (markPulses is >= MinMarkPulses and <= MaxMarkPulses)
                {
                    state = DecodeState.MarkFinal;
                    finalPulses = 1;
                    return;
                }
                ResetDecoder(durationUnits);
                return;
            }

            if (state == DecodeState.MarkFinal)
            {
                if (classified != 1)
                {
                    ResetDecoder(durationUnits);
                    return;
                }
                finalPulses++;
                if (finalPulses == FinalMarkPulses)
                {
                    state = DecodeState.Data;
                }
                return;
            }

            AcceptDataPulse((byte)classified);
        }

        private void AcceptDataPulse(byte pulseClass)
        {
            if (bitCount < 8)
            {
                byteValue = (byte)((byteValue << 1) | pulseClass);
                bitCount++;
                return;
            }

            if (pulseClass != 1)
            {
                ResetDecoder(0);
                return;
            }

            AcceptByte(byteValue);
            byteValue = 0;
            bitCount = 0;
        }

        private void AcceptByte(byte value)
        {
            int index = byteIndex;
            if (index < expectedBytes)
            {
                checksum = unchecked((ushort)(checksum + BitOperations.PopCount(value)));
                if (mode == DecoderMode.Header)
                {
                    headerBuffer[index] = value;
                }
                else
                {
                    PublishEvent(SharpMzDecoderEventType.DataByte, value, index);
                }
            }
            else
            {
                recordedChecksum = (ushort)((recordedChecksum << 8) | value);
            }

            byteIndex++;
            if (byteIndex != expectedBytes + 2)
            {
                return;
            }

            bool valid = recordedChecksum == checksum;
            if (mode == DecoderMode.Header)
            {
                if (valid)
                {
                    validatedHeader = (byte[])headerBuffer.Clone();
                    validatedShortX8 = shortX8;
                    validatedPhysicalHighX8 = physicalHighIntervals == 0
                        ? 0
                        : ((physicalHighUnitsTotal * 8) + (physicalHighIntervals / 2)) /
                            physicalHighIntervals;
                    mode = DecoderMode.Stopped;
                    PublishEvent(SharpMzDecoderEventType.HeaderValid, 0, 0);
                }
                else
                {
                    BeginDuplicateGap(HeaderBytes);
                }
                return;
            }

            PublishEvent(
                valid ? SharpMzDecoderEventType.BlockValid : SharpMzDecoderEventType.BlockInvalid,
                0,
                expectedBytes);
            mode = DecoderMode.Stopped;
        }

        private void BeginDuplicateGap(int byteCount)
        {
            state = DecodeState.DuplicateGap;
            leaderPulses = 0;
            markPulses = 0;
            finalPulses = 0;
            bitCount = 0;
            byteValue = 0;
            byteIndex = 0;
            expectedBytes = byteCount;
            checksum = 0;
            recordedChecksum = 0;
            copyIndex = 1;
        }

        private void PublishEvent(SharpMzDecoderEventType type, byte value, int index)
        {
            pendingEvent ??= new SharpMzDecoderEvent(
                type,
                value,
                index,
                checksum,
                recordedChecksum,
                leaderPulses,
                copyIndex);
        }

        private void TrackLeaderPhysicalHigh(long durationUnits)
        {
            if (state != DecodeState.SearchLeader || leaderPulses == 0)
            {
                return;
            }

            if (physicalHighIntervals == int.MaxValue)
            {
                return;
            }
            physicalHighUnitsTotal = checked(physicalHighUnitsTotal + durationUnits);
            physicalHighIntervals++;
        }
    }
}
