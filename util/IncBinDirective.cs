using System.Globalization;

namespace Diz.LogWriter.util;

// An incbin directive parsed from a data byte's comment (or its label's comment):
//   !!incbin:N <operand>   -> replace the next N ROM bytes with a single line:  incbin <operand>
//
// N (the byte count this line collapses/skips) is decimal or $-prefixed hex, so it can be
// written the same way asset sizes appear elsewhere (e.g. !!incbin:$34 or !!incbin:52).
// <operand> is emitted VERBATIM after "incbin " — the user supplies the quotes and the path,
// and is responsible for putting the referenced file where asar can find it. Diz never reads,
// writes, or edits that file; from its perspective the operand is an opaque string.
//
// Mirrors DataByteDirective's conventions: the directive must be at the very start of the
// comment, and a trailing "; human note" is ignored. Because it is read via GetCommentText
// (byte comment first, else label comment) it can sit on a data table's labeled anchor byte.
public readonly struct IncBinDirective
{
    public int ByteCount { get; private init; }   // number of ROM bytes this line collapses
    public string Operand { get; private init; }  // verbatim text emitted after "incbin "

    private const string Prefix = "!!incbin:";

    public static IncBinDirective? TryParse(string comment)
    {
        if (string.IsNullOrEmpty(comment))
            return null;

        // a trailing "; human note" after the directive is not part of the directive
        var text = comment;
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0)
            text = text[..semicolon];
        text = text.Trim();

        if (!text.StartsWith(Prefix))
            return null;

        // "<count> <operand>": the count token runs up to the first space, the rest is the operand
        var rest = text[Prefix.Length..];
        var space = rest.IndexOf(' ');
        if (space <= 0)
            return null; // need both a count and a following operand

        if (!TryParseCount(rest[..space], out var byteCount) || byteCount <= 0)
            return null;

        var operand = rest[(space + 1)..].Trim();
        if (operand.Length == 0)
            return null;

        return new IncBinDirective { ByteCount = byteCount, Operand = operand };
    }

    private static bool TryParseCount(string token, out int value)
    {
        token = token.Trim();
        if (token.StartsWith('$'))
            return int.TryParse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
