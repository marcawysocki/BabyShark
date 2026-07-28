using MemoryPack;
using System;

namespace BabySharkBot.Setup
{
    [MemoryPackable]
    public partial class MapDatSnapshot
    {
        public string MapName { get; set; } = string.Empty;
        public string SourceFileName { get; set; } = string.Empty;
        public byte[] SourceBytes { get; set; } = Array.Empty<byte>();
    }
}