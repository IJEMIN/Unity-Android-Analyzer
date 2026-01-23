using System.Text;
using System.Collections.Generic;

namespace UnityAndroidAnalyzer.Core;

public class UnityAssetsReader
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;

    private bool _isBigEndian = true;

    public UnityAssetsReader(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(stream);
    }

    public void Read()
    {
        _isBigEndian = true; // Header is always Big Endian
        long metadataSize = ReadInt32();
        long fileSize = ReadInt32();
        int version = ReadInt32();
        long dataOffset = ReadInt32();
        
        byte endian = 0;
        if (version >= 9)
        {
            endian = _reader.ReadByte();
            _reader.ReadBytes(3); // reserved
        }

        if (version >= 22)
        {
            // For version 22+, metadataSize is still 32-bit uint in the extension? 
            // Actually, let's re-read the first 4 bytes of extension as metadataSize
            metadataSize = ReadUInt32(); 
            fileSize = ReadInt64();
            dataOffset = ReadInt64();
            ReadInt64(); // unknown
        }
        
        Console.WriteLine($"[Assets] Version: {version}, MetadataSize: {metadataSize}, FileSize: {fileSize}, DataOffset: {dataOffset}, Endian: {endian}");

        // Switch endianness for metadata if version >= 22
        if (version >= 22)
        {
            _isBigEndian = endian == 1;
        }

        if (version >= 7)
        {
            string assetUnityVersion = ReadStringNullTerminated();
            int targetPlatform = ReadInt32();
            Console.WriteLine($"[Assets] UnityVersion: {assetUnityVersion}, Platform: {targetPlatform}");
        }
        
        // Object Info Table
        int objectCount;
        var typeClassIds = new List<int>();
        if (version >= 13)
        {
            bool hasTypeTree = _reader.ReadBoolean();
            int typeCount = ReadInt32();
            Console.WriteLine($"[Assets] HasTypeTree: {hasTypeTree}, TypeCount: {typeCount}");
            for (int i = 0; i < typeCount; i++)
            {
                int classId = ReadInt32();
                typeClassIds.Add(classId);

                if (version >= 16) _reader.ReadByte(); // isStrippedType
                if (version >= 17) ReadInt16(); // scriptTypeIndex

                if (version >= 13)
                {
                    if (classId == 114 || classId < 0)
                    {
                        _reader.ReadBytes(16); // script id / hash
                    }
                    _reader.ReadBytes(16); // type hash
                }

                if (hasTypeTree)
                {
                    int nodeCount = ReadInt32();
                    int stringSize = ReadInt32();
                    if (nodeCount < 0 || stringSize < 0)
                    {
                         throw new Exception($"Invalid TypeTree nodeCount({nodeCount}) or stringSize({stringSize}) at type {i}");
                    }
                    int nodeSize = (version >= 19) ? 32 : 24;
                    _reader.ReadBytes(nodeCount * nodeSize + stringSize);
                }
            }
            objectCount = ReadInt32();
        }
        else 
        {
            objectCount = ReadInt32();
        }
        
        if (version >= 22)
        {
            Align(4);
        }

        Console.WriteLine($"[Assets] Object Count: {objectCount}");

        var objectInfos = new Dictionary<long, ObjectInfo>();
        var objectIdsList = new List<long>();

        for (int i = 0; i < objectCount; i++)
        {
            if (version >= 22) Align(4);
            
            long objectId;
            if (version >= 14) objectId = ReadInt64();
            else objectId = ReadInt32();

            long byteStart;
            if (version >= 22) byteStart = ReadInt64();
            else byteStart = ReadInt32();

            uint byteSize = ReadUInt32();
            int typeId = ReadInt32();

            if (version < 16)
            {
                ReadUInt16(); // classId
            }
            if (version == 15 || version == 16)
            {
                _reader.ReadByte();
            }

            int classId = typeId;
            if (version >= 16)
            {
                if (typeId >= 0 && typeId < typeClassIds.Count)
                    classId = typeClassIds[typeId];
            }

            var info = new ObjectInfo { PathId = objectId, ByteStart = byteStart, ByteSize = byteSize, TypeId = typeId, ClassId = classId };
            objectInfos[objectId] = info;
            objectIdsList.Add(objectId);
        }

        foreach (var objectId in objectIdsList)
        {
            var info = objectInfos[objectId];
            if (info.ClassId == 1) // GameObject
            {
                ParseGameObject(info, objectInfos, dataOffset, version, endian);
            }
        }
    }

    private void ParseGameObject(ObjectInfo info, Dictionary<long, ObjectInfo> allObjects, long dataOffset, int version, byte endian)
    {
        long currentPos = _stream.Position;
        _stream.Position = dataOffset + info.ByteStart;
        
        try {
            bool oldEndian = _isBigEndian;
            _isBigEndian = (endian == 1);

            // Unity 5.4+ GameObject structure:
            // 1. m_Component (Array of PPtr)
            // 2. m_Layer (int)
            // 3. m_Name (string)
            // 4. m_Tag (uint16)
            // 5. m_IsActive (bool)

            int componentCount = ReadInt32();
            if (componentCount < 0 || componentCount > 1000) return;

            var componentNames = new List<string>();
            for (int j = 0; j < componentCount; j++)
            {
                int fileId = ReadInt32();
                long compPathId = (version >= 14) ? ReadInt64() : ReadInt32();
                
                if (allObjects.TryGetValue(compPathId, out var compInfo))
                {
                    componentNames.Add(GetUnitClassName(compInfo.ClassId));
                }
                else
                {
                    componentNames.Add($"Unknown({compPathId})");
                }
            }

            int layer = ReadInt32();
            
            int nameLength = ReadInt32();
            string name = "";
            if (nameLength > 0 && nameLength < 1024) {
                byte[] nameBytes = _reader.ReadBytes(nameLength);
                name = Encoding.UTF8.GetString(nameBytes);
                // Align(4);
            }

            if (!string.IsNullOrEmpty(name))
            {
                Console.WriteLine($"[Assets] GameObject: \"{name}\", Components: [{string.Join(", ", componentNames)}]");
            }
            
            _isBigEndian = oldEndian;
        } catch {
            // Silently ignore or log error
        }

        _stream.Position = currentPos;
    }

    private string GetUnitClassName(int classId)
    {
        return classId switch
        {
            1 => "GameObject",
            2 => "Component",
            4 => "Transform",
            20 => "Camera",
            21 => "Material",
            23 => "Renderer",
            28 => "Texture2D",
            33 => "MeshFilter",
            43 => "Mesh",
            48 => "Shader",
            64 => "MeshRenderer",
            65 => "GUITexture",
            81 => "AudioSource",
            92 => "GUIText",
            104 => "RenderTexture",
            114 => "MonoBehaviour",
            115 => "MonoScript",
            128 => "Font",
            213 => "Sprite",
            222 => "Canvas",
            223 => "CanvasRenderer",
            224 => "RectTransform",
            225 => "CanvasGroup",
            _ => $"ClassID({classId})"
        };
    }

    private struct ObjectInfo
    {
        public long PathId;
        public long ByteStart;
        public uint ByteSize;
        public int TypeId;
        public int ClassId;
    }

    private void Align(int alignment)
    {
        long pos = _stream.Position;
        long mod = pos % alignment;
        if (mod != 0)
        {
            _stream.Position += (alignment - mod);
        }
    }

    private string ReadStringNullTerminated()
    {
        var bytes = new List<byte>();
        try {
            while (true)
            {
                byte b = _reader.ReadByte();
                if (b == 0) break;
                bytes.Add(b);
            }
        } catch (EndOfStreamException) {
            // Ignore if we reached end of stream while reading version string
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private int ReadInt32()
    {
        var bytes = _reader.ReadBytes(4);
        if (bytes.Length < 4) throw new EndOfStreamException($"Expected 4 bytes for Int32, got {bytes.Length} at pos {_stream.Position}");
        if (_isBigEndian) Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes, 0);
    }

    private uint ReadUInt32()
    {
        var bytes = _reader.ReadBytes(4);
        if (bytes.Length < 4) throw new EndOfStreamException($"Expected 4 bytes for UInt32, got {bytes.Length} at pos {_stream.Position}");
        if (_isBigEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private short ReadInt16()
    {
        var bytes = _reader.ReadBytes(2);
        if (bytes.Length < 2) throw new EndOfStreamException($"Expected 2 bytes for Int16, got {bytes.Length} at pos {_stream.Position}");
        if (_isBigEndian) Array.Reverse(bytes);
        return BitConverter.ToInt16(bytes, 0);
    }

    private ushort ReadUInt16()
    {
        var bytes = _reader.ReadBytes(2);
        if (bytes.Length < 2) throw new EndOfStreamException($"Expected 2 bytes for UInt16, got {bytes.Length} at pos {_stream.Position}");
        if (_isBigEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    private long ReadInt64()
    {
        var bytes = _reader.ReadBytes(8);
        if (bytes.Length < 8) throw new EndOfStreamException($"Expected 8 bytes for Int64, got {bytes.Length} at pos {_stream.Position}");
        if (_isBigEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }
}
