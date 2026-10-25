namespace Orbit.Models;

public sealed class AppSettings
{
    // ----- Launcher -----

    /// <summary>Allow several Roblox clients at the same time.</summary>
    public bool MultiRoblox { get; set; }

    /// <summary>Seconds to wait between launching two accounts.</summary>
    public int LaunchDelaySeconds { get; set; } = 8;

    /// <summary>Start RobloxPlayerBeta.exe directly instead of going through the roblox-player:// handler.</summary>
    public bool LaunchDirect { get; set; } = true;

    public string LastPlace { get; set; } = "";
    public string LastJobId { get; set; } = "";

    // ----- Performance -----

    /// <summary>0 = don't touch, otherwise the frame rate cap written to GlobalBasicSettings_13.xml.</summary>
    public int FpsCap { get; set; }

    /// <summary>0 = automatic, 1..21 = forced render quality level.</summary>
    public int QualityLevel { get; set; }

    /// <summary>-1 = automatic, otherwise 1/2/4/8 samples.</summary>
    public int MsaaSamples { get; set; } = -1;

    /// <summary>-1 = automatic, otherwise 0..3.</summary>
    public int TextureQuality { get; set; } = -1;

    /// <summary>auto | d3d11 | vulkan | opengl</summary>
    public string Renderer { get; set; } = "auto";

    public bool DisableDpiScale { get; set; }
    public bool ExclusiveFullscreen { get; set; }
    public bool DisableGrass { get; set; }
    public bool GraySky { get; set; }

    /// <summary>Extra FastFlags as a JSON object. Only flags on Roblox's allowlist have an effect.</summary>
    public string CustomFlags { get; set; } = "";
}
