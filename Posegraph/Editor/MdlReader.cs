using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Posegraph.Editor;

/// <summary>
/// 只读手部吸附要用的部分的 .mdl 解析器：最高 LOD 的主网格的顶点位置、法线、骨骼权重 / 下标、三角形、子网格属性，以及骨骼名。
/// 文件头和各个表的排布照 Lumina 的 MdlFile（WTFPL）；Lumina 按旧格式（v5）读骨骼表，7.x 的模型（v6）骨骼表是变长的，读到后面就乱了，
/// 所以自己读到骨骼表为止（之后的形态键、包围盒用不上），顶点和索引按 LOD 记的绝对偏移直接取。
/// v6 骨骼表：每张表一个 (偏移, 数量)（各 u16，偏移以 4 字节为单位、从这条记录自己的位置算起），后面是 u16 骨骼下标，每张表补齐到 4 字节。
/// </summary>
public sealed class MdlReader
{
    /// <summary>BoneIndex / BoneWeight：每个顶点 Influences 个（模型骨骼名的下标、权重 0~1）。</summary>
    public sealed record Mesh(Vector3[] Positions, Vector3[] Normals, int[] BoneIndex, float[] BoneWeight, int[] Triangles);

    /// <summary>每个顶点最多几根骨骼（7.x 加了 8 骨骼蒙皮）。</summary>
    public const int Influences = 8;

    public string[] Bones { get; private set; } = [];
    public List<Mesh> Meshes { get; } = [];
    public uint Version { get; private set; }

    private const int HeaderSize = 0x44;
    private const int DeclarationSize = 17 * 8;

    private readonly byte[] _d;
    private int _p;

    private MdlReader(byte[] data) => _d = data;

    /// <summary>解析；attributes：模型当前启用的属性（子网格要用到的属性没全启用的不要，游戏也不画）。</summary>
    public static MdlReader Read(byte[] data, uint attributes)
    {
        var r = new MdlReader(data);
        r.Parse(attributes);
        return r;
    }

