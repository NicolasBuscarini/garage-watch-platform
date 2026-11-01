using System.Text;

namespace GarageWatch.Api.Tests;

/// <summary>
/// Minimal ONNX protobuf writer for deterministic fixtures. Constant nodes produce fixed tensors, so the
/// adapter wiring (sessions, formatting, decoding) is exercised without shipping real weights.
/// </summary>
internal static class OnnxFixture
{
    public static byte[] CreateModel((string Name, int[] Dimensions)[] inputs,
        params (string Name, int[] Dimensions, float[] Values)[] outputs)
    {
        var nodes = new List<byte[]>();
        foreach (var output in outputs)
        {
            var tensor = new List<byte>();
            foreach (var dimension in output.Dimensions) tensor.AddRange(VarintField(1, (ulong)dimension));
            tensor.AddRange(VarintField(2, 1));
            var raw = new byte[output.Values.Length * 4];
            for (var index = 0; index < output.Values.Length; index++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(index * 4, 4),
                    output.Values[index]);
            tensor.AddRange(BytesField(9, raw));

            var attribute = new List<byte>();
            attribute.AddRange(BytesField(1, "value"u8.ToArray()));
            attribute.AddRange(BytesField(5, tensor.ToArray()));
            attribute.AddRange(VarintField(20, 4));

            var node = new List<byte>();
            node.AddRange(BytesField(2, Encoding.UTF8.GetBytes(output.Name)));
            node.AddRange(BytesField(4, "Constant"u8.ToArray()));
            node.AddRange(BytesField(5, attribute.ToArray()));
            nodes.Add(node.ToArray());
        }

        var graph = new List<byte>();
        foreach (var node in nodes) graph.AddRange(BytesField(1, node));
        graph.AddRange(BytesField(2, "face_fixture"u8.ToArray()));
        foreach (var input in inputs) graph.AddRange(BytesField(11, ValueInfo(input.Name, input.Dimensions)));
        foreach (var output in outputs) graph.AddRange(BytesField(12, ValueInfo(output.Name, output.Dimensions)));

        var model = new List<byte>();
        model.AddRange(VarintField(1, 9));
        model.AddRange(BytesField(2, "garage-watch-tests"u8.ToArray()));
        model.AddRange(BytesField(7, graph.ToArray()));
        model.AddRange(BytesField(8, VarintField(2, 13)));
        return model.ToArray();
    }

    private static byte[] ValueInfo(string name, int[] dimensions)
    {
        var shape = new List<byte>();
        foreach (var dimension in dimensions) shape.AddRange(BytesField(1, VarintField(1, (ulong)dimension)));
        var tensorType = new List<byte>();
        tensorType.AddRange(VarintField(1, 1));
        tensorType.AddRange(BytesField(2, shape.ToArray()));
        var info = new List<byte>();
        info.AddRange(BytesField(1, Encoding.UTF8.GetBytes(name)));
        info.AddRange(BytesField(2, BytesField(1, tensorType.ToArray())));
        return info.ToArray();
    }

    private static byte[] VarintField(int field, ulong value)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (ulong)(field << 3));
        WriteVarint(bytes, value);
        return bytes.ToArray();
    }

    private static byte[] BytesField(int field, byte[] value)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (ulong)(field << 3 | 2));
        WriteVarint(bytes, (ulong)value.Length);
        bytes.AddRange(value);
        return bytes.ToArray();
    }

    private static void WriteVarint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }
}
