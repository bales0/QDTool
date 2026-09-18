using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace QDTool
{
    public partial class WavAnalysisStatisticsWindow : Window
    {
        internal WavAnalysisStatisticsWindow(WavHeuristicStatistics statistics)
        {
            InitializeComponent();
            statisticsText.Text = BuildText(statistics);
        }

        private static string BuildText(WavHeuristicStatistics statistics)
        {
            PcmAudioFormat format = statistics.Format;
            var text = new StringBuilder();
            text.AppendLine($"Source format:         {statistics.SourceFormat}");
            text.AppendLine($"Duration:              {FormatDuration(statistics.DurationSeconds)}");
            text.AppendLine($"Sample rate:           {format.SampleRate:N0} Hz");
            text.AppendLine($"Bit depth:             {format.BitsPerSample} bit");
            text.AppendLine($"Channels:              {format.Channels}");
            text.AppendLine($"PCM frames:            {format.FrameCount:N0}");
            text.AppendLine($"Signal polarity:       {FormatOverallPolarity(statistics.RecordRecoveries)}");
            text.AppendLine();
            text.AppendLine($"Header candidates:     {statistics.HeaderCandidates:N0}");
            text.AppendLine($"Payload candidates:    {statistics.PayloadCandidates:N0}");
            text.AppendLine($"Valid payload candidates: {statistics.ValidPayloadCandidates:N0}");
            text.AppendLine($"Result records:        {statistics.ResultRecords:N0}");
            text.AppendLine($"Reconstructed records: {statistics.ReconstructedRecords:N0}");
            text.AppendLine($"Selective recovery:    {(statistics.SelectiveRecoveryUsed ? "used" : "not needed")}");
            text.AppendLine();
            text.AppendLine("Detected records:");
            for (int index = 0; index < statistics.RecordRecoveries.Count; index++)
            {
                WavRecoveryInfo recovery = statistics.RecordRecoveries[index];
                text.AppendLine(
                    $"  {index + 1}. {TapeProfileNames.ToDisplayName(recovery.Header.Candidate.Profile)}, " +
                    $"polarity {FormatRecordPolarity(recovery)}");
            }
            return text.ToString();
        }

        private static string FormatOverallPolarity(IReadOnlyList<WavRecoveryInfo> recoveries)
        {
            bool anyNormal = false;
            bool anyInverted = false;
            foreach (WavRecoveryInfo recovery in recoveries)
            {
                anyInverted |= recovery.Header.Candidate.Inverted || recovery.Payload.Candidate.Inverted;
                anyNormal |= !recovery.Header.Candidate.Inverted || !recovery.Payload.Candidate.Inverted;
            }
            return (anyNormal, anyInverted) switch
            {
                (true, false) => "normal",
                (false, true) => "inverted",
                (true, true) => "mixed",
                _ => "not determined"
            };
        }

        private static string FormatRecordPolarity(WavRecoveryInfo recovery)
        {
            bool header = recovery.Header.Candidate.Inverted;
            bool payload = recovery.Payload.Candidate.Inverted;
            if (header == payload)
            {
                return header ? "inverted" : "normal";
            }
            return $"mixed (header {(header ? "inverted" : "normal")}, " +
                $"payload {(payload ? "inverted" : "normal")})";
        }

        internal static string FormatDuration(double durationSeconds)
        {
            long totalTenths = checked((long)Math.Round(
                durationSeconds * 10,
                MidpointRounding.AwayFromZero));
            long hours = totalTenths / 36000;
            long minutes = (totalTenths / 600) % 60;
            long seconds = (totalTenths / 10) % 60;
            long tenths = totalTenths % 10;
            return $"{hours}:{minutes:00}:{seconds:00},{tenths}";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
