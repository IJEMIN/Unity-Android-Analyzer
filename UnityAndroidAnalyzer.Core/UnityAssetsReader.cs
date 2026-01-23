using System.Text;
using System.Collections.Generic;

namespace UnityAndroidAnalyzer.Core;

public class UnityAssetsReader
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;

    private bool _isBigEndian = true;

    private static Dictionary<(string file, long pathId), string> _globalMonoScripts = new();
    private static UnityParsingData _parsingData = new();
    private List<string> _externals = new();
    private string _currentFileName = "";

    public static UnityParsingData ParsingData => _parsingData;

    public UnityAssetsReader(Stream stream)
    {
        _stream = stream;
        _reader = new BinaryReader(stream);
    }

    public static void ClearCache()
    {
        _globalMonoScripts.Clear();
        _parsingData.Clear();
    }

    public void Read(string fileName = "", bool scriptsOnly = false)
    {
        _currentFileName = fileName;
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
        
        if (!scriptsOnly)
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
            if (!scriptsOnly)
                Console.WriteLine($"[Assets] UnityVersion: {assetUnityVersion}, Platform: {targetPlatform}");
        }
        
        // Object Info Table
        int objectCount;
        var typeClassIds = new List<int>();
        if (version >= 13)
        {
            bool hasTypeTree = _reader.ReadBoolean();
            int typeCount = ReadInt32();
            if (!scriptsOnly)
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

        if (!scriptsOnly)
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

            // Pre-parse MonoScript to populate cache
            if (classId == 115) // MonoScript
            {
                ParseMonoScript(info, dataOffset, version, endian);
            }
        }

        // Scripts (version >= 11)
        if (version >= 11)
        {
            int scriptCount = ReadInt32();
            for (int i = 0; i < scriptCount; i++)
            {
                if (version >= 14) ReadInt64(); else ReadInt32(); // pathId
            }
        }

        // Externals
        int externalCount = ReadInt32();
        if (externalCount > 0 && !scriptsOnly) Console.WriteLine($"[Assets] Externals for {_currentFileName}: {externalCount}");
        for (int i = 0; i < externalCount; i++)
        {
            if (version >= 6) ReadStringNullTerminated(); // asset name
            _reader.ReadBytes(16); // guid
            ReadInt32(); // type
            string pathName = ReadStringNullTerminated();
            string fileNameOnly = Path.GetFileName(pathName);
            _externals.Add(fileNameOnly);
            if (!scriptsOnly)
                Console.WriteLine($"[Assets] External {i+1}: {fileNameOnly} (original: {pathName})");
        }

        if (scriptsOnly) return;

        foreach (var objectId in objectIdsList)
        {
            var info = objectInfos[objectId];
            if (info.ClassId == 1) // GameObject
            {
                ParseGameObject(info, objectInfos, dataOffset, version, endian);
            }
        }
    }

    private void ParseMonoScript(ObjectInfo info, long dataOffset, int version, byte endian)
    {
        long currentPos = _stream.Position;
        _stream.Position = dataOffset + info.ByteStart;
        try
        {
            bool oldEndian = _isBigEndian;
            _isBigEndian = (endian == 1);

            // string m_Name
            int nameLength = ReadInt32();
            string scriptName = "";
            if (nameLength > 0 && nameLength < 1024)
            {
                byte[] nameBytes = _reader.ReadBytes(nameLength);
                scriptName = Encoding.UTF8.GetString(nameBytes);
                Align(4);
            }

            // int m_ExecutionOrder
            ReadInt32();

            // Hash128 m_PropertiesHash (16 bytes)
            _reader.ReadBytes(16);

            // string m_ClassName
            int classLength = ReadInt32();
            if (classLength > 0 && classLength < 1024)
            {
                byte[] classBytes = _reader.ReadBytes(classLength);
                string className = Encoding.UTF8.GetString(classBytes);
                Align(4);

                // string m_Namespace
                int nsLength = ReadInt32();
                if (nsLength > 0 && nsLength < 1024)
                {
                    byte[] nsBytes = _reader.ReadBytes(nsLength);
                    string nsName = Encoding.UTF8.GetString(nsBytes);
                    if (!string.IsNullOrEmpty(nsName))
                    {
                        className = nsName + "." + className;
                    }
                    Align(4);
                }
                
                // string m_AssemblyName
                int assemblyLength = ReadInt32();
                if (assemblyLength > 0 && assemblyLength < 1024)
                {
                    _reader.ReadBytes(assemblyLength);
                    Align(4);
                }
                
                Console.WriteLine($"[Assets] MonoScript: \"{className}\" at PathId {info.PathId} in {_currentFileName}");
                _globalMonoScripts[(_currentFileName, info.PathId)] = className;
                _parsingData.AllMonoScripts.Add(className);
            }
            else
            {
                // If ClassName is empty, fallback to m_Name
                if (!string.IsNullOrEmpty(scriptName))
                {
                    Console.WriteLine($"[Assets] MonoScript (Fallback to Name): \"{scriptName}\" at PathId {info.PathId} in {_currentFileName}");
                    _globalMonoScripts[(_currentFileName, info.PathId)] = scriptName;
                    _parsingData.AllMonoScripts.Add(scriptName);
                }
            }

            _isBigEndian = oldEndian;
        }
        catch
        {
            // Ignore
        }
        finally
        {
            _stream.Position = currentPos;
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
                    if (compInfo.ClassId == 114) // MonoBehaviour
                    {
                        componentNames.Add(GetMonoBehaviourScriptName(compInfo, allObjects, dataOffset, version, endian));
                    }
                    else
                    {
                        componentNames.Add(GetUnitClassName(compInfo.ClassId));
                    }
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

            if (_currentFileName.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var comp in componentNames)
                {
                    _parsingData.SceneComponents.Add(comp);
                }
            }
            
            _isBigEndian = oldEndian;
        } catch {
            // Silently ignore or log error
        }

        _stream.Position = currentPos;
    }

    private string GetMonoBehaviourScriptName(ObjectInfo info, Dictionary<long, ObjectInfo> allObjects, long dataOffset, int version, byte endian)
    {
        long currentPos = _stream.Position;
        _stream.Position = dataOffset + info.ByteStart;
        try
        {
            bool oldEndian = _isBigEndian;
            _isBigEndian = (endian == 1);

            // MonoBehaviour structure:
            // 1. m_GameObject (PPtr)
            ReadInt32(); // fileId
            if (version >= 14) ReadInt64(); else ReadInt32(); // pathId

            // 2. m_Enabled (byte)
            _reader.ReadByte();
            Align(4);

            // 3. m_Script (PPtr)
            int scriptFileId = ReadInt32();
            long scriptPathId = (version >= 14) ? ReadInt64() : ReadInt32();

            _isBigEndian = oldEndian;

            if (TryResolveScript(scriptFileId, scriptPathId, out var scriptName, out var targetFile))
            {
                return scriptName;
            }
            
            if (scriptPathId != 0)
            {
                Console.WriteLine($"[Assets] MonoBehaviour script not found in {_currentFileName}: FileId={scriptFileId} ({targetFile}), PathId={scriptPathId}");
            }
            
            return "MonoBehaviour";
        }
        catch
        {
            return "MonoBehaviour";
        }
        finally
        {
            _stream.Position = currentPos;
        }
    }

    private bool TryResolveScript(int fileId, long pathId, out string scriptName, out string targetFile)
    {
        targetFile = _currentFileName;
        if (fileId > 0 && fileId <= _externals.Count)
        {
            targetFile = _externals[fileId - 1];
        }

        if (_globalMonoScripts.TryGetValue((targetFile, pathId), out scriptName!))
        {
            return true;
        }

        // Fallback: search all files if not found in the specific file
        foreach (var entry in _globalMonoScripts)
        {
            if (entry.Key.pathId == pathId)
            {
                scriptName = entry.Value;
                return true;
            }
        }

        scriptName = "";
        return false;
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
            108 => "Light",
            111 => "Animation",
            114 => "MonoBehaviour",
            115 => "MonoScript",
            124 => "Flare",
            128 => "Font",
            137 => "PolygonCollider2D",
            198 => "ParticleSystem",
            199 => "ParticleSystemRenderer",
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

public class UnityParsingData
{
    public HashSet<string> AllMonoScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SceneComponents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        AllMonoScripts.Clear();
        SceneComponents.Clear();
    }
}
