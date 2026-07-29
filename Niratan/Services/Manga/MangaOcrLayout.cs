using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal static class MangaOcrLayout
{
    private const int MaximumMergeCandidateBlocks = 2048;
    private const double MinimumFlowOverlapRatio = 0.5;
    private const double MaximumLaneGapRatio = 1.25;

    public static IReadOnlyList<MangaTextRegion> MergeAdjacentTextBlocks(
        IReadOnlyList<MangaTextRegion> regions)
    {
        if (regions.Count == 0)
            return regions;

        var blocks = regions
            .Select((region, index) => (region, index))
            .GroupBy(item => item.region.BlockId)
            .Select(group => TextBlock.Create(group.Key, group.ToList()))
            .ToList();
        if (blocks.Count < 2 || blocks.Count > MaximumMergeCandidateBlocks)
            return regions;

        var components = BuildComponents(blocks);
        if (components.All(component => component.Count == 1))
            return regions;

        var replacements = new Dictionary<string, RegionReplacement>();
        foreach (var component in components.Where(component => component.Count > 1))
        {
            var ordered = OrderForReading(component);
            var mergedBlockId = ordered[0].BlockId;
            var sentence = string.Concat(ordered.Select(block => block.Sentence));
            var baseOffset = 0;
            foreach (var block in ordered)
            {
                replacements[block.BlockId] = new RegionReplacement(
                    mergedBlockId,
                    sentence,
                    baseOffset);
                baseOffset += block.Sentence.Length;
            }
        }

        return regions
            .Select(region =>
            {
                if (!replacements.TryGetValue(region.BlockId, out var replacement))
                    return region;
                return region with
                {
                    BlockId = replacement.BlockId,
                    Sentence = replacement.Sentence,
                    Utf16Offset = replacement.BaseOffset + region.Utf16Offset,
                };
            })
            .ToList();
    }

    private static List<List<TextBlock>> BuildComponents(
        IReadOnlyList<TextBlock> blocks)
    {
        var components = new List<List<TextBlock>>();
        var visited = new bool[blocks.Count];
        for (var start = 0; start < blocks.Count; start++)
        {
            if (visited[start])
                continue;

            var component = new List<TextBlock>();
            var pending = new Stack<int>();
            pending.Push(start);
            visited[start] = true;
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                component.Add(blocks[current]);
                for (var candidate = 0; candidate < blocks.Count; candidate++)
                {
                    if (visited[candidate]
                        || !ShouldMerge(blocks[current], blocks[candidate]))
                    {
                        continue;
                    }

                    visited[candidate] = true;
                    pending.Push(candidate);
                }
            }
            components.Add(component);
        }
        return components;
    }

    private static bool ShouldMerge(TextBlock left, TextBlock right)
    {
        if (left.IsVertical != right.IsVertical)
            return false;

        if (left.IsVertical)
        {
            var overlap = Overlap(left.Top, left.Bottom, right.Top, right.Bottom);
            var minimumLength = Math.Min(left.Height, right.Height);
            var gap = Gap(left.Left, left.Right, right.Left, right.Right);
            var laneSize = Math.Max(left.Width, right.Width);
            return overlap >= minimumLength * MinimumFlowOverlapRatio
                && gap <= laneSize * MaximumLaneGapRatio;
        }

        var horizontalOverlap = Overlap(left.Left, left.Right, right.Left, right.Right);
        var minimumWidth = Math.Min(left.Width, right.Width);
        var verticalGap = Gap(left.Top, left.Bottom, right.Top, right.Bottom);
        var rowSize = Math.Max(left.Height, right.Height);
        return horizontalOverlap >= minimumWidth * MinimumFlowOverlapRatio
            && verticalGap <= rowSize * MaximumLaneGapRatio;
    }

    private static IReadOnlyList<TextBlock> OrderForReading(
        IReadOnlyList<TextBlock> blocks) =>
        blocks[0].IsVertical
            ? blocks
                .OrderByDescending(block => block.CenterX)
                .ThenBy(block => block.Top)
                .ThenBy(block => block.SourceIndex)
                .ToList()
            : blocks
                .OrderBy(block => block.CenterY)
                .ThenBy(block => block.Left)
                .ThenBy(block => block.SourceIndex)
                .ToList();

    private static double Overlap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd) =>
        Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));

    private static double Gap(
        double firstStart,
        double firstEnd,
        double secondStart,
        double secondEnd) =>
        Math.Max(0, Math.Max(firstStart, secondStart) - Math.Min(firstEnd, secondEnd));

    private sealed record RegionReplacement(
        string BlockId,
        string Sentence,
        int BaseOffset);

    private sealed record TextBlock(
        string BlockId,
        string Sentence,
        bool IsVertical,
        int SourceIndex,
        double Left,
        double Top,
        double Right,
        double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
        public double CenterX => Left + Width / 2;
        public double CenterY => Top + Height / 2;

        public static TextBlock Create(
            string blockId,
            IReadOnlyList<(MangaTextRegion region, int index)> items)
        {
            var first = items.MinBy(item => item.index);
            return new TextBlock(
                blockId,
                first.region.Sentence,
                first.region.IsVertical,
                first.index,
                items.Min(item => item.region.X),
                items.Min(item => item.region.Y),
                items.Max(item => item.region.X + item.region.Width),
                items.Max(item => item.region.Y + item.region.Height));
        }
    }
}
