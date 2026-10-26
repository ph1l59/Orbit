using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Orbit.Services;

/// <summary>A ready-to-show avatar: the 3D model plus its bounding box and the side Roblox's camera looks at.</summary>
public sealed record AvatarModel(Model3DGroup Model, Rect3D Bounds, Vector3D Front);

/// <summary>
/// Loads a user's avatar as a 3D model from Roblox (thumbnails "avatar-3d": OBJ mesh, MTL materials, PNG textures)
/// and turns it into a WPF <see cref="Model3DGroup"/>. Everything returned is frozen, so it can be built off the UI thread.
/// </summary>
public static class Avatar3D
{
    private static readonly Dictionary<long, AvatarModel> Cache = new();

    public static async Task<AvatarModel?> LoadAsync(long userId)
    {
        lock (Cache)
            if (Cache.TryGetValue(userId, out var cached)) return cached;

        try
        {
            var model = await Task.Run(() => BuildAsync(userId));
            if (model != null)
                lock (Cache) Cache[userId] = model;
            return model;
        }
        catch
        {
            return null; // the Play page falls back to the 2D picture
        }
    }

    private static async Task<AvatarModel?> BuildAsync(long userId)
    {
        // 1. Ask Roblox for the 3D render (it may still be rendering for a moment).
        string? manifestUrl = null;
        for (var attempt = 0; attempt < 6 && manifestUrl == null; attempt++)
        {
            var json = await RobloxApi.Http.GetStringAsync($"https://thumbnails.roblox.com/v1/users/avatar-3d?userId={userId}");
            using var doc = JsonDocument.Parse(json);
            var state = doc.RootElement.TryGetProperty("state", out var st) ? st.GetString() : null;
            if (state == "Completed" && doc.RootElement.TryGetProperty("imageUrl", out var url) && url.ValueKind == JsonValueKind.String)
                manifestUrl = url.GetString();
            else if (state is "Blocked" or "Error") return null;
            else await Task.Delay(1200);
        }
        if (manifestUrl == null) return null;

        // 2. The manifest names the mesh, the materials and the textures by hash.
        using var manifest = JsonDocument.Parse(await RobloxApi.Http.GetStringAsync(manifestUrl));
        var root = manifest.RootElement;
        var objText = await DownloadStringAsync(root.GetProperty("obj").GetString()!);
        var mtlText = await DownloadStringAsync(root.GetProperty("mtl").GetString()!);

        var front = new Vector3D(0, 0, -1);
        if (root.TryGetProperty("camera", out var cam) && cam.TryGetProperty("direction", out var dir))
        {
            // The camera looks *at* the avatar, so the avatar's front is the opposite way.
            var d = new Vector3D(-dir.GetProperty("x").GetDouble(), 0, -dir.GetProperty("z").GetDouble());
            if (d.Length > 0.01) { d.Normalize(); front = d; }
        }

        // Download every texture first ...
        var materials = ParseMtl(mtlText);
        var textures = new Dictionary<string, byte[]>();
        foreach (var hash in materials.Values.Select(m => m.Texture).OfType<string>().Distinct())
        {
            try { textures[hash] = await DownloadAsync(hash); }
            catch { /* that part keeps its plain color */ }
        }

        // ... then build all WPF objects in one go on this thread (no awaits from here on).
        foreach (var m in materials.Values)
        {
            Brush brush;
            try
            {
                brush = m.Texture != null && textures.TryGetValue(m.Texture, out var png)
                    ? TextureBrush(png, m.Color)
                    : new SolidColorBrush(m.Color);
            }
            catch
            {
                brush = new SolidColorBrush(m.Color);
            }
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            m.Built = material;
        }

        var (model, bounds) = BuildModel(objText, materials);
        model.Freeze();
        return new AvatarModel(model, bounds, front);
    }

    // ---------- Roblox CDN ----------

