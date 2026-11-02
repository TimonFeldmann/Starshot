#if DEBUG
using System;
using Microsoft.Graphics.Display;
using Microsoft.UI.Windowing;

namespace Starshot.Features.Recording;

/// <summary>
/// Headless per-display advanced-color probe (DEBUG builds only; entry: the --hdr-probe branch in Program.Main).
/// Usage: Starshot.exe --hdr-probe
/// Prints the OS's live per-display state: current advanced-color kind, panel luminance range (nits),
/// SDR white level, and which advanced-color kinds are available — used to determine whether the OS is
/// actually driving a display in HDR mode (diagnosing gray HDR10 recordings). Same API source as the
/// recording decision: Microsoft.Graphics.Display.DisplayInformation.GetAdvancedColorInfo().
/// </summary>
internal static class HdrProbeHost
{
    public static int Run(string[] args)
    {
        // WinRT interop + minimal config/logging init, same as the record-test host
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        AppConfig.PrepareHeadless();
        try
        {
            // Index access, not foreach: enumerating the projected FindAll() list via GetEnumerator
            // fails with InvalidCastException (known projection quirk)
            var displays = DisplayArea.FindAll();
            Console.WriteLine($"[hdr-probe] displays={displays.Count}");
            for (int i = 0; i < displays.Count; i++)
            {
                var area = displays[i];
                using var di = DisplayInformation.CreateForDisplayId(area.DisplayId);
                var adv = di.GetAdvancedColorInfo();
                var ob = area.OuterBounds;
                Console.WriteLine(
                    $"[hdr-probe] [{i}] logical={ob.Width}x{ob.Height} primary={area.IsPrimary} displayId=0x{area.DisplayId.Value:X16}"
                );
                Console.WriteLine(
                    $"[hdr-probe]     kind={adv.CurrentAdvancedColorKind} sdrWhite={adv.SdrWhiteLevelInNits} nits=[{adv.MinLuminanceInNits}..{adv.MaxLuminanceInNits}] maxFALL={adv.MaxAverageFullFrameLuminanceInNits}"
                );
                Console.WriteLine(
                    $"[hdr-probe]     available: hdr={adv.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange)}"
                );
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hdr-probe] failed: {ex}");
            return 3;
        }
        return 0;
    }
}
#endif
