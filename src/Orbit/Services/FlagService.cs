using System.Globalization;
using System.IO;
using System.Text.Json;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// Writes FastFlags to ClientSettings/ClientAppSettings.json.
/// Since September 2025 Roblox only honours flags on its official allowlist;
/// every preset below uses allowlisted flags only.
/// </summary>
public static class FlagService
{
    /// <summary>Mesh level-of-detail switching distances per "Mesh detail" level (0 = lowest detail).</summary>
    private static readonly int[][] MeshLevels =
    {
        new[] { 0, 0, 0, 0 },
        new[] { 50, 100, 150, 200 },
        new[] { 100, 200, 300, 400 },
        new[] { 200, 400, 600, 800 },
        new[] { 250, 500, 750, 1000 },
    };

    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>();
        string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        if (s.MeshDetail >= 0)
        {
            var d = MeshLevels[Math.Clamp(s.MeshDetail, 0, MeshLevels.Length - 1)];
            flags["DFIntCSGLevelOfDetailSwitchingDistance"] = I(d[0]);
            flags["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = I(d[1]);
            flags["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = I(d[2]);
            flags["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = I(d[3]);
        }

        if (s.MsaaSamples > 0) flags["FIntDebugForceMSAASamples"] = I(s.MsaaSamples);
        if (s.PreserveRenderingQuality) flags["DFFlagDisableDPIScale"] = "True";
        if (s.FrmQuality > 0) flags["DFIntDebugFRMQualityLevelOverride"] = I(s.FrmQuality);

        switch (s.RenderingMode)
        {
            case "d3d11": flags["FFlagDebugGraphicsPreferD3D11"] = "True"; break;
            case "vulkan": flags["FFlagDebugGraphicsPreferVulkan"] = "True"; break;
            case "opengl": flags["FFlagDebugGraphicsPreferOpenGL"] = "True"; break;
        }

        if (s.TextureQuality >= 0)
        {
            flags["DFFlagTextureQualityOverrideEnabled"] = "True";
            flags["DFIntTextureQualityOverride"] = I(s.TextureQuality);
        }

        if (s.ExclusiveFullscreen) flags["FFlagHandleAltEnterFullscreenManually"] = "False";
        if (s.GraySky) flags["FFlagDebugSkyGray"] = "True";
        if (s.PauseVoxelizer) flags["DFFlagDebugPauseVoxelizer"] = "True";
        if (s.DisableGrass)
        {
            flags["FIntFRMMinGrassDistance"] = "0";
            flags["FIntFRMMaxGrassDistance"] = "0";
        }

        // The user's own flags from the editor win over presets.
        foreach (var (key, value) in s.UserFlags)
            if (!string.IsNullOrWhiteSpace(key)) flags[key.Trim()] = value;

        return flags;
    }

    /// <summary>Writes the flags into every given Roblox folder. Returns how many folders were written.</summary>
    public static int Apply(AppSettings s, IEnumerable<string> versionDirs)
    {
        if (!s.ManageFastFlags) return 0;

        var json = JsonSerializer.Serialize(BuildFlags(s), new JsonSerializerOptions { WriteIndented = true });
        var written = 0;
        foreach (var folder in versionDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dir = Path.Combine(folder, "ClientSettings");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "ClientAppSettings.json"), json);
                written++;
            }
            catch { /* no write access */ }
        }
        return written;
    }

    /// <summary>Writes the flags to every Roblox installation Orbit can find.</summary>
    public static int ApplyEverywhere(AppSettings s) => Apply(s, RobloxPaths.FindPlayerFolders());

    /// <summary>Parses a JSON object of flags (for importing into the editor).</summary>
    public static Dictionary<string, string> ParseJson(string json)
    {
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected a JSON object like { \"FlagName\": \"Value\" }.");

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => prop.Value.GetRawText(),
            };
        }
        return result;
    }
}
