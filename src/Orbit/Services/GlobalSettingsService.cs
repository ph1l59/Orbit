using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Orbit.Services;

/// <summary>The in-game settings Roblox keeps in GlobalBasicSettings_13.xml.</summary>
public sealed class RobloxGlobalSettings
{
    /// <summary>0 = automatic, 1..10.</summary>
    public int GraphicsQuality { get; set; }
    public int FramerateCap { get; set; } = 60;
    /// <summary>0..1, 1 = default.</summary>
    public double Transparency { get; set; } = 1;
    public bool ReducedMotion { get; set; }
    /// <summary>1 = default, 2 = large, 3 = larger, 4 = largest.</summary>
    public int FontSize { get; set; } = 1;
    public double MouseSensitivity { get; set; } = 0.2;
    public bool VrEnabled { get; set; }
}

/// <summary>Reads and writes Roblox's own in-game settings file.</summary>
public static class GlobalSettingsService
{
    private static string FilePath => RobloxPaths.GlobalBasicSettingsFile;

    public static bool Exists => File.Exists(FilePath);

    public static bool IsReadOnly
    {
        get => Exists && new FileInfo(FilePath).IsReadOnly;
        set
        {
            if (!Exists) return;
            var info = new FileInfo(FilePath);
            info.IsReadOnly = value;
        }
    }

    private static XElement? Find(XDocument doc, string type, string name)
        => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == type && (string?)e.Attribute("name") == name);

    private static XElement? Properties(XDocument doc)
        => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Item" && (string?)e.Attribute("class") == "UserGameSettings")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");

    private static void Set(XDocument doc, string type, string name, string value)
    {
        var element = Find(doc, type, name);
        if (element == null)
        {
            var props = Properties(doc) ?? throw new InvalidDataException("Unknown settings file format.");
            element = new XElement(type, new XAttribute("name", name));
            props.Add(element);
        }
        element.Value = value;
    }

    private static int GetInt(XDocument doc, string type, string name, int fallback)
        => int.TryParse(Find(doc, type, name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double GetDouble(XDocument doc, string name, double fallback)
        => double.TryParse(Find(doc, "float", name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool GetBool(XDocument doc, string name, bool fallback)
        => bool.TryParse(Find(doc, "bool", name)?.Value, out var v) ? v : fallback;

    public static RobloxGlobalSettings? Load()
    {
        if (!Exists) return null;
        var doc = XDocument.Load(FilePath, LoadOptions.PreserveWhitespace);
        return new RobloxGlobalSettings
        {
            GraphicsQuality = GetInt(doc, "token", "SavedQualityLevel", 0),
            FramerateCap = GetInt(doc, "int", "FramerateCap", 60),
            Transparency = GetDouble(doc, "PreferredTransparency", 1),
            ReducedMotion = GetBool(doc, "ReducedMotion", false),
            FontSize = GetInt(doc, "token", "PreferredTextSize", 1),
            MouseSensitivity = GetDouble(doc, "MouseSensitivity", 0.2),
            VrEnabled = GetBool(doc, "VREnabled", false),
        };
    }

    public static void Save(RobloxGlobalSettings s)
    {
        if (!Exists) throw new FileNotFoundException("Start Roblox once so it creates its settings file.");

        var wasReadOnly = IsReadOnly;
        if (wasReadOnly) IsReadOnly = false;

        try
        {
            var doc = XDocument.Load(FilePath, LoadOptions.PreserveWhitespace);
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            string I(int v) => v.ToString(CultureInfo.InvariantCulture);

            Set(doc, "token", "SavedQualityLevel", I(Math.Clamp(s.GraphicsQuality, 0, 10)));
            if (s.GraphicsQuality > 0) Set(doc, "int", "GraphicsQualityLevel", I(s.GraphicsQuality * 2));
            Set(doc, "int", "FramerateCap", I(s.FramerateCap));
            Set(doc, "float", "PreferredTransparency", F(Math.Clamp(s.Transparency, 0, 1)));
            Set(doc, "bool", "ReducedMotion", s.ReducedMotion ? "true" : "false");
            Set(doc, "token", "PreferredTextSize", I(Math.Clamp(s.FontSize, 1, 4)));
            Set(doc, "float", "MouseSensitivity", F(s.MouseSensitivity));
            Set(doc, "bool", "VREnabled", s.VrEnabled ? "true" : "false");

            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
            using var writer = XmlWriter.Create(FilePath, settings);
            doc.Save(writer);
        }
        finally
        {
            if (wasReadOnly) IsReadOnly = true;
        }
    }
}
