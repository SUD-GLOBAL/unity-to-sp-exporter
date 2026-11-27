using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Sud.UnityExporter
{
    // Unity runs the converter synchronously. Standalone writers get a short-lived scope;
    // writers called by the same export share file observations until it completes.
    internal sealed class SudExportFiles : IDisposable
    {
        [ThreadStatic] private static SudExportFiles current;
        private readonly SudExportFiles parent;
        private readonly Dictionary<string, Fingerprint> files;

        private SudExportFiles()
        {
            parent = current;
            files = parent?.files ?? new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
            current = this;
        }

        public static SudExportFiles Begin() => new SudExportFiles();
        public void Dispose() => current = parent;

        internal sealed class Fingerprint
        {
            public long Length;
            public DateTime LastWriteUtc;
            public string Sha256;
            public string Md5;
            public SudWasmInfo Wasm;
        }

        public static Fingerprint Read(string path)
        {
            var file = new FileInfo(path);
            if (current.files.TryGetValue(file.FullName, out var found) &&
                found.Length == file.Length && found.LastWriteUtc == file.LastWriteTimeUtc) return found;
            var snapshot = new Fingerprint { Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
            using (var input = file.OpenRead())
            using (var sha = SHA256.Create())
            using (var md5 = MD5.Create())
            {
                var buffer = new byte[64 * 1024];
                int size;
                while ((size = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, size, null, 0);
                    md5.TransformBlock(buffer, 0, size, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                snapshot.Sha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                snapshot.Md5 = BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
            }
            file.Refresh();
            if (snapshot.Length != file.Length || snapshot.LastWriteUtc != file.LastWriteTimeUtc)
                throw new IOException("Build file changed while reading: " + file.FullName);
            current.files[file.FullName] = snapshot;
            return snapshot;
        }

        public static SudWasmInfo InspectWasm(string path)
        {
            var file = Read(path);
            return file.Wasm ?? (file.Wasm = SudWasmInspector.Inspect(path));
        }
    }
}
