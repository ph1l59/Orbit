using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Orbit.Models;

namespace Orbit.Services;

public sealed record ApplyResult(int FoldersWritten, int FoldersFailed, int FlagCount, string FpsMessage, bool FpsOk)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (FoldersWritten == 0 && FoldersFailed == 0)
                parts.Add("Keine Roblox-Installation gefunden – Grafik-Einstellungen werden beim nächsten Start angewendet.");
            else
                parts.Add($"{FlagCount} Grafik-Einstellung(en) in {FoldersWritten} Roblox-Installation(en) gespeichert" +
                          (FoldersFailed > 0 ? $" ({FoldersFailed} ohne Schreibrechte)." : "."));
            parts.Add(FpsMessage);
            return string.Join(" ", parts);
        }
    }
}

/// <summary>
/// Applies performance settings:
///  - graphics options as FastFlags in ClientSettings/ClientAppSettings.json
///    (only flags on Roblox's allowlist have an effect since Sept. 2025)
///  - the frame rate cap via Roblox's own "FramerateCap" setting in GlobalBasicSettings_13.xml
/// </summary>
public static class FlagService
{
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>();

        if (s.QualityLevel > 0)
            flags["DFIntDebugFRMQualityLevelOverride"] = s.QualityLevel.ToString(CultureInfo.InvariantCulture);

        if (s.MsaaSamples > 0)
            flags["FIntDebugForceMSAASamples"] = s.MsaaSamples.ToString(CultureInfo.InvariantCulture);

        if (s.TextureQuality >= 0)
        {
            flags["DFFlagTextureQualityOverrideEnabled"] = "True";
            flags["DFIntTextureQualityOverride"] = s.TextureQuality.ToString(CultureInfo.InvariantCulture);
        }

        switch (s.Renderer)
        {
            case "d3d11": flags["FFlagDebugGraphicsPreferD3D11"] = "True"; break;
            case "vulkan": flags["FFlagDebugGraphicsPreferVulkan"] = "True"; break;
            case "opengl": flags["FFlagDebugGraphicsPreferOpenGL"] = "True"; break;
        }

        if (s.DisableDpiScale) flags["DFFlagDisableDPIScale"] = "True";
        if (s.ExclusiveFullscreen) flags["FFlagHandleAltEnterFullscreenManually"] = "False";
        if (s.GraySky) flags["FFlagDebugSkyGray"] = "True";
        if (s.DisableGrass)
        {
            flags["FIntFRMMinGrassDistance"] = "0";
            flags["FIntFRMMaxGrassDistance"] = "0";
        }

        foreach (var (key, value) in ParseCustomFlags(s.CustomFlags, out _))
            flags[key] = value;

        return flags;
    }

    /// <summary>Parses the user's custom JSON. Returns an empty set and an error message if it is invalid.</summary>
    public static Dictionary<string, string> ParseCustomFlags(string? json, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Eigene Flags müssen ein JSON-Objekt sein, z. B. { \"FIntDebugForceMSAASamples\": \"4\" }.";
                return result;
            }

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
        }
        catch (JsonException ex)
        {
            error = "Eigene Flags sind kein gültiges JSON: " + ex.Message;
            result.Clear();
        }

        return result;
    }

    public static ApplyResult ApplyAll(AppSettings s)
    {
        var flags = BuildFlags(s);
        var json = JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true });

        int written = 0, failed = 0;
        foreach (var folder in RobloxPaths.FindPlayerFolders())
        {
            try
            {
                var dir = Path.Combine(folder, "ClientSettings");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "ClientAppSettings.json"), json);
                written++;
            }
            catch
            {
                failed++;
            }
        }

        var (fpsOk, fpsMessage) = ApplyFramerateCap(s.FpsCap);
        return new ApplyResult(written, failed, flags.Count, fpsMessage, fpsOk);
    }

    private static (bool ok, string message) ApplyFramerateCap(int cap)
    {
        if (cap <= 0) return (true, "FPS-Limit: unverändert.");

        var path = RobloxPaths.GlobalBasicSettingsFile;
        if (!File.Exists(path))
            return (false, "FPS-Limit: Roblox-Einstellungsdatei nicht gefunden. Starte Roblox einmal normal und versuche es dann erneut.");

        try
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);

            var element = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "int" && (string?)e.Attribute("name") == "FramerateCap");

            if (element == null)
            {
                var properties = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Item" && (string?)e.Attribute("class") == "UserGameSettings")
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");

                if (properties == null)
                    return (false, "FPS-Limit: Die Roblox-Einstellungsdatei hat ein unbekanntes Format.");

                element = new XElement("int", new XAttribute("name", "FramerateCap"));
                properties.Add(element);
            }

            element.Value = cap.ToString(CultureInfo.InvariantCulture);

            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
            using (var writer = XmlWriter.Create(path, settings))
                doc.Save(writer);

            var label = cap >= 9999 ? "unbegrenzt" : cap + " FPS";
            var note = RobloxPaths.IsRobloxRunning()
                ? " Hinweis: Roblox läuft gerade – das Limit greift ab dem nächsten Start."
                : "";
            return (true, $"FPS-Limit: {label}.{note}");
        }
        catch (Exception ex)
        {
            return (false, "FPS-Limit konnte nicht gesetzt werden: " + ex.Message);
        }
    }
}
