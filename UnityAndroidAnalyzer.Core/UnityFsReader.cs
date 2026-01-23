using System.Text;
using K4os.Compression.LZ4;

namespace UnityAndroidAnalyzer.Core;

public class UnityFsReader
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private int _version;
    private string _unityVersion = "";

    public UnityFsReader(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(stream, Encoding.UTF8);
    }

    public void Read(bool scriptsOnly = false)
    {
        string signature = ReadStringNullTerminated();
        _version = ReadInt32BE();
        _unityVersion = ReadStringNullTerminated();
        string unityRevision = ReadStringNullTerminated();
        long size = ReadInt64BE();

        if (!scriptsOnly)
            Console.WriteLine($"[UnityFS] Signature: {signature}, Version: {_version}, Unity: {_unityVersion}, Size: {size}");

        if (signature != "UnityFS") return;

        int compressedSize = ReadInt32BE();
        int uncompressedSize = ReadInt32BE();
        int flags = ReadInt32BE();

        if (!scriptsOnly)
            Console.WriteLine($"[UnityFS] Blocks Info: compressedSize={compressedSize}, uncompressedSize={uncompressedSize}, flags=0x{flags:X}");

        // Read Block Info
        byte[] blockInfoBytes;
        long blocksStartingPos;
        if ((flags & 0x80) != 0) // At end
        {
            long currentPos = _stream.Position;
            _stream.Position = _stream.Length - compressedSize;
            blockInfoBytes = _reader.ReadBytes(compressedSize);
            
            blocksStartingPos = currentPos;
            // Version 7+ (Unity 2020+) uses 16-byte alignment for the data blocks
            if (_version >= 7)
            {
                blocksStartingPos = (blocksStartingPos + 15) & ~15;
            }
            
            // Revert stream position to original to keep logic consistent
            _stream.Position = currentPos;
        }
        else // After header
        {
            // Version 7+ (Unity 2020+) requires 16-byte alignment before the Block Info if it follows the header
            if (_version >= 7)
            {
                long alignedPos = (_stream.Position + 15) & ~15;
                if (alignedPos != _stream.Position)
                {
                    if (!scriptsOnly)
                        Console.WriteLine($"[UnityFS] Aligning header from {_stream.Position} to {alignedPos}");
                    _stream.Position = alignedPos;
                }
            }
            
            blockInfoBytes = _reader.ReadBytes(compressedSize);
            
            blocksStartingPos = _stream.Position;
            if (_version >= 7)
            {
                blocksStartingPos = (blocksStartingPos + 15) & ~15;
            }
        }

        // Handle compression for Block Info
        int compressionType = flags & 0x3F;
        byte[] uncompressedBlockInfo;
        if (compressionType == 0) // None
        {
            uncompressedBlockInfo = blockInfoBytes;
        }
        else if (compressionType == 2 || compressionType == 3) // LZ4 or LZ4HC
        {
            // UnityFS block info can be LZ4 compressed. 
            // The uncompressedSize is the exact size needed for the output buffer.
            uncompressedBlockInfo = new byte[uncompressedSize];
            
            // Note: LZ4Codec.Decode in K4os.Compression.LZ4 expects the EXACT uncompressed size.
            // Some Unity versions might have quirks, but generally this should work if sizes are correct.
            // If it returns -1, the output buffer might be considered too small or input is invalid.
            int decoded = LZ4Codec.Decode(blockInfoBytes, 0, blockInfoBytes.Length, uncompressedBlockInfo, 0, uncompressedSize);
            
            if (decoded < 0)
            {
                // Try with a larger buffer just in case uncompressedSize was misleading
                byte[] largerBuffer = new byte[uncompressedSize + 256]; 
                decoded = LZ4Codec.Decode(blockInfoBytes, 0, blockInfoBytes.Length, largerBuffer, 0, largerBuffer.Length);
                if (decoded > 0)
                {
                    uncompressedBlockInfo = new byte[decoded];
                    Array.Copy(largerBuffer, uncompressedBlockInfo, decoded);
                }
            }

            if (decoded != uncompressedSize && decoded > 0)
            {
                if (!scriptsOnly)
                    Console.WriteLine($"[UnityFS] LZ4 decoded size mismatch. Expected {uncompressedSize}, got {decoded}. Proceeding with decoded size.");
                // Some Unity versions might have slightly different sizes due to alignment or header nuances.
            }
            else if (decoded <= 0)
            {
                Console.WriteLine($"[UnityFS] LZ4 decoding failed. Expected {uncompressedSize}, got {decoded}");
                return;
            }
        }
        else
        {
            // TODO: LZMA compression for Block Info not implemented yet
            Console.WriteLine($"[UnityFS] Compression type {compressionType} for block info is not supported yet.");
            return;
        }

        using var biMs = new MemoryStream(uncompressedBlockInfo);
        using var biReader = new BinaryReader(biMs);

        // Guid (16 bytes)
        biReader.ReadBytes(16);

        int blockCount = ReadInt32BE(biReader);
        if (!scriptsOnly)
            Console.WriteLine($"[UnityFS] Block Count: {blockCount}, Blocks Starting Pos: {blocksStartingPos}");

        var blocks = new List<StorageBlock>();
        for (int i = 0; i < blockCount; i++)
        {
            var block = new StorageBlock
            {
                UncompressedSize = ReadUInt32BE(biReader),
                CompressedSize = ReadUInt32BE(biReader),
                Flags = ReadUInt16BE(biReader)
            };
            blocks.Add(block);
            // Console.WriteLine($"[UnityFS] Block {i}: Compressed={block.CompressedSize}, Uncompressed={block.UncompressedSize}, Flags=0x{block.Flags:X}");
        }

        int nodeCount = ReadInt32BE(biReader);
        if (!scriptsOnly)
            Console.WriteLine($"[UnityFS] Node Count: {nodeCount}");

        for (int i = 0; i < nodeCount; i++)
        {
            long offset = ReadInt64BE(biReader);
            long nodeSize = ReadInt64BE(biReader);
            int nodeFlags = ReadInt32BE(biReader);
            string path = ReadStringNullTerminated(biReader);
            
            if (!scriptsOnly)
                Console.WriteLine($"[UnityFS] Node: {path}, Size: {nodeSize}, Offset: {offset}, Flags: 0x{nodeFlags:X}");
            
            // Handle Assets or SharedAssets
            bool isSerialized = (nodeFlags & 0x04) != 0;
            bool isAssets = path.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) || 
                            path.EndsWith(".sharedassets", StringComparison.OrdinalIgnoreCase) || 
                            path.Contains("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || 
                            path.StartsWith("level", StringComparison.OrdinalIgnoreCase) || 
                            path.Contains("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains("unity default resources", StringComparison.OrdinalIgnoreCase);
            
            if (isSerialized || isAssets)
            {
                // Skip nodes that are likely just resource containers and too large or non-assets
                if (path.EndsWith(".resS") || path.EndsWith(".resource"))
                {
                    if (!scriptsOnly)
                        Console.WriteLine($"[UnityFS] Skipping resource node: {path}");
                    continue;
                }
                
                if (!scriptsOnly)
                    Console.WriteLine($"[UnityFS] Parsing embedded assets file: {path} (Flags: 0x{nodeFlags:X}, Serialized: {isSerialized})");
                try
                {
                    byte[] nodeData = ExtractNodeData(blocks, blocksStartingPos, offset, nodeSize);
                    using var nodeMs = new MemoryStream(nodeData);
                    var assetsReader = new UnityAssetsReader(nodeMs);
                    assetsReader.Read(Path.GetFileName(path), scriptsOnly);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UnityFS] Failed to parse node {path}: {ex.Message}");
                }
            }
            else
            {
                // Console.WriteLine($"[UnityFS] Skipping non-asset node: {path}");
            }
        }
    }

    private byte[] ExtractNodeData(List<StorageBlock> blocks, long blocksStartingPos, long nodeOffset, long nodeSize)
    {
        byte[] result = new byte[nodeSize];
        long currentPosInBundle = blocksStartingPos; 

        // We need to find which blocks contain the data for this node
        long currentOffset = 0;
        long bytesRead = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            long blockUncompressedSize = block.UncompressedSize;
            long blockCompressedSize = block.CompressedSize;

            // Check if this block contains part of our node
            if (currentOffset + blockUncompressedSize > nodeOffset && currentOffset < nodeOffset + nodeSize)
            {
                // Decompress this block
                _stream.Position = currentPosInBundle;
                byte[] compressedData = _reader.ReadBytes((int)blockCompressedSize);
                byte[] uncompressedData;

                int compressionType = block.Flags & 0x3F;
                if (compressionType == 0) // None
                {
                    uncompressedData = compressedData;
                }
                else if (compressionType == 2 || compressionType == 3) // LZ4
                {
                    uncompressedData = new byte[blockUncompressedSize];
                    int decoded = LZ4Codec.Decode(compressedData, 0, compressedData.Length, uncompressedData, 0, (int)blockUncompressedSize);
                    
                    if (decoded < 0)
                    {
                        byte[] retryBuffer = new byte[blockUncompressedSize + 65536]; 
                        decoded = LZ4Codec.Decode(compressedData, 0, compressedData.Length, retryBuffer, 0, retryBuffer.Length);
                        if (decoded > 0)
                        {
                            uncompressedData = new byte[decoded];
                            Array.Copy(retryBuffer, uncompressedData, decoded);
                        }
                    }

                    if (decoded < 0)
                    {
                         Console.WriteLine($"[UnityFS] LZ4 block decoding failed for block {i} at {currentPosInBundle}, compressedSize {blockCompressedSize}, uncompressedSize {blockUncompressedSize}, flags 0x{block.Flags:X}.");
                         throw new Exception($"LZ4 block decoding failed (Block {i})");
                    }
                }
                else
                {
                    throw new Exception($"Compression type {compressionType} in block {i} not supported");
                }

                // Copy relevant part to result
                long startInBlock = Math.Max(0, nodeOffset - currentOffset);
                long endInBlock = Math.Min(blockUncompressedSize, nodeOffset + nodeSize - currentOffset);
                long sizeToCopy = endInBlock - startInBlock;

                Array.Copy(uncompressedData, (int)startInBlock, result, (int)bytesRead, (int)sizeToCopy);
                bytesRead += sizeToCopy;
            }

            currentOffset += blockUncompressedSize;
            currentPosInBundle += blockCompressedSize;
            
            if (bytesRead >= nodeSize) break;
        }

        return result;
    }

    private struct StorageBlock
    {
        public uint UncompressedSize;
        public uint CompressedSize;
        public ushort Flags;
    }

    private string ReadStringNullTerminated(BinaryReader? reader = null)
    {
        var r = reader ?? _reader;
        var bytes = new List<byte>();
        while (true)
        {
            byte b = r.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private int ReadInt32BE(BinaryReader? reader = null)
    {
        var r = reader ?? _reader;
        var bytes = r.ReadBytes(4);
        Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes, 0);
    }

    private uint ReadUInt32BE(BinaryReader? reader = null)
    {
        var r = reader ?? _reader;
        var bytes = r.ReadBytes(4);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private ushort ReadUInt16BE(BinaryReader? reader = null)
    {
        var r = reader ?? _reader;
        var bytes = r.ReadBytes(2);
        Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    private long ReadInt64BE(BinaryReader? reader = null)
    {
        var r = reader ?? _reader;
        var bytes = r.ReadBytes(8);
        Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }
}
