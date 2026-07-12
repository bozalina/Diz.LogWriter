using System;
using System.Collections.Generic;
using Diz.Core.Interfaces;
using Diz.Core.util;

namespace Diz.LogWriter.util;

// One emitted `incbin` line for an !!incbin asset. A non-straddling asset is a single WHOLE segment
// rendered as `incbin "<file>"` (byte-identical to the pre-segment behavior). An asset whose byte
// span crosses one or more bank boundaries is emitted as the WHOLE combined file referenced by one
// asar range-incbin per bank segment, split exactly at each boundary — this keeps each bank file at
// its natural org and collapses the asset to a single .bin (no `_N_of_M` part files).
//
// asar range semantics: `incbin "file":start..end` copies bytes inclusive on BOTH ends, and an end
// of 0 means "to end of file". So a segment covering file bytes [FileStart, FileStart+Length) renders
// its end as FileStart+Length-1 (inclusive) — except the final segment, which uses 0 so it always
// reaches the true end of file regardless of the byte count.
public readonly record struct IncBinSegment(string File, int FileStart, int Length, bool IsFinal, bool IsWhole)
{
    public string ToIncbinLine()
    {
        var quoted = $"incbin \"{File}\"";
        if (IsWhole)
            return quoted;

        var start = "$" + Util.NumberToBaseString(FileStart, Util.NumberBase.Hexadecimal, 0);
        var end = IsFinal
            ? "0"   // asar: end 0 == to end of file
            : "$" + Util.NumberToBaseString(FileStart + Length - 1, Util.NumberBase.Hexadecimal, 0); // inclusive end
        return $"{quoted}:{start}..{end}";
    }
}

// Precomputes, for one export, the map from ROM offset -> the incbin segment that STARTS at that
// offset. Both LogCreator.GetLineByteLength (to advance the emit loop one segment at a time) and
// AssemblyGenerateCode (to render each segment's line) consult this map, so a segment's byte length
// and its emitted line can never disagree.
public static class IncBinSegmentMap
{
    // Split one anchor's [anchorOffset, anchorOffset+byteCount) span into per-bank segments, yielding
    // (segmentStartOffset, segment). Bank boundaries are the PC-offset multiples of bankSize (the same
    // space the emit loop and GetLineByteLength use to detect bank crossings). A span that stays inside
    // one bank yields a single WHOLE segment.
    public static IEnumerable<(int Offset, IncBinSegment Segment)> ForAnchor(
        string file, int anchorOffset, int byteCount, int bankSize)
    {
        var assetEnd = anchorOffset + byteCount;
        var fileStart = 0;
        for (var segStart = anchorOffset; segStart < assetEnd;)
        {
            var nextBankBoundary = (segStart / bankSize + 1) * bankSize;
            var segEnd = Math.Min(nextBankBoundary, assetEnd);
            var length = segEnd - segStart;
            var isFinal = segEnd == assetEnd;
            var isWhole = segStart == anchorOffset && isFinal;

            yield return (segStart, new IncBinSegment(file, fileStart, length, isFinal, isWhole));

            fileStart += length;
            segStart = segEnd;
        }
    }

    // Walk the ROM (same scan as IncBinManifest.Collect) and build the offset -> segment map for every
    // !!incbin anchor. The filename derives from the anchor byte's label so it matches bins.json.
    public static Dictionary<int, IncBinSegment> Build(ILogCreatorDataSource<IData> data, int romSize)
    {
        var map = new Dictionary<int, IncBinSegment>();
        var bankSize = data.GetBankSize();

        for (var offset = 0; offset < romSize;)
        {
            var snes = data.ConvertPCtoSnes(offset);
            if (snes == -1)
            {
                offset++;
                continue;
            }

            var incbin = IncBinDirective.TryParse(data.GetCommentText(snes));
            if (incbin == null)
            {
                offset++;
                continue;
            }

            var file = IncBinDirective.DeriveBinFilename(data.Labels.GetLabelName(snes), snes);
            foreach (var (segOffset, segment) in ForAnchor(file, offset, incbin.Value.ByteCount, bankSize))
                map[segOffset] = segment;

            // skip past the collapsed span so interior bytes aren't re-examined
            offset += incbin.Value.ByteCount;
        }

        return map;
    }
}
