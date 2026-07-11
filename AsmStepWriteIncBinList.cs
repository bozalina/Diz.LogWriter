using Diz.LogWriter.util;

namespace Diz.LogWriter;

// Writes bins.json: a manifest of every !!incbin asset (file, start, length, comment).
// Thin wrapper over IncBinManifest; disabled in SingleFile mode (see LogCreator.RegisterSteps),
// so it never pollutes the concatenated single-file/string output.
public class AsmStepWriteIncBinList : AsmCreationBase
{
    protected override void Execute()
    {
        var entries = IncBinManifest.Collect(Data, LogCreator.GetRomSize());

        LogCreator.SwitchOutputStream("bins.json");
        LogCreator.WriteLine(IncBinManifest.ToJson(entries));
    }
}