    private void Parse(uint attributes)
    {
        Version = U32();
        _p = 12;
        var declarationCount = U16();
        // 顶点声明：每个网格一个，17 个元素（流、偏移、类型、用途、用途序号、3 字节补齐），流为 255 表示结束
        var declarations = new List<(byte Stream, byte Offset, byte Type, byte Usage)>[declarationCount];
        for (var i = 0; i < declarationCount; i++)
        {
            var list = declarations[i] = [];
            for (var e = 0; e < 17; e++)
            {
                var at = HeaderSize + i * DeclarationSize + e * 8;
                if (_d[at] == 255) break;
                list.Add((_d[at], _d[at + 1], _d[at + 2], _d[at + 3]));
            }
        }
        _p = HeaderSize + declarationCount * DeclarationSize;
        U16();
        U16();
        var stringSize = (int)U32();
        var strings = _p;
        _p += stringSize;

        // 模型头（56 字节）
        var header = _p;
        _p += 4;
        int meshCount = U16(), attributeCount = U16(), submeshCount = U16(), materialCount = U16(), boneCount = U16(), boneTableCount = U16();
        _p = header + 24;   // 跳过形态键的三个数、LOD 数、标志 1
        int elementIdCount = U16();
        int terrainShadowMeshCount = _d[_p++];
        var flags2 = _d[_p++];
        _p = header + 38;   // 跳过两个裁剪距离和一个未知
        int terrainShadowSubmeshCount = U16();
        _p = header + 56;

        _p += elementIdCount * 32;
        var lods = _p;                         // 3 个，每个 60 字节
        _p += 3 * 60;
        if ((flags2 & 0x10) != 0) _p += 3 * 40;   // 额外 LOD
        var meshes = _p;
        _p += meshCount * 36;
        _p += attributeCount * 4;
        _p += terrainShadowMeshCount * 20;
        var submeshes = _p;
        _p += submeshCount * 16;
        _p += terrainShadowSubmeshCount * 12;
        _p += materialCount * 4;
        var boneNames = _p;
        _p += boneCount * 4;
        var tables = new int[boneTableCount][];
        if ((Version & 0xFF) >= 6)
        {
            var start = _p;
            for (var t = 0; t < boneTableCount; t++)
            {
                var entry = start + t * 4;
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(entry)), count = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(entry + 2));
                var data = entry + offset * 4;
                tables[t] = Enumerable.Range(0, count).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(data + i * 2))).ToArray();
            }
        }
        else
            for (var t = 0; t < boneTableCount; t++)
            {
                var at = _p + t * 132;
                var count = _d[at + 128];
                tables[t] = Enumerable.Range(0, count).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(at + i * 2))).ToArray();
            }

        Bones = Enumerable.Range(0, boneCount).Select(i => CString(strings + (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(boneNames + i * 4)))).ToArray();

        // LOD0 的主网格
        int lodMeshIndex = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(lods)), lodMeshCount = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(lods + 2));
        var vertexData = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(lods + 52));
        var indexData = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(lods + 56));
        for (var m = lodMeshIndex; m < lodMeshIndex + lodMeshCount && m < meshCount && m < declarationCount; m++)
        {
            var mp = meshes + m * 36;
            int vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(mp));
            var indexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(mp + 4));
            int subIndex = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(mp + 10)), subCount = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(mp + 12));
            int tableIndex = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(mp + 14));
            var startIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(mp + 16));
            var streamOffset = new int[3];
            var stride = new int[3];
            for (var s = 0; s < 3; s++)
            {
                streamOffset[s] = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(mp + 20 + s * 4));
                stride[s] = _d[mp + 32 + s];
            }
            var table = tableIndex < tables.Length ? tables[tableIndex] : [];

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var boneIndex = new int[vertexCount * Influences];
            var boneWeight = new float[vertexCount * Influences];
            foreach (var (stream, offset, type, usage) in declarations[m])
            {
                if (stream > 2) continue;
                for (var v = 0; v < vertexCount; v++)
                {
                    var at = vertexData + streamOffset[stream] + v * stride[stream] + offset;
                    if (at + 4 > _d.Length) break;
                    switch (usage)
                    {
                        case 0: positions[v] = Vec3(at, type); break;
                        case 3: normals[v] = Vec3(at, type); break;
                        // 权重、骨骼下标：类型 5 / 8 是 4 个字节，类型 17 是 8 个字节（8 骨骼蒙皮）；权重都是 0~255
                        case 1:
                            for (var k = 0; k < (type == 17 ? 8 : 4); k++) boneWeight[v * Influences + k] = _d[at + k] / 255f;
                            break;
                        case 2:
                            for (var k = 0; k < (type == 17 ? 8 : 4); k++)
                            {
                                var local = _d[at + k];
                                boneIndex[v * Influences + k] = local < table.Length ? table[local] : 0;
                            }
                            break;
                    }
                }
            }

            var triangles = new List<int>();
            void Add(int first, int count)
            {
                for (var i = first; i + 2 < first + count && i + 2 < indexCount; i += 3)
                    for (var k = 0; k < 3; k++)
                        triangles.Add(BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(indexData + (startIndex + i + k) * 2)));
            }
            if (subCount == 0) Add(0, indexCount);
            else
                for (var s = subIndex; s < subIndex + subCount && s < submeshCount; s++)
                {
                    var sp = submeshes + s * 16;
                    var subOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(sp));
                    var subIndices = (int)BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(sp + 4));
                    var mask = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(sp + 8));
                    if ((mask & ~attributes) != 0) continue;
                    Add(subOffset - startIndex, subIndices);
                }
            Meshes.Add(new Mesh(positions, normals, boneIndex, boneWeight, [.. triangles]));
        }
    }

    private Vector3 Vec3(int at, byte type)
    {
        var v = Vec4(at, type);
        return new Vector3(v.X, v.Y, v.Z);
    }

    /// <summary>顶点元素类型（位置、法线用）：2 / 3 = 3 / 4 个 float，5 = 4 个 byte，8 = 4 个 byte 归一化，13 / 14 = 2 / 4 个 half。</summary>
    private Vector4 Vec4(int at, byte type)
    {
        var s = _d.AsSpan(at);
        return type switch
        {
            2 => new Vector4(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]), 0),
            3 => new Vector4(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]),
                             BinaryPrimitives.ReadSingleLittleEndian(s[12..])),
            5 => new Vector4(s[0], s[1], s[2], s[3]),
            8 => new Vector4(s[0], s[1], s[2], s[3]) / 255f,
            13 => new Vector4((float)BinaryPrimitives.ReadHalfLittleEndian(s), (float)BinaryPrimitives.ReadHalfLittleEndian(s[2..]), 0, 0),
            14 => new Vector4((float)BinaryPrimitives.ReadHalfLittleEndian(s), (float)BinaryPrimitives.ReadHalfLittleEndian(s[2..]),
                              (float)BinaryPrimitives.ReadHalfLittleEndian(s[4..]), (float)BinaryPrimitives.ReadHalfLittleEndian(s[6..])),
            _ => Vector4.Zero,
        };
    }

    private ushort U16()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(_p));
        _p += 2;
        return v;
    }

    private uint U32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(_p));
        _p += 4;
        return v;
    }

    private string CString(int at)
    {
        if (at < 0 || at >= _d.Length) return "";
        var end = Array.IndexOf(_d, (byte)0, at);
        return Encoding.UTF8.GetString(_d, at, (end < 0 ? _d.Length : end) - at);
    }
}
