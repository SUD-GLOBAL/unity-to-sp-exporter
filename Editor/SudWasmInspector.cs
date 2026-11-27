using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sud.UnityExporter
{
    public sealed class SudWasmInfo
    {
        public int ImportedFunctionCount;
        public int DefinedFunctionCount;
        public int TotalFunctionCount;
        public bool HasNameSection;
        public uint NameSectionSize;
        public int NameSectionFunctionCount;
        public readonly Dictionary<int, string> FunctionNames = new Dictionary<int, string>();
    }

    public static class SudWasmInspector
    {
        public static SudWasmInfo Inspect(string wasmPath)
        {
            if (string.IsNullOrEmpty(wasmPath) || !File.Exists(wasmPath))
            {
                throw new FileNotFoundException("WebAssembly file not found.", wasmPath);
            }

            var bytes = File.ReadAllBytes(wasmPath);
            var reader = new Reader(bytes);
            if (reader.ReadByte() != 0x00 ||
                reader.ReadByte() != 0x61 ||
                reader.ReadByte() != 0x73 ||
                reader.ReadByte() != 0x6d)
            {
                throw new InvalidDataException("Invalid WebAssembly magic header.");
            }

            reader.Skip(4);

            var info = new SudWasmInfo();
            while (!reader.End)
            {
                var sectionId = reader.ReadByte();
                var sectionSize = reader.ReadVarUInt32();
                var sectionEnd = reader.Position + checked((int)sectionSize);
                if (sectionEnd > bytes.Length)
                {
                    throw new InvalidDataException("Invalid WebAssembly section size.");
                }

                if (sectionId == 0)
                {
                    ReadCustomSection(reader, sectionEnd, sectionSize, info);
                }
                else if (sectionId == 2)
                {
                    info.ImportedFunctionCount = ReadImportFunctionCount(reader, sectionEnd);
                }
                else if (sectionId == 3)
                {
                    info.DefinedFunctionCount = checked((int)reader.ReadVarUInt32());
                }

                reader.Position = sectionEnd;
            }

            info.TotalFunctionCount = info.ImportedFunctionCount + info.DefinedFunctionCount;
            return info;
        }

        private static void ReadCustomSection(Reader reader, int sectionEnd, uint sectionSize, SudWasmInfo info)
        {
            var name = reader.ReadName();
            if (name != "name")
            {
                return;
            }

            info.HasNameSection = true;
            info.NameSectionSize = sectionSize;

            while (reader.Position < sectionEnd)
            {
                var subsectionId = reader.ReadByte();
                var subsectionSize = reader.ReadVarUInt32();
                var subsectionEnd = reader.Position + checked((int)subsectionSize);
                if (subsectionEnd > sectionEnd)
                {
                    throw new InvalidDataException("Invalid wasm name subsection size.");
                }

                if (subsectionId == 1)
                {
                    var count = checked((int)reader.ReadVarUInt32());
                    info.NameSectionFunctionCount = count;
                    for (var i = 0; i < count; i++)
                    {
                        var index = checked((int)reader.ReadVarUInt32());
                        info.FunctionNames[index] = reader.ReadName();
                    }
                }

                reader.Position = subsectionEnd;
            }
        }

        private static int ReadImportFunctionCount(Reader reader, int sectionEnd)
        {
            var importCount = checked((int)reader.ReadVarUInt32());
            var functionCount = 0;
            for (var i = 0; i < importCount; i++)
            {
                reader.ReadName();
                reader.ReadName();
                var kind = reader.ReadByte();
                if (kind == 0)
                {
                    reader.ReadVarUInt32();
                    functionCount++;
                }
                else if (kind == 1)
                {
                    SkipTableType(reader);
                }
                else if (kind == 2)
                {
                    SkipLimits(reader);
                }
                else if (kind == 3)
                {
                    reader.ReadByte();
                    reader.ReadByte();
                }
                else
                {
                    throw new InvalidDataException("Unsupported wasm import kind: " + kind);
                }
            }

            if (reader.Position > sectionEnd)
            {
                throw new InvalidDataException("Invalid wasm import section.");
            }

            return functionCount;
        }

        private static void SkipTableType(Reader reader)
        {
            reader.ReadByte();
            SkipLimits(reader);
        }

        private static void SkipLimits(Reader reader)
        {
            var flags = reader.ReadVarUInt32();
            reader.ReadVarUInt32();
            if ((flags & 1) != 0)
            {
                reader.ReadVarUInt32();
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _bytes;

            public Reader(byte[] bytes)
            {
                _bytes = bytes;
            }

            public int Position { get; set; }
            public bool End { get { return Position >= _bytes.Length; } }

            public byte ReadByte()
            {
                if (Position >= _bytes.Length)
                {
                    throw new EndOfStreamException();
                }

                return _bytes[Position++];
            }

            public uint ReadVarUInt32()
            {
                uint result = 0;
                var shift = 0;
                while (true)
                {
                    var b = ReadByte();
                    result |= (uint)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0)
                    {
                        return result;
                    }

                    shift += 7;
                    if (shift > 35)
                    {
                        throw new InvalidDataException("Invalid unsigned LEB128 value.");
                    }
                }
            }

            public string ReadName()
            {
                var length = checked((int)ReadVarUInt32());
                if (Position + length > _bytes.Length)
                {
                    throw new EndOfStreamException();
                }

                var value = Encoding.UTF8.GetString(_bytes, Position, length);
                Position += length;
                return value;
            }

            public void Skip(int count)
            {
                Position += count;
                if (Position > _bytes.Length)
                {
                    throw new EndOfStreamException();
                }
            }
        }
    }
}
