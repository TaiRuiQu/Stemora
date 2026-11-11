using System.Text;
using System.Text.Json;

namespace Stemora.Core;

public static class GgufInspector
{
    public static ModelDescriptor Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is < 2 or > 3)
            throw new InvalidDataException("不是支持的 GGUF 模型。");
        _ = reader.ReadUInt64();
        var count = reader.ReadUInt64();
        if (count > 100_000) throw new InvalidDataException("GGUF 元数据数量异常。");
        var values = new Dictionary<string, object?>();
        for (ulong i = 0; i < count; i++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            values.Add(key, ReadValue(reader, type));
        }
        string Text(string key, string fallback = "") => values.GetValueOrDefault(key) as string ?? fallback;
        int Number(string key, int fallback) => values.GetValueOrDefault(key) is { } value
            ? checked(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)) : fallback;
        var architecture = Text("general.architecture") switch { "bs" => "bs_roformer", "mel_band" => "mel_band_roformer", var name => name };
        if (string.IsNullOrWhiteSpace(architecture)) throw new InvalidDataException("模型缺少 general.architecture。");
        var prefix = architecture + ".";
        var stemCount = Number(prefix + "num_stems", 1);
        if (stemCount is < 1 or > 64) throw new InvalidDataException("模型输出音轨数无效。");
        var names = new[] { "stemora.stems", prefix + "stems", prefix + "instruments", "training.instruments" }
            .Select(key => values.GetValueOrDefault(key)).OfType<string[]>().FirstOrDefault()
            ?? Enumerable.Range(0, stemCount).Select(i => stemCount == 1 ? "primary" : $"stem_{i + 1}").ToArray();
        ValidateStems(names, stemCount);
        var result = new ModelDescriptor(architecture, architecture, Text("general.name", Path.GetFileNameWithoutExtension(path)),
            Number(prefix + "sample_rate", 44100), names.Select(id => new StemDefinition(id, StemLabel(id))).ToArray(),
            Number(prefix + "default_chunk_size", 352800), Number(prefix + "default_num_overlap", 2));
        var sidecar = path + ".stemora.json";
        if (File.Exists(sidecar))
        {
            if (new FileInfo(sidecar).Length > 65536) throw new InvalidDataException("模型描述文件过大。");
            using var json = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = json.RootElement;
            var family = root.TryGetProperty("family", out var familyValue) ? familyValue.GetString() ?? architecture : architecture;
            if (root.TryGetProperty("stems", out var stems))
            {
                var ids = stems.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                ValidateStems(ids, stemCount);
                result = result with { Family = family, Stems = ids.Select(id => new StemDefinition(id, StemLabel(id))).ToArray() };
            }
        }
        return result;
    }
    public static string StemLabel(string id) => id switch
    {
        "bass" => "贝斯",
        "drums" => "鼓",
        "other" => "其他",
        "vocals" => "人声",
        "guitar" => "吉他",
        "piano" => "钢琴",
        "primary" => "主音轨",
        "residual" => "残差伴奏",
        "instrumental" => "伴奏",
        "original" => "原始混音",
        _ => id
    };
    public static string FamilyLabel(string id) => id switch
    { "bs_roformer_sw" => "BS Roformer SW", "bs_roformer" => "BS Roformer", "mel_band_roformer" => "Mel-Band Roformer", _ => id };
    public static void ValidateStems(IReadOnlyList<string> names, int expected)
    {
        if (names.Count != expected || names.Distinct(StringComparer.Ordinal).Count() != expected || names.Any(id =>
            id.Length is < 1 or > 64 || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))))
            throw new InvalidDataException("音轨名称必须唯一、与模型输出数一致，并使用小写英文、数字、下划线或连字符。");
    }
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 8_388_608 || length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position)) throw new InvalidDataException("GGUF 字符串长度异常。");
        var bytes = reader.ReadBytes((int)length);
        if ((ulong)bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
    private static object? ReadValue(BinaryReader reader, uint type) => type switch
    {
        0 => reader.ReadByte(),
        1 => reader.ReadSByte(),
        2 => reader.ReadUInt16(),
        3 => reader.ReadInt16(),
        4 => reader.ReadUInt32(),
        5 => reader.ReadInt32(),
        6 => reader.ReadSingle(),
        7 => reader.ReadBoolean(),
        8 => ReadString(reader),
        9 => ReadArray(reader),
        10 => reader.ReadUInt64(),
        11 => reader.ReadInt64(),
        12 => reader.ReadDouble(),
        _ => throw new InvalidDataException("未知 GGUF 元数据类型。")
    };
    private static object? ReadArray(BinaryReader reader)
    {
        var type = reader.ReadUInt32(); var count = reader.ReadUInt64();
        if (count > 16_777_216 || type == 9) throw new InvalidDataException("GGUF 数组长度或类型异常。");
        if (type == 8)
        {
            if (count > 65536) throw new InvalidDataException("GGUF 字符串数组过大。");
            return Enumerable.Range(0, (int)count).Select(_ => ReadString(reader)).ToArray();
        }
        var size = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("未知 GGUF 数组类型。") };
        var bytes = checked((long)count * size);
        if (bytes > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
        reader.BaseStream.Seek(bytes, SeekOrigin.Current);
        return null;
    }
}
