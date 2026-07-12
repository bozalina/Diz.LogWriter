using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diz.Core.Interfaces;
using Diz.Core.model.snes;
using Diz.Core.util;
using Diz.Cpu._65816;
using Diz.LogWriter.util;
using JetBrains.Annotations;

namespace Diz.LogWriter;

// this class outputs the meat of the assembly generation process, it prints the actual assembler instructions
public class AsmCreationInstructions : AsmCreationBase
{
    public bool EnableRegionIncSrc { get; init; } = true;
    
    private readonly List<int> visitedBanks = [];
    private int currentBank = -1;
    
    private int GetBankFromOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            throw new InvalidDataException($"Rom offset required to map to SNES address: {offset}");
        
        return RomUtil.GetBankFromSnesAddress(snesAddress);
    }
    
    private void SwitchBanksIfNeeded(int offset)
    {
        // remember: in LoRom mapping, an offset like 0 will map to SNES address $808000.
        
        var bank = GetBankFromOffset(offset);
        if (bank == currentBank) 
            return;

        if (currentRegionName != "") {
            // we're in a region but that's not allowed when we cross banks,
            // because the "incsrc" for this won't work
            throw new InvalidDataException($"Crossing banks in a region with ExportSeparateFile=true is not allowed. In '{currentRegionName}', crossing into new bank {bank}");
        }
        
        SwitchBank(bank, offset);
        CheckForBankCrossError(offset);
    }

    // switch to a bank, one we may or may not have visited before.
    // we also need the real offset because in LoRom, bank 80 starts at ROM offset 0x00 == SNES address 0x808000
    private void SwitchBank(int bank, int offset)
    {
        LogCreator.SwitchOutputStreamForBank(bank);
        
        // note: we can't just bank<<16 here, it will produce incorrect output for LoRom
        var snesAddress = Data.ConvertPCtoSnes(offset);

        if (!visitedBanks.Contains(bank)) {
            visitedBanks.Add(bank);
            LogCreator.WriteOrgDirectiveForSnesAddress(snesAddress);
        }

        currentBank = bank;
    }

    private void CheckForBankCrossError(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress % Data.GetBankSize() != 0)
            LogCreator.OnErrorReported(offset, "An instruction crossed a bank boundary.");
    }

    // an !!incbin directive at `offset` makes GetLineByteLength skip N bytes (collapsing them into
    // incbin line(s)). validate that span here so a bad count is reported (bumping ErrorCount)
    // instead of silently corrupting output. A span that crosses a bank boundary is NOT an error:
    // it is emitted as the whole file sliced by one range-incbin per bank (see IncBinSegmentMap), so
    // each bank re-enters at its own org. It must, however, stay inside the ROM, and no label may
    // fall strictly INSIDE a segment (a swallowed label would vanish from the source) — a label at a
    // bank boundary starts the next segment and IS emitted, so it is allowed.
    private void CheckForIncBinErrors(int offset)
    {
        var incbin = IncBinDirective.TryParse(Data.GetCommentText(Data.ConvertPCtoSnes(offset)));
        if (incbin == null)
            return;

        var n = incbin.Value.ByteCount;

        if (offset + n > LogCreator.GetRomSize()) {
            LogCreator.OnErrorReported(offset, $"incbin byte count {n} runs past the end of the ROM.");
            return;
        }

        var bankSize = Data.GetBankSize();
        for (var i = 1; i < n; i++) {
            // a label exactly at a bank boundary begins the next incbin segment and is emitted on its
            // continuation line, so it is not swallowed; only labels strictly inside a segment vanish.
            if ((offset + i) % bankSize == 0)
                continue;

            var label = Data.Labels.GetLabel(Data.ConvertPCtoSnes(offset + i));
            if (label?.Name.Length > 0)
                LogCreator.OnErrorReported(offset + i, $"label '{label.Name}' falls inside an incbin span and would be dropped.");
        }
    }

    // includes both real regions from Data, and
    // temporary extra regions generated dynamically from comments
    private List<IRegion> allRegions = [];

    protected override void Execute()
    {
        GenerateExtraIncSrcRegionsFromComments();

        var romSize = LogCreator.GetRomSize();
        
        // perf: this is the meat of the export, takes a while
        for (var offset = 0; offset < romSize;) {
            WriteOutputLinesForRomOffset(ref offset);
        }

        LogCreator.ReportVisitedBanks(visitedBanks);
    }

    private void GenerateExtraIncSrcRegionsFromComments()
    {
        var regionSnesAddrStart = -1;
        var regionName = "";
        
        // start with all existing real regions, and we'll append from here
        allRegions.Clear();
        allRegions.AddRange(LogCreator.Data.Data.Regions);
        
        foreach (var (snesAddress, comment) in LogCreator.Data.Data.Comments)
        {
            var parsed = CpuUtils.ParseCommentSpecialDirective(comment);
            if (parsed == null || parsed.IncludeSrc == CpuUtils.OperandOverride.IncSrcOverride.None)
                continue;
            
            var offset = Data.ConvertSnesToPc(snesAddress);

            switch (parsed.IncludeSrc)
            {
                case CpuUtils.OperandOverride.IncSrcOverride.IncSrcStart:
                {
                    if (regionSnesAddrStart != -1)
                    {
                        // we're already inside a region, so we can't generate a new one
                        // this is a bug in the source code, but we'll just ignore it
                        LogCreator.OnErrorReported(offset,
                            "Extra region directive found 'ir' inside a dynamic region. This is not supported.");
                        continue;
                    }

                    var labelName = Data.Labels.GetLabel(snesAddress)?.Name;
                    if ((labelName?.Length ?? 0) == 0)
                    {
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ir' without a label defined. This is not supported.");
                        continue;
                    }
                
                    // good to go
                    regionName = labelName;
                    regionSnesAddrStart = snesAddress;
                    break;
                }
                case CpuUtils.OperandOverride.IncSrcOverride.IncSrcEnd:
                {
                    if (regionSnesAddrStart == -1)
                    {
                        // we're not inside a region, so we can't generate a new one
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ie' outside a dynamic region. This is not supported. Ignoring");;
                        continue;
                    }

                    if (regionName == "")
                    {
                        LogCreator.OnErrorReported(offset, "Extra region directive found 'ie'  name defined. This is not supported. Ignoring");
                        continue;
                    }
                
                    // good to go
                    allRegions.Add(new Region {
                        ExportSeparateFile = true,
                        RegionName = regionName,
                        Priority = 0,
                        StartSnesAddress = regionSnesAddrStart,
                        EndSnesAddress = snesAddress,
                    });

                    // reset
                    regionName = "";
                    regionSnesAddrStart = -1;
                    break;
                }
            }
        }

        if (regionSnesAddrStart != -1) {
            LogCreator.OnErrorReported(Data.ConvertSnesToPc(regionSnesAddrStart), $"Unclosed region start directive '{regionName}', ignoring");
        }
    }

    private readonly List<string> previousRegions = [];
    private string currentRegionName = "";

    private void CheckIfRegionChanged(int offset)
    {
        if (!EnableRegionIncSrc)
            return;
        
        var nextRegion = GetRegionAtOffset(offset);                     // can be null if no region here
        var nextRegionName = nextRegion?.RegionName ?? "";

        // did anything change?
        if (nextRegionName == currentRegionName)
            return; // no
        
        var leavingActiveRegion = currentRegionName.Length > 0;
        if (leavingActiveRegion)
        {
            // switch out of the region and back into the current bank
            LogCreator.SwitchOutputStreamForBank(currentBank);
            currentRegionName = "";
        }
        
        // we're back in the parent bank now
        
        // if we're next crossing the bank, don't allow jumping into a new region
        // (bank crossing is part of the reason why caller must to call this function twice, before and after bank is crossed)
        var crossingBankNext = currentBank != GetBankFromOffset(offset);
        if (crossingBankNext)
            return;
        
        var goingIntoNewRegion = nextRegionName.Length > 0;
        if (!goingIntoNewRegion) 
            return;
        
        // make sure we haven't already included a region like this (invalid)
        if (previousRegions.Contains(currentRegionName))
            throw new InvalidDataException($"Multiple regions  with ExportSeparateFile=true use the same name '{currentRegionName}, invalid'");
            
        previousRegions.Add(nextRegionName);
            
        var regionNameIncSrcFilenameTarget = nextRegionName + ".asm";
            
        // we're in the bank file, write an "incsrc" to the new file we're about to use
        LogCreator.WriteIncludeFileDirective(regionNameIncSrcFilenameTarget, padWithBlankLine: true);
            
        // switch further output to the region-specific new file:
        LogCreator.SwitchOutputStream(regionNameIncSrcFilenameTarget);

        var regionBytesSize = -1;
        if (nextRegion != null)
        {
            var startOffset = Data.ConvertSnesToPc(nextRegion.StartSnesAddress);
            var endOffset = Data.ConvertSnesToPc(nextRegion.EndSnesAddress);
            if (startOffset != -1 && endOffset != -1 && GetBankFromOffset(startOffset) == GetBankFromOffset(endOffset)) {
                // we'll only report the size if they're in the same bank, otherwise the math gets weird. maybe. unsure. whatever
                regionBytesSize = endOffset - startOffset;   
            }
        }
        LogCreator.WriteHeaderForNewlyIncludedFile(offset, "region", nextRegionName, regionBytesSize);

        currentRegionName = nextRegionName;
    }

    [CanBeNull]
    private IRegion GetRegionAtOffset(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        if (snesAddress == -1)
            return null;
        
        // find any applicable regions in the surrounding context of where we are in the ROM offset
        var applicableOrderedRegions = allRegions
            .Where(x => 
                snesAddress >= x.StartSnesAddress && 
                snesAddress <= x.EndSnesAddress && 
                x.ExportSeparateFile
            )
            .OrderBy(x => x.Priority)
            .ToList();

        var region = applicableOrderedRegions.FirstOrDefault(); // can be null
        
        // in the future maybe we can deal with this
        if (applicableOrderedRegions.Count > 1)
            throw new InvalidDataException($"Multiple overlapping regions with ExportSeparateFile=true. This is not supported. '{region?.RegionName}'");
        
        return region;
    }

    // write one line of the assembly output
    // address is a "PC address" i.e. offset into the ROM.
    // not a SNES address.
    private void WriteOutputLinesForRomOffset(ref int offset)
    {
        // since we can only CHANGE INTO a region when we're in a bank, and if we're just starting, we're not in a bank yet,
        // skip this check. this check will ONLY make sure  
        if (currentBank != -1) {
            CheckIfRegionChanged(offset);
        }

        // switch output to another bank file if needed.
        // note: being inside a region during a bank cross
        SwitchBanksIfNeeded(offset);
        CheckIfRegionChanged(offset);

        WriteBlankLineIfStartingNewParagraph(offset);
        GenerateAndWriteCodeOutputLinesForRomOffset(offset);    // the important thing
        LogCreator.DataErrorChecking.CheckForErrorsAt(offset);
        CheckForIncBinErrors(offset);
        WriteBlankLineIfEndPoint(offset);

        // TODO: WARNING: TECHNICALLY, we should be checking for bank and region crosses
        //  looking at our previous offset through our new offset.  we could miss transitions or
        //  put region includes in the wrong spot.  Happens if there's regions that begin in the middle of 
        //  boundaries like inside the middle byte of data labelled as 24-bit.
        offset += LogCreator.GetLineByteLength(offset);
    }

    private void GenerateAndWriteCodeOutputLinesForRomOffset(int offset)
    {
        // one ROM offset might generate multiple lines of output (example: if there's a multi-line comment)
        // generate them all and write them out:
        var outputLines = LogCreator.LineGenerator.GenerateNormalLines(offset);
        foreach (var outputLine in outputLines) {
            LogCreator.WriteLine(outputLine);
        }
    }

    private void WriteBlankLineIfStartingNewParagraph(int offset)
    {
        // skip if we're in the middle of a pointer table
        if (Data.GetFlag(offset) is FlagType.Pointer16Bit or FlagType.Pointer24Bit or FlagType.Pointer32Bit)
            return;

        if (Data.IsLocationAReadPoint(offset) || AreAnyLabelsPresentAt(offset)) 
            LogCreator.WriteEmptyLine();
    }

    private bool AreAnyLabelsPresentAt(int offset)
    {
        var snesAddress = Data.ConvertPCtoSnes(offset);
        return Data.Labels.GetLabel(snesAddress)?.Name.Length > 0;
    }

    private void WriteBlankLineIfEndPoint(int offset)
    {
        if (!Data.IsLocationAnEndPoint(offset))
            return;

        LogCreator.WriteEmptyLine();
    }
}