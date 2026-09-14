using System;
using System.Collections.Generic;
using System.IO;

namespace QDTool
{
    internal static class TapeDocumentWriter
    {
        public static void SaveMzf(
            string path,
            TapeRecord record,
            bool preserveTrailing,
            bool createSidecar = false)
        {
            string sidecarPath = SidecarService.GetSidecarPath(path);
            bool writeSidecar = createSidecar || File.Exists(sidecarPath);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = SerializeMzf(record, preserveTrailing)
            };
            if (writeSidecar)
            {
                files[sidecarPath] = SidecarService.SerializeMfi(record);
            }

            WriteAtomically(files);
            if (!preserveTrailing)
            {
                record.RemoveTrailingData();
            }
            ApplySavedMetadataState(new[] { record }, writeSidecar, MetadataOrigin.LoadedFromMfi);
        }

        public static void SaveMzt(
            string path,
            IReadOnlyList<TapeRecord> records,
            bool createSidecar = false)
        {
            string sidecarPath = SidecarService.GetSidecarPath(path);
            bool writeSidecar = createSidecar || File.Exists(sidecarPath);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = SerializeMzt(records)
            };
            if (writeSidecar)
            {
                files[sidecarPath] = SidecarService.SerializeMti(records);
            }

            WriteAtomically(files);
            foreach (TapeRecord record in records)
            {
                record.RemoveTrailingData();
            }
            ApplySavedMetadataState(records, writeSidecar, MetadataOrigin.LoadedFromMti);
        }

        public static string GenerateSidecar(
            string mainPath,
            TapeDocumentFormat format,
            IReadOnlyList<TapeRecord> records)
        {
            if (string.IsNullOrWhiteSpace(mainPath))
            {
                throw new ArgumentException("The current document has no file path.", nameof(mainPath));
            }

            byte[] content;
            MetadataOrigin persistedOrigin;
            if (format == TapeDocumentFormat.Mzf)
            {
                if (records.Count != 1)
                {
                    throw new InvalidOperationException("An MFI sidecar requires exactly one MZF record.");
                }
                content = SidecarService.SerializeMfi(records[0]);
                persistedOrigin = MetadataOrigin.LoadedFromMfi;
            }
            else if (format == TapeDocumentFormat.Mzt)
            {
                if (records.Count == 0)
                {
                    throw new InvalidOperationException("An MTI sidecar requires at least one MZT record.");
                }
                content = SidecarService.SerializeMti(records);
                persistedOrigin = MetadataOrigin.LoadedFromMti;
            }
            else
            {
                throw new InvalidOperationException("MFI/MTI can be generated only for an MZF or MZT document.");
            }

            string sidecarPath = SidecarService.GetSidecarPath(mainPath);
            WriteAtomically(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [sidecarPath] = content
            });
            ApplySavedMetadataState(records, sidecarWritten: true, persistedOrigin);
            return sidecarPath;
        }

        public static byte[] SerializeMzf(TapeRecord record, bool preserveTrailing)
        {
            using var stream = new MemoryStream();
            WriteRecord(stream, record);
            if (preserveTrailing && record.Body.TrailingData is { Length: > 0 } trailing)
            {
                stream.Write(trailing);
            }
            return stream.ToArray();
        }

        public static byte[] SerializeMzt(IReadOnlyList<TapeRecord> records)
        {
            using var stream = new MemoryStream();
            foreach (TapeRecord record in records)
            {
                WriteRecord(stream, record);
            }
            return stream.ToArray();
        }

        public static void WriteRecord(Stream stream, TapeRecord record)
        {
            byte[] header = record.GetSerializedHeader();
            stream.Write(header);
            MZQFileBody body = record.Body;
            if (body.MzfBody == null || body.MzfBody.Length != body.DataSize || body.DataSize != record.Header.MzfSize)
            {
                throw new InvalidDataException("The MZF header and body sizes are inconsistent.");
            }
            stream.Write(body.MzfBody, 0, body.DataSize);
        }

        private static void ApplySavedMetadataState(
            IEnumerable<TapeRecord> records,
            bool sidecarWritten,
            MetadataOrigin persistedOrigin)
        {
            foreach (TapeRecord record in records)
            {
                if (sidecarWritten)
                {
                    record.MetadataOrigin = persistedOrigin;
                }
                else
                {
                    record.ResetMetadataToImplicit();
                }
            }
        }

        private static void WriteAtomically(IReadOnlyDictionary<string, byte[]> files)
        {
            string transactionId = Guid.NewGuid().ToString("N");
            var temporary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var installed = new List<string>();

            try
            {
                foreach ((string target, byte[] content) in files)
                {
                    string fullTarget = Path.GetFullPath(target);
                    string? directory = Path.GetDirectoryName(fullTarget);
                    if (string.IsNullOrEmpty(directory))
                    {
                        throw new InvalidOperationException("The output path has no directory.");
                    }
                    Directory.CreateDirectory(directory);
                    string temp = Path.Combine(directory, $".{Path.GetFileName(fullTarget)}.{transactionId}.tmp");
                    File.WriteAllBytes(temp, content);
                    temporary[fullTarget] = temp;
                    backups[fullTarget] = File.Exists(fullTarget)
                        ? Path.Combine(directory, $".{Path.GetFileName(fullTarget)}.{transactionId}.bak")
                        : null;
                }

                foreach ((string target, string temp) in temporary)
                {
                    string? backup = backups[target];
                    if (backup != null)
                    {
                        File.Copy(target, backup, overwrite: true);
                        File.Move(temp, target, overwrite: true);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                    installed.Add(target);
                }
            }
            catch
            {
                for (int index = installed.Count - 1; index >= 0; index--)
                {
                    string target = installed[index];
                    string? backup = backups[target];
                    if (backup != null && File.Exists(backup))
                    {
                        File.Move(backup, target, overwrite: true);
                    }
                    else if (backup == null && File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                throw;
            }
            finally
            {
                foreach (string temp in temporary.Values)
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                foreach (string? backup in backups.Values)
                {
                    if (backup != null && File.Exists(backup)) File.Delete(backup);
                }
            }
        }
    }
}
