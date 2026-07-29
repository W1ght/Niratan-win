using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Niratan.Services.Manga;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaGoogleLensProtocolTests
{
    [Fact]
    public void DecodeResponse_VerticalParagraph_CreatesCharacterHitRegions()
    {
        var response = Message(2, Message(3, Message(1, Message(1,
            Line([("日", " "), ("本", "")], 0.80f)
            .Concat(Line([("語", "")], 0.60f))
            .ToArray()))));

        var regions = MangaGoogleLensProtocol.DecodeResponse(response, 7);

        regions.Should().HaveCount(3);
        regions.Should().OnlyContain(region =>
            region.Sentence == "日本語"
            && region.PageIndex == 7
            && region.IsVertical);
        regions.Select(region => region.Utf16Offset).Should().Equal(0, 1, 2);
        regions.Select(region => region.BlockId).Distinct().Should().ContainSingle();
        regions.Select(region => region.LineId).Distinct().Should().HaveCount(2);
        regions[0].Y.Should().BeLessThan(regions[1].Y);
        regions.Should().OnlyContain(region =>
            region.X >= 0 && region.Y >= 0
            && region.X + region.Width <= 1
            && region.Y + region.Height <= 1);
    }

    [Fact]
    public void DecodeResponse_HorizontalParagraph_OrdersLinesTopToBottom()
    {
        var response = Message(2, Message(3, Message(1, Message(1,
            Line(
                [("下", "")],
                centerX: 0.50f,
                centerY: 0.80f,
                rotation: 0)
            .Concat(Line(
                [("上", "")],
                centerX: 0.50f,
                centerY: 0.20f,
                rotation: 0))
            .ToArray()))));

        var regions = MangaGoogleLensProtocol.DecodeResponse(response, 0);

        regions.Select(region => region.Sentence).Should().OnlyContain(
            sentence => sentence == "上下");
        regions.Select(region => region.Utf16Offset).Should().Equal(0, 1);
        regions.Should().OnlyContain(region => !region.IsVertical);
        regions[0].Y.Should().BeLessThan(regions[1].Y);
    }

    [Fact]
    public void DecodeResponse_TallLineWithoutRotation_IsVerticalLikeNiratan()
    {
        var response = Message(2, Message(3, Message(1, Message(1,
            Line(
                [("一", "")],
                centerX: 0.50f,
                centerY: 0.50f,
                width: 0.04f,
                height: 0.12f,
                rotation: null)
            .ToArray()))));

        var region = MangaGoogleLensProtocol.DecodeResponse(response, 0)
            .Should()
            .ContainSingle()
            .Subject;

        region.IsVertical.Should().BeTrue();
    }

    [Fact]
    public void DecodeResponse_UsesWordGeometryForMorePreciseHitRegions()
    {
        var response = Message(2, Message(3, Message(1, Message(1,
            LineWithWordGeometry(
                [("日", 0.20f), ("本", 0.80f)],
                centerX: 0.50f,
                centerY: 0.50f,
                width: 0.90f,
                height: 0.10f,
                rotation: 0)
            .ToArray()))));

        var regions = MangaGoogleLensProtocol.DecodeResponse(response, 0);

        regions.Should().HaveCount(2);
        regions[0].X.Should().BeApproximately(0.15, 0.0001);
        regions[0].Width.Should().BeApproximately(0.10, 0.0001);
        regions[1].X.Should().BeApproximately(0.75, 0.0001);
        regions[1].Width.Should().BeApproximately(0.10, 0.0001);
    }

    [Fact]
    public void DecodeResponse_AdjacentVerticalParagraphs_FormOneReadingBlock()
    {
        var response = Message(2, Message(3, Message(1,
            Paragraph(Line(
                [("あんたの", "")],
                centerX: 0.84f,
                centerY: 0.36f,
                width: 0.08f,
                height: 0.02f))
            .Concat(Paragraph(Line(
                [("落とし物", "")],
                centerX: 0.81f,
                centerY: 0.36f,
                width: 0.08f,
                height: 0.02f)))
            .Concat(Paragraph(Line(
                [("じゃないの?", "")],
                centerX: 0.78f,
                centerY: 0.37f,
                width: 0.10f,
                height: 0.02f)))
            .ToArray())));

        var regions = MangaGoogleLensProtocol.DecodeResponse(response, 2);

        regions.Select(region => region.BlockId).Distinct().Should().ContainSingle();
        regions.Select(region => region.Sentence).Distinct().Should()
            .Equal("あんたの落とし物じゃないの?");
        regions.Select(region => region.Utf16Offset).Should()
            .Equal(Enumerable.Range(0, 14));
    }

    [Fact]
    public void DecodeResponse_SeparatedVerticalParagraphs_RemainDifferentBlocks()
    {
        var response = Message(2, Message(3, Message(1,
            Paragraph(Line(
                [("右", "")],
                centerX: 0.80f,
                centerY: 0.20f,
                width: 0.08f,
                height: 0.02f))
            .Concat(Paragraph(Line(
                [("左", "")],
                centerX: 0.30f,
                centerY: 0.20f,
                width: 0.08f,
                height: 0.02f)))
            .ToArray())));

        var regions = MangaGoogleLensProtocol.DecodeResponse(response, 0);

        regions.Select(region => region.BlockId).Distinct().Should().HaveCount(2);
        regions.Select(region => region.Sentence).Should().Equal("右", "左");
    }

    [Fact]
    public void MakeRequest_WrapsImageAndDimensions()
    {
        var request = MangaGoogleLensProtocol.MakeRequest(
            [0xde, 0xad, 0xbe, 0xef],
            1200,
            800);

        request.Should().HaveCountGreaterThan(4);
        request.Should().ContainInOrder(0xde, 0xad, 0xbe, 0xef);
    }

    [Fact]
    public void DecodeResponse_UsesWinUiTopLeadingCoordinates()
    {
        var response = Message(2, Message(3, Message(1, Message(1,
            Line(
                [("日", "")],
                centerX: 0.50f,
                centerY: 0.20f,
                width: 0.20f,
                height: 0.10f,
                rotation: 0)
            .ToArray()))));

        var region = MangaGoogleLensProtocol.DecodeResponse(response, 0)
            .Should()
            .ContainSingle()
            .Subject;

        region.X.Should().BeApproximately(0.40, 0.0001);
        region.Y.Should().BeApproximately(0.15, 0.0001);
        region.Width.Should().BeApproximately(0.20, 0.0001);
        region.Height.Should().BeApproximately(0.10, 0.0001);
    }

    private static byte[] Line(
        IReadOnlyList<(string Text, string Separator)> words,
        float centerX,
        float centerY = 0.50f,
        float width = 0.40f,
        float height = 0.10f,
        float? rotation = MathF.PI / 2)
    {
        var contents = words
            .Select(word => Message(
                1,
                String(2, word.Text).Concat(String(3, word.Separator)).ToArray()))
            .SelectMany(value => value)
            .Concat(Message(2, Geometry(
                centerX,
                centerY,
                width,
                height,
                rotation)))
            .ToArray();
        return Message(2, contents);
    }

    private static byte[] LineWithWordGeometry(
        IReadOnlyList<(string Text, float CenterX)> words,
        float centerX,
        float centerY,
        float width,
        float height,
        float rotation)
    {
        var contents = words
            .Select(word => Message(
                1,
                String(2, word.Text)
                    .Concat(Message(4, Geometry(
                        word.CenterX,
                        centerY,
                        0.10f,
                        height,
                        rotation)))
                    .ToArray()))
            .SelectMany(value => value)
            .Concat(Message(2, Geometry(
                centerX,
                centerY,
                width,
                height,
                rotation)))
            .ToArray();
        return Message(2, contents);
    }

    private static byte[] Paragraph(byte[] line) => Message(1, line);

    private static byte[] Geometry(
        float centerX,
        float centerY,
        float width,
        float height,
        float? rotation) =>
        Message(
            1,
            Float32(1, centerX)
                .Concat(Float32(2, centerY))
                .Concat(Float32(3, width))
                .Concat(Float32(4, height))
                .Concat(rotation is { } value ? Float32(5, value) : [])
                .ToArray());

    private static byte[] Message(int field, byte[] value) => Bytes(field, value);

    private static byte[] String(int field, string value) =>
        Bytes(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Bytes(int field, byte[] value) =>
        Varint((ulong)((field << 3) | 2))
            .Concat(Varint((ulong)value.Length))
            .Concat(value)
            .ToArray();

    private static byte[] Float32(int field, float value)
    {
        var result = Varint((ulong)((field << 3) | 5)).ToList();
        Span<byte> bits = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bits, value);
        result.AddRange(bits.ToArray());
        return result.ToArray();
    }

    private static byte[] Varint(ulong value)
    {
        var result = new List<byte>();
        while (value > 0x7f)
        {
            result.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        result.Add((byte)value);
        return result.ToArray();
    }
}