    /// <summary>Each hash says which of the t0–t7 CDN servers holds it.</summary>
    private static string CdnUrl(string hash)
    {
        if (hash.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return hash;
        var i = 31;
        foreach (var c in hash) i ^= c;
        return $"https://t{i % 8}.rbxcdn.com/{hash}";
    }

    private static async Task<byte[]> DownloadAsync(string hash)
    {
        try
        {
            return await RobloxApi.Http.GetByteArrayAsync(CdnUrl(hash));
        }
        catch when (!hash.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // Roblox has changed the server rule before – try the others.
            for (var n = 0; n < 8; n++)
            {
                try { return await RobloxApi.Http.GetByteArrayAsync($"https://t{n}.rbxcdn.com/{hash}"); }
                catch { /* next */ }
            }
            throw;
        }
    }

    private static async Task<string> DownloadStringAsync(string hash) =>
        System.Text.Encoding.UTF8.GetString(await DownloadAsync(hash));

    // ---------- Materials ----------

    private sealed class MaterialInfo
    {
        public Color Color = Colors.LightGray;
        public string? Texture;
        public Material? Built;
    }

    private static Dictionary<string, MaterialInfo> ParseMtl(string mtl)
    {
        var result = new Dictionary<string, MaterialInfo>();
        MaterialInfo? current = null;
        foreach (var raw in mtl.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("newmtl ")) { current = new MaterialInfo(); result[line[7..].Trim()] = current; }
            else if (current == null) continue;
            else if (line.StartsWith("Kd "))
            {
                var p = line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3)
                    current.Color = Color.FromRgb(ToByte(p[0]), ToByte(p[1]), ToByte(p[2]));
            }
            else if (line.StartsWith("map_Kd ")) current.Texture = line[7..].Trim();
            // "map_d" (an alpha map) is ignored on purpose: it would make the whole model see-through.
        }
        return result;
    }

    private static byte ToByte(string s) =>
        (byte)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);

    /// <summary>Puts the texture on top of the material color, so see-through parts show the skin color instead of black.</summary>
    private static Brush TextureBrush(byte[] png, Color under)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3] / 255.0;
            pixels[i] = (byte)(pixels[i] * a + under.B * (1 - a));
            pixels[i + 1] = (byte)(pixels[i + 1] * a + under.G * (1 - a));
            pixels[i + 2] = (byte)(pixels[i + 2] * a + under.R * (1 - a));
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return new ImageBrush(bitmap)
        {
            ViewportUnits = BrushMappingMode.Absolute, // texture coordinates are 0..1 of the whole image
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.Tile,
        };
    }

    // ---------- Mesh ----------

    private static (Model3DGroup, Rect3D) BuildModel(string obj, Dictionary<string, MaterialInfo> materials)
    {
        var positions = new List<Point3D>();
        var uvs = new List<Point>();
        var normals = new List<Vector3D>();
        var parts = new Dictionary<string, (MeshGeometry3D Mesh, Dictionary<(int, int, int), int> Index)>();
        var currentName = "";

        (MeshGeometry3D Mesh, Dictionary<(int, int, int), int> Index) Part(string name)
        {
            if (!parts.TryGetValue(name, out var part))
            {
                part = (new MeshGeometry3D(), new Dictionary<(int, int, int), int>());
                parts[name] = part;
            }
            return part;
        }

        static int Resolve(string s, int count)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            var n = int.Parse(s, CultureInfo.InvariantCulture);
            return n < 0 ? count + n : n - 1; // OBJ indices start at 1, negative ones count from the end
        }

        foreach (var raw in obj.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 2) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "v":
                    positions.Add(new Point3D(D(p[1]), D(p[2]), D(p[3])));
                    break;
                case "vt":
                    uvs.Add(new Point(D(p[1]), 1 - D(p[2]))); // OBJ starts at the bottom, WPF at the top
                    break;
                case "vn":
                    normals.Add(new Vector3D(D(p[1]), D(p[2]), D(p[3])));
                    break;
                case "usemtl":
                    currentName = p.Length > 1 ? p[1] : "";
                    break;
                case "f":
                {
                    var part = Part(currentName);
                    var corners = new List<int>();
                    for (var i = 1; i < p.Length; i++)
                    {
                        var idx = p[i].Split('/');
                        var key = (Resolve(idx[0], positions.Count),
                                   idx.Length > 1 ? Resolve(idx[1], uvs.Count) : -1,
                                   idx.Length > 2 ? Resolve(idx[2], normals.Count) : -1);
                        if (!part.Index.TryGetValue(key, out var vertex))
                        {
                            vertex = part.Mesh.Positions.Count;
                            part.Mesh.Positions.Add(positions[key.Item1]);
                            part.Mesh.TextureCoordinates.Add(key.Item2 >= 0 ? uvs[key.Item2] : new Point());
                            if (key.Item3 >= 0) part.Mesh.Normals.Add(normals[key.Item3]);
                            part.Index[key] = vertex;
                        }
                        corners.Add(vertex);
                    }
                    for (var i = 1; i + 1 < corners.Count; i++) // fan triangulation for quads/polygons
                    {
                        part.Mesh.TriangleIndices.Add(corners[0]);
                        part.Mesh.TriangleIndices.Add(corners[i]);
                        part.Mesh.TriangleIndices.Add(corners[i + 1]);
                    }
                    break;
                }
            }
        }

        var group = new Model3DGroup();
        foreach (var (name, part) in parts)
        {
            if (part.Mesh.Normals.Count != part.Mesh.Positions.Count) part.Mesh.Normals.Clear(); // let WPF compute them
            part.Mesh.Freeze();
            Material material;
            if (materials.TryGetValue(name, out var m) && m.Built != null) material = m.Built;
            else
            {
                material = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));
                material.Freeze();
            }
            group.Children.Add(new GeometryModel3D(part.Mesh, material));
        }
        return (group, group.Bounds);
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
