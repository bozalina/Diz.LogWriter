using System.Collections.Generic;
using System.Linq;
using System.Text;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.util;

namespace Diz.LogWriter;

public static class LogCreatorExtensions
{
    public static string CreateAssemblyFormattedTextLine(this ILogCreatorDataSource<IData> data, int offset, int count)
    {
        var rawStr = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            rawStr.Append((char)(data.GetRomByte(offset + i) ?? 0));
        }

        return CreateAssemblyFormattedTextLine(rawStr.ToString());
    }
        
    public static string CreateAssemblyFormattedTextLine(string rawStr)
    {
        // important: Asar will not accept null characters printed inside quoted text. so we need to break up text lines.
        // also, asar seems to have issues with exclamation points in text
        bool IsPrintableAsciiCharacter(char c) => 
            c >= 32 && c <= 127 && c != '"' && c != '!';

        var outputStr = new StringBuilder("db ");
        var inQuotedSection = false;

        bool StartQuotedSectionIfNeeded(bool printedSomethingBeforeThis)
        {
            if (inQuotedSection)
                return false;

            if (printedSomethingBeforeThis)
                outputStr.Append(", ");
                
            outputStr.Append('"');
            inQuotedSection = true;

            return true;
        }

        bool EndQuotedSectionIfNeeded()
        {
            if (!inQuotedSection)
                return false;
                
            outputStr.Append('"');
            inQuotedSection = false;

            return true;
        }

        var previouslyOutputConstant = false;

        var i = 0;
        foreach (var c in rawStr)
        {
            if (IsPrintableAsciiCharacter(c))
            {
                StartQuotedSectionIfNeeded(i != 0);

                // final thing.  there are some characters that, yes, are printable, but, we need an extra escape for them.
                switch (c)
                {
                    // NOTE: there might be some way to print a literal double quote in the output stream but,
                    // couldn't figure it out.  something about doubling quotes.
                    // for now, we'll count it as "not printable"
                        
                    case '\\':
                        // literal single backslash, we need to escape it when we write it.
                        outputStr.Append(@"\\"); // literally double backslash.
                        break;
                    default:
                        // otherwise, it's just a normal character
                        outputStr.Append(c);
                        break;
                }
            } 
            else
            {
                if (EndQuotedSectionIfNeeded() || previouslyOutputConstant)
                    outputStr.Append(", ");
                        
                outputStr.Append('$');
                outputStr.Append(Util.NumberToBaseString(c, Util.NumberBase.Hexadecimal, 2));

                previouslyOutputConstant = true;
            }

            ++i;
        }
            
        EndQuotedSectionIfNeeded();

        return outputStr.ToString();
    }
        
    public static int GetLineByteLength(this ILogCreatorDataSource<IData> data, int offset, int romSizeMax,
        int countPerLine)
    {
        var snesApi = data.Data.GetSnesApi();
        var flagType = snesApi.GetFlag(offset);

        if (flagType == FlagType.Opcode)
            return data.GetInstructionLength(offset);

        GetLineByteLengthMaxAndStep(flagType, out var max, out var step, countPerLine);

        var bankSize = data.GetBankSize();
        var myBank = offset / bankSize;
        
        var srcSnesAddress = data.ConvertPCtoSnes(offset);
        var srcRegions = srcSnesAddress == -1 ? [] : 
            data.Data.Regions
            .Where(x => srcSnesAddress >= x.StartSnesAddress && srcSnesAddress <= x.EndSnesAddress)
            .OrderBy(x => x.Priority)
            .ToList();
        
        var min = step;
        while (true)
        {
            if (min >= max)
                break;

            if (offset + min >= romSizeMax)
                break;

            if (snesApi.GetFlag(offset + min) != flagType)
                break;

            var endSnesAddress = data.ConvertPCtoSnes(offset + min);
            
            if (data.Labels.GetLabel(endSnesAddress) != null)
                break;

            // a plain byte-comment ends the run so it can be attached to its own line,
            // but a !!db/!!dw/!!dl data-byte directive is rendered in-place inside this run,
            // so it must NOT split the run into separate lines.
            var endComment = data.GetComment(endSnesAddress);
            if (endComment != null && DataByteDirective.TryParse(endComment) == null)
                break;

            if ((offset + min) / bankSize != myBank)
                break;
            
            // check if we crossed a boundary of any "region" defined
            // NOTE: doing it this way means that region boundaries that don't fall neatly across our "min" division
            // might create correct but non-intuitive situations.
            // i.e. a region boundary that starts/ends in the middle of a 16bit value
            var dstRegions = endSnesAddress == -1 ? [] : 
                data.Data.Regions
                    .Where(x => endSnesAddress >= x.StartSnesAddress && endSnesAddress <= x.EndSnesAddress)
                    .OrderBy(x => x.Priority)
                    .ToList();

            // warning: compares by reference (good enough for now)
            if (!srcRegions.SequenceEqual(dstRegions))
                break;
            
            min += step;
        }
        return min;
    }
        
    private static void GetLineByteLengthMaxAndStep(FlagType flagType, out int max, out int step, int dataPerLineSize)
    {
        max = 1; step = 1;

        switch (flagType)
        {
            case FlagType.Opcode:
                break;
            case FlagType.Unreached:
            case FlagType.Operand:
            case FlagType.Data8Bit:
            case FlagType.Graphics:
            case FlagType.Music:
            case FlagType.Empty:
                max = dataPerLineSize;
                break;
            case FlagType.Text:
                max = 21;
                break;
            case FlagType.Data16Bit:
                step = 2;
                max = dataPerLineSize;
                break;
            case FlagType.Data24Bit:
                step = 3;
                max = dataPerLineSize;
                break;
            case FlagType.Data32Bit:
                step = 4;
                max = dataPerLineSize;
                break;
            case FlagType.Pointer16Bit:
                step = 2;
                max = 2;
                break;
            case FlagType.Pointer24Bit:
                step = 3;
                max = 3;
                break;
            case FlagType.Pointer32Bit:
                step = 4;
                max = 4;
                break;
        }
    }

    public static string GeneratePointerStr(this ISnesApi<IData> data, int offset, int bytes)
    {
        var ia = -1;
        string format = "", param = "";
        switch (bytes)
        {
            case 2:
                // here's a tricky Diz-specific thing.
                // at this address, we only have the two bytes of the IA to work with (since this is a 16-bit pointer).
                // we need to come up with a bank# to use for this.
                //
                // which one to use?
                // 1. almost always: we want to use the SAME bank as where the pointer is sitting. but we don't want to force the user to use that.
                var autoDetectedBank = RomUtil.GetBankFromSnesAddress(data.ConvertPCtoSnes(offset));
                    
                // except... 2. we'll still allow the user to manually specify the bank by typing into the grid.
                // this is useful if the pointers are going to RAM addresses [ex: Chrono Trigger],
                // or, for places where we're using another bank in the code [ex: Megaman X text code]
                var bankFromUser = data.GetDataBank(offset);

                // Use the autodetected bank# unless the user excplicitly typed in a non-zero value.
                var bankToUse = bankFromUser != 0 ? bankFromUser : autoDetectedBank;
                    
                ia = (bankToUse << 16) | data.GetRomWordUnsafe(offset);
                    
                format = "dw {0}";
                param = Util.NumberToBaseString(data.GetRomWordUnsafe(offset), Util.NumberBase.Hexadecimal, 4, true);
                break;
            case 3:
                ia = data.GetRomLongUnsafe(offset);
                format = "dl {0}";
                param = Util.NumberToBaseString(data.GetRomLongUnsafe(offset), Util.NumberBase.Hexadecimal, 6, true);
                break;
            case 4:
                ia = data.GetRomLongUnsafe(offset);
                format = "dl {0}" +
                         $" : db {Util.NumberToBaseString(data.GetRomByteUnsafe(offset + 3), Util.NumberBase.Hexadecimal, 2, true)}";
                param = Util.NumberToBaseString(data.GetRomLongUnsafe(offset), Util.NumberBase.Hexadecimal, 6, true);
                break;
        }

        if (data.ConvertSnesToPc(ia) < 0) 
            return string.Format(format, param);
            
        var labelName = data.Labels.GetLabelName(ia);
                
        // check: filter +/- labels here, like "+", "-", "++", "--", etc
        if (labelName != "" && !RomUtil.IsValidPlusMinusLabel(labelName)) {
            param = labelName;
        }

        return string.Format(format, param);
    }
        
    public static string GetFormattedBytes(this IReadOnlyByteSource data, int offset, int step, int bytes)
    {
        var res = step switch
        {
            1 => "db ",
            2 => "dw ",
            3 => "dl ",
            4 => "dd ",
            _ => ""
        };

        for (var i = 0; i < bytes; i += step)
        {
            if (i > 0) res += ",";

            switch (step)
            {
                case 1:
                    res += Util.NumberToBaseString(data.GetRomByteUnsafe(offset + i), Util.NumberBase.Hexadecimal, 2,
                        true);
                    break;
                case 2:
                    res += Util.NumberToBaseString(data.GetRomWordUnsafe(offset + i), Util.NumberBase.Hexadecimal, 4,
                        true);
                    break;
                case 3:
                    res += Util.NumberToBaseString(data.GetRomLongUnsafe(offset + i), Util.NumberBase.Hexadecimal, 6,
                        true);
                    break;
                case 4:
                    res += Util.NumberToBaseString(data.GetRomDoubleWordUnsafe(offset + i), Util.NumberBase.Hexadecimal,
                        8, true);
                    break;
            }
        }

        return res;
    }

    // Render a run of data bytes as a db/dw/dl/dd line, substituting any !!db/!!dw/!!dl
    // symbolic override (stored in a byte's comment) in place of the raw hex and advancing
    // by the directive's declared width. Falls back to Diz's per-element hex when no override
    // is present, so a run with ZERO overrides renders byte-for-byte identically to
    // GetFormattedBytes above. The substitution happens in-place inside the comma list.
    public static string GetFormattedBytesWithDataOverrides(this ISnesApi<IData> data, int offset, int step, int bytes,
        ILogCreatorForGenerator logCreator)
    {
        var lineKeyword = DataDirectiveKeyword(step);

        // walk the run, emitting one (keyword, element) per value: a directive contributes
        // its own-width keyword + expression and advances by its width; every other position
        // contributes the line's keyword + a hex element and advances by step.
        var segments = new List<(string keyword, string element)>();
        for (var i = 0; i < bytes;)
        {
            // resolve the directive from the byte's comment, falling back to its label's
            // comment (GetCommentText: byte comment first, else label comment). A data table's
            // anchor byte is labeled and can't also hold a byte comment, so a directive placed
            // at the start of the label comment is how that anchor byte gets symbolicated.
            var directive = DataByteDirective.TryParse(data.GetCommentText(data.ConvertPCtoSnes(offset + i)));

            // honor the directive only if its declared width fits within what remains of this
            // run. otherwise (a !!dw/!!dl whose span would cross the run's end into a labeled /
            // region-boundary byte) fall through to raw hex so we never overshoot and double-emit
            // the next line's bytes; the un-applied directive is visible as raw hex.
            if (directive != null && i + directive.Value.WidthBytes <= bytes)
            {
                var width = directive.Value.WidthBytes;
                segments.Add((DataDirectiveKeyword(width), directive.Value.Expr));

                // only single-byte !!db overrides auto-generate a "!name = $xx" define
                // (identical to the operand-override case). !!dw/!!dl never do.
                if (width == 1)
                    logCreator.OnDataByteOverridden(offset + i, FormatHexElement(data, offset + i, 1), directive.Value.Expr);

                i += width;
            }
            else
            {
                segments.Add((lineKeyword, FormatHexElement(data, offset + i, step)));
                i += step;
            }
        }

        // coalesce consecutive same-keyword elements into one comma list ("db a,b,c");
        // join runs of differing keywords with asar's " : " statement separator so a
        // mixed-width line ("db $AA : dw VMDATAL : db $BB") still assembles byte-exact.
        // With zero directives this is a single group == the old GetFormattedBytes output.
        var res = new StringBuilder();
        for (var idx = 0; idx < segments.Count;)
        {
            var keyword = segments[idx].keyword;
            res.Append(res.Length == 0 ? "" : " : ").Append(keyword).Append(' ').Append(segments[idx].element);
            idx++;
            while (idx < segments.Count && segments[idx].keyword == keyword)
                res.Append(',').Append(segments[idx++].element);
        }

        return res.ToString();
    }

    private static string DataDirectiveKeyword(int widthBytes) => widthBytes switch
    {
        1 => "db",
        2 => "dw",
        3 => "dl",
        4 => "dd",
        _ => ""
    };

    private static string FormatHexElement(IReadOnlyByteSource data, int offset, int step) => step switch
    {
        1 => Util.NumberToBaseString(data.GetRomByteUnsafe(offset), Util.NumberBase.Hexadecimal, 2, true),
        2 => Util.NumberToBaseString(data.GetRomWordUnsafe(offset), Util.NumberBase.Hexadecimal, 4, true),
        3 => Util.NumberToBaseString(data.GetRomLongUnsafe(offset), Util.NumberBase.Hexadecimal, 6, true),
        4 => Util.NumberToBaseString(data.GetRomDoubleWordUnsafe(offset), Util.NumberBase.Hexadecimal, 8, true),
        _ => ""
    };
}