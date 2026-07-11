using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.util;

// Builds the bins.json manifest: one entry per !!incbin directive in the ROM. Kept as a pure
// helper (no output-stream plumbing) so it can be unit-tested directly. AsmStepWriteIncBinList is
// the thin wrapper that writes ToJson(Collect(...)) to the bins.json stream during export.
public static class IncBinManifest
{
    public readonly record struct Entry(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("start")] int Start,
        [property: JsonPropertyName("length")] int Length,
        [property: JsonPropertyName("comment")] string Comment);

    public static List<Entry> Collect(ILogCreatorDataSource<IData> data, int romSize)
    {
        var entries = new List<Entry>();

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
            entries.Add(new Entry(file, snes, incbin.Value.ByteCount, incbin.Value.Note));

            // skip past the collapsed span so interior bytes aren't re-examined
            offset += incbin.Value.ByteCount;
        }

        return entries;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ToJson(IReadOnlyList<Entry> entries) =>
        JsonSerializer.Serialize(entries, JsonOptions);
}
