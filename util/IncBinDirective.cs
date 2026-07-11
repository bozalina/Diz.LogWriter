using System.Globalization;

namespace Diz.LogWriter.util;

// An incbin directive parsed from a data byte's comment (or its label's comment):
//   !!incbin:N            -> collapse the next N ROM bytes into one line:  incbin "<label>.bin"
//   !!incbin:N ; <note>   -> same, and capture <note> for the bins.json manifest
//
// N (the byte count this line collapses/skips) is decimal or $-prefixed hex. The emitted
// filename is derived from the anchor byte's label via DeriveBinFilename (the operand the old
// format required has been removed). Any non-whitespace text between N and the first ';' (e.g. a
// stale operand from the old format) is ignored, so old comments still parse.
//
// The directive must be at the very start of the comment. <note> is the text after the first ';'.
public readonly struct IncBinDirective
{
    public int ByteCount { get; private init; }   // number of ROM bytes this line collapses
    public string Note { get; private init; }      // text after the first ';' (the manifest comment); "" if none

    private const string Prefix = "!!incbin:";

    public static IncBinDirective? TryParse(string comment)
    {
        if (string.IsNullOrEmpty(comment))
            return null;

        if (!comment.StartsWith(Prefix))
            return null;

        // the note is everything after the first ';'
        var note = "";
        var body = comment;
        var semicolon = comment.IndexOf(';');
        if (semicolon >= 0)
        {
            note = comment[(semicolon + 1)..].Trim();
            body = comment[..semicolon];
        }

        // body is "!!incbin:<count>[ <ignored stale operand>]"; the count token runs up to the
        // first space, and anything after it (before the ';') is ignored.
        var rest = body[Prefix.Length..].Trim();
        var space = rest.IndexOf(' ');
        var countToken = space < 0 ? rest : rest[..space];

        if (!TryParseCount(countToken, out var byteCount) || byteCount <= 0)
            return null;

        return new IncBinDirective { ByteCount = byteCount, Note = note };
    }

    // The filename emitted into the incbin line AND recorded in bins.json. Both call this so they
    // can never disagree. A missing label falls back to a per-address name so multiple unlabeled
    // assets don't collide on a single "UNKNOWN.bin".
    public static string DeriveBinFilename(string labelName, int snesAddress) =>
        string.IsNullOrEmpty(labelName)
            ? $"UNKNOWN_{snesAddress:X6}.bin"
            : $"{labelName}.bin";

    private static bool TryParseCount(string token, out int value)
    {
        token = token.Trim();
        if (token.StartsWith('$'))
            return int.TryParse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
