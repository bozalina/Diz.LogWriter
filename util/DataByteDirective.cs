namespace Diz.LogWriter.util;

// A data-byte symbolic-override directive parsed from a byte's comment:
//   !!db <expr>   -> render <expr> in place of a 1-byte hex value
//   !!dw <expr>   -> render <expr> in place of a 2-byte (little-endian) value
//   !!dl <expr>   -> render <expr> in place of a 3-byte (little-endian) value
// The width is carried by the directive because a data value (unlike an operand,
// whose addressing mode fixes its size) has nothing to infer its span from.
//
// This mirrors the conventions of Diz.Cpu._65816's CpuUtils.ParseCommentSpecialDirective
// (directive must be at the very start of the comment; a trailing "; human note" is
// ignored) but lives in Diz.LogWriter so the data renderer is self-contained.
public readonly struct DataByteDirective
{
    public int WidthBytes { get; private init; }  // 1 (!!db), 2 (!!dw), 3 (!!dl)
    public string Expr { get; private init; }      // verbatim override text, emitted as-is

    public static DataByteDirective? TryParse(string comment)
    {
        if (string.IsNullOrEmpty(comment))
            return null;

        // a trailing "; human note" after the directive is not part of the expression
        var text = comment;
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0)
            text = text[..semicolon];
        text = text.Trim();

        // must be exactly one of !!db / !!dw / !!dl followed by a space and the expression
        if (!text.StartsWith("!!d") || text.Length < 5)
            return null;

        var width = text[3] switch
        {
            'b' => 1,
            'w' => 2,
            'l' => 3,
            _ => 0,
        };
        if (width == 0 || text[4] != ' ')
            return null;

        var expr = text[5..].Trim();
        if (expr.Length == 0)
            return null;

        return new DataByteDirective { WidthBytes = width, Expr = expr };
    }
}
