using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal static class MangaGoogleLensProtocol
{
    public static byte[] MakeRequest(
        ReadOnlySpan<byte> imageData,
        int width,
        int height,
        string language = "ja")
    {
        var imageBytes = imageData.ToArray();
        var root = new ProtobufWriter();
        root.Message(1, objects =>
        {
            objects.Message(1, context =>
            {
                context.Message(3, requestId =>
                {
                    requestId.UInt(1, (ulong)Random.Shared.NextInt64(1, long.MaxValue));
                    requestId.UInt(2, 1);
                    requestId.UInt(3, 1);
                });
                context.Message(4, client =>
                {
                    client.UInt(1, 3);
                    client.UInt(2, 4);
                    client.Message(4, locale =>
                    {
                        locale.String(1, language);
                        locale.String(2, "US");
                        locale.String(3, "America/New_York");
                    });
                });
            });
            objects.Message(3, image =>
            {
                image.Message(1, payload => payload.Bytes(1, imageBytes));
                image.Message(3, metadata =>
                {
                    metadata.UInt(1, (ulong)width);
                    metadata.UInt(2, (ulong)height);
                });
            });
        });
        return root.ToArray();
    }

    public static IReadOnlyList<MangaTextRegion> DecodeResponse(
        ReadOnlySpan<byte> data,
        int pageIndex,
        string language = "ja")
    {
        var root = new ProtobufMessage(data.ToArray());
        var paragraphs = root.FirstMessage(2)?
            .FirstMessage(3)?
            .FirstMessage(1)?
            .Messages(1) ?? [];
        var regions = new List<MangaTextRegion>();

        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Count; paragraphIndex++)
        {
            var paragraph = paragraphs[paragraphIndex];
            var lines = new List<RecognizedLine>();
            var sourceLines = paragraph.Messages(2);
            for (var lineIndex = 0; lineIndex < sourceLines.Count; lineIndex++)
            {
                var line = sourceLines[lineIndex];
                var words = line.Messages(1)
                    .Select(word =>
                    {
                        var text = Normalize(
                            word.String(2) + word.String(3),
                            language);
                        var geometry = word.FirstMessage(4) is { } wordGeometry
                            ? ReadGeometry(wordGeometry)
                            : null;
                        return new RecognizedWord(text, geometry);
                    })
                    .Where(word => word.Text.Length > 0)
                    .ToList();
                var rawText = string.Concat(line.Messages(1).Select(
                    word => word.String(2) + word.String(3)));
                var text = Normalize(rawText, language);
                var geometry = line.FirstMessage(2) is { } geometryMessage
                    ? ReadGeometry(geometryMessage)
                    : null;
                if (text.Length > 0 && geometry is not null)
                {
                    lines.Add(new RecognizedLine(
                        lineIndex,
                        text,
                        geometry.Value,
                        string.Concat(words.Select(word => word.Text)) == text
                            ? words
                            : []));
                }
            }

            if (lines.Count == 0)
                continue;

            var paragraphGeometry = paragraph.FirstMessage(3) is { } paragraphMessage
                ? ReadGeometry(paragraphMessage)
                : null;
            var isVertical = IsVerticalParagraph(paragraphGeometry, lines);
            lines.Sort((left, right) =>
            {
                if (isVertical)
                {
                    var horizontal = Math.Abs(left.Geometry.CenterX - right.Geometry.CenterX);
                    return horizontal > 0.002
                        ? right.Geometry.CenterX.CompareTo(left.Geometry.CenterX)
                        : left.Geometry.Y.CompareTo(right.Geometry.Y);
                }

                var vertical = Math.Abs(left.Geometry.CenterY - right.Geometry.CenterY);
                return vertical > 0.002
                    ? left.Geometry.CenterY.CompareTo(right.Geometry.CenterY)
                    : left.Geometry.X.CompareTo(right.Geometry.X);
            });

            var sentence = string.Concat(lines.Select(line => line.Text));
            var blockId = $"lens-{pageIndex}-{paragraphIndex}";
            var utf16BaseOffset = 0;
            foreach (var line in lines)
            {
                var lineId = $"{blockId}-{line.SourceIndex}";
                regions.AddRange(MakeCharacterRegions(
                    line,
                    sentence,
                    utf16BaseOffset,
                    isVertical,
                    pageIndex,
                    blockId,
                    lineId));
                utf16BaseOffset += line.Text.Length;
            }
        }

        return MangaOcrLayout.MergeAdjacentTextBlocks(regions);
    }

    private static bool IsVerticalParagraph(
        Geometry? paragraphGeometry,
        IReadOnlyList<RecognizedLine> lines) =>
        paragraphGeometry?.IsVertical == true
        || lines.Count(line => line.Geometry.IsVertical) * 2 > lines.Count;

    private static string Normalize(string text, string language)
    {
        var trimmed = text.Trim();
        return language is "ja" or "zh"
            ? string.Concat(trimmed.Where(character => !char.IsWhiteSpace(character)))
            : string.Join(" ", trimmed.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static Geometry? ReadGeometry(ProtobufMessage message)
    {
        var box = message.FirstMessage(1);
        var centerX = box?.Float32(1);
        var centerY = box?.Float32(2);
        var width = box?.Float32(3);
        var height = box?.Float32(4);
        if (centerX is null || centerY is null || width is null || height is null
            || width <= 0 || height <= 0)
        {
            return null;
        }

        var rotation = box!.Float32(5);
        var cosine = Math.Abs(Math.Cos(rotation ?? 0));
        var sine = Math.Abs(Math.Sin(rotation ?? 0));
        var halfWidth = (width.Value * cosine + height.Value * sine) / 2;
        var halfHeight = (width.Value * sine + height.Value * cosine) / 2;
        var left = Math.Max(0, centerX.Value - halfWidth);
        var top = Math.Max(0, centerY.Value - halfHeight);
        var right = Math.Min(1, centerX.Value + halfWidth);
        var bottom = Math.Min(1, centerY.Value + halfHeight);
        if (right <= left || bottom <= top)
            return null;
        // Lens geometry is top-leading. Niratan's AppKit renderer converts it
        // to bottom-leading coordinates, but WinUI Canvas also uses a
        // top-leading origin, so retaining top here avoids a vertical mirror.
        return new Geometry(
            left,
            top,
            right - left,
            bottom - top,
            rotation);
    }

    private static IEnumerable<MangaTextRegion> MakeCharacterRegions(
        RecognizedLine line,
        string sentence,
        int utf16BaseOffset,
        bool isVertical,
        int pageIndex,
        string blockId,
        string lineId)
    {
        if (line.Words.Count > 0
            && line.Words.All(word => word.Geometry is not null))
        {
            var wordOffset = 0;
            foreach (var word in line.Words)
            {
                foreach (var region in MakeTextElementRegions(
                             word.Text,
                             sentence,
                             utf16BaseOffset + wordOffset,
                             word.Geometry!.Value,
                             isVertical,
                             pageIndex,
                             blockId,
                             lineId))
                {
                    yield return region;
                }
                wordOffset += word.Text.Length;
            }
            yield break;
        }

        foreach (var region in MakeTextElementRegions(
                     line.Text,
                     sentence,
                     utf16BaseOffset,
                     line.Geometry,
                     isVertical,
                     pageIndex,
                     blockId,
                     lineId))
        {
            yield return region;
        }
    }

    private static IEnumerable<MangaTextRegion> MakeTextElementRegions(
        string text,
        string sentence,
        int utf16BaseOffset,
        Geometry geometry,
        bool isVertical,
        int pageIndex,
        string blockId,
        string lineId)
    {
        var elements = new List<(int Offset, string Text)>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element))
                elements.Add((utf16BaseOffset + enumerator.ElementIndex, element));
        }

        if (elements.Count == 0)
            yield break;
        for (var index = 0; index < elements.Count; index++)
        {
            double x;
            double y;
            double width;
            double height;
            if (isVertical)
            {
                height = geometry.Height / elements.Count;
                x = geometry.X;
                y = geometry.Y + index * height;
                width = geometry.Width;
            }
            else
            {
                width = geometry.Width / elements.Count;
                x = geometry.X + index * width;
                y = geometry.Y;
                height = geometry.Height;
            }

            yield return new MangaTextRegion(
                $"{lineId}-{elements[index].Offset}",
                pageIndex,
                blockId,
                lineId,
                sentence,
                elements[index].Offset,
                isVertical,
                x,
                y,
                width,
                height);
        }
    }

    private readonly record struct Geometry(
        double X,
        double Y,
        double Width,
        double Height,
        double? Rotation)
    {
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;
        public bool IsVertical =>
            (Rotation is { } rotation
             && Math.Abs(Math.Abs(rotation) - Math.PI / 2) < 0.5)
            || Height > Width * 1.25;
    }

    private sealed record RecognizedWord(string Text, Geometry? Geometry);

    private sealed record RecognizedLine(
        int SourceIndex,
        string Text,
        Geometry Geometry,
        IReadOnlyList<RecognizedWord> Words);

    private sealed class ProtobufWriter
    {
        private readonly MemoryStream _stream = new();

        public void UInt(int field, ulong value)
        {
            WriteVarint((ulong)(field << 3));
            WriteVarint(value);
        }

        public void String(int field, string value) =>
            Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Bytes(int field, ReadOnlySpan<byte> value)
        {
            WriteVarint((ulong)((field << 3) | 2));
            WriteVarint((ulong)value.Length);
            _stream.Write(value);
        }

        public void Message(int field, Action<ProtobufWriter> build)
        {
            var nested = new ProtobufWriter();
            build(nested);
            Bytes(field, nested.ToArray());
        }

        public byte[] ToArray() => _stream.ToArray();

        private void WriteVarint(ulong value)
        {
            while (value > 0x7f)
            {
                _stream.WriteByte((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }
            _stream.WriteByte((byte)value);
        }
    }

    private sealed class ProtobufMessage
    {
        private readonly Dictionary<int, List<Field>> _fields = [];

        public ProtobufMessage(byte[] data)
        {
            var cursor = new ProtobufCursor(data);
            while (!cursor.IsAtEnd)
            {
                var tag = cursor.Varint();
                var field = checked((int)(tag >> 3));
                var wireType = tag & 7;
                if (field <= 0)
                    throw new InvalidDataException("Invalid Google Lens response.");
                byte[] value;
                switch (wireType)
                {
                    case 0:
                        cursor.Varint();
                        value = [];
                        break;
                    case 1:
                        value = cursor.Read(8);
                        break;
                    case 2:
                        value = cursor.Read(checked((int)cursor.Varint()));
                        break;
                    case 5:
                        value = cursor.Read(4);
                        break;
                    default:
                        throw new InvalidDataException("Invalid Google Lens wire type.");
                }
                if (!_fields.TryGetValue(field, out var values))
                    _fields[field] = values = [];
                values.Add(new Field(wireType, value));
            }
        }

        public IReadOnlyList<ProtobufMessage> Messages(int field) =>
            _fields.GetValueOrDefault(field)?
                .Where(value => value.WireType == 2)
                .Select(value => new ProtobufMessage(value.Value))
                .ToList() ?? [];

        public ProtobufMessage? FirstMessage(int field) => Messages(field).FirstOrDefault();

        public string String(int field)
        {
            var value = _fields.GetValueOrDefault(field)?
                .FirstOrDefault(candidate => candidate.WireType == 2)?.Value;
            return value is null ? string.Empty : Encoding.UTF8.GetString(value);
        }

        public float? Float32(int field)
        {
            var value = _fields.GetValueOrDefault(field)?
                .FirstOrDefault(candidate => candidate.WireType == 5)?.Value;
            return value is { Length: 4 }
                ? BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(value))
                : null;
        }

        private sealed record Field(ulong WireType, byte[] Value);
    }

    private sealed class ProtobufCursor(byte[] data)
    {
        private int _offset;
        public bool IsAtEnd => _offset >= data.Length;

        public ulong Varint()
        {
            ulong result = 0;
            var shift = 0;
            while (_offset < data.Length && shift < 70)
            {
                var value = data[_offset++];
                result |= (ulong)(value & 0x7f) << shift;
                if ((value & 0x80) == 0)
                    return result;
                shift += 7;
            }
            throw new InvalidDataException("Invalid Google Lens varint.");
        }

        public byte[] Read(int count)
        {
            if (count < 0 || _offset > data.Length - count)
                throw new InvalidDataException("Truncated Google Lens response.");
            var result = data.AsSpan(_offset, count).ToArray();
            _offset += count;
            return result;
        }
    }
}
