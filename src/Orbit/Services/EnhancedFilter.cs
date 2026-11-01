using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Orbit.Models;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Orbit.Services;

/// <summary>
/// Enhanced mode of the game filter: Orbit's own GPU filter. It reads what the screen shows where the
/// Roblox window is (Windows' desktop duplication – like screen recording, Roblox is never touched),
/// runs a shader over it (curves, vibrance, sharpen, clarity, HDR toning, bloom, the color matrix,
/// vignette, letterbox) and shows the result in a click-through window exactly over the game.
/// That window is left out of screen captures, so it never reads its own picture.
/// Everything runs on one background thread.
/// </summary>
public sealed class EnhancedFilter
{
    public static EnhancedFilter Instance { get; } = new();

    /// <summary>"Running", "Off" or why it couldn't start.</summary>
    public string Status { get; private set; } = "Off";
    /// <summary>True while it draws the game (the normal color filter must stay off then).</summary>
    public bool Running { get; private set; }
    /// <summary>It failed and won't try again until the setting is switched on again.</summary>
    public bool Failed { get; private set; }

    private volatile IntPtr _target;
    private volatile Constants? _constants;
    private Thread? _thread;

    /// <summary>Draw the filter over this game window (call often; IntPtr.Zero = stop drawing).</summary>
    public void Update(IntPtr game, GameFilterProfile? profile)
    {
        if (Failed) return;
        _target = profile == null ? IntPtr.Zero : game;
        if (profile != null) _constants = Constants.From(profile);
        if (_target != IntPtr.Zero && _thread == null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Orbit enhanced filter", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>The setting was switched on again: allow another try after a failure.</summary>
    public void ResetFailure()
    {
        if (!Failed) return;
        Failed = false;
        Status = "Off";
    }

    public void Stop() => _target = IntPtr.Zero;

    // =================== Render thread ===================

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _cb;
    private IDXGIFactory2? _factory;
    private IDCompositionDevice? _comp;
    private IDCompositionTarget? _compTarget;
    private IDCompositionVisual? _visual;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _source;
    private ID3D11ShaderResourceView? _sourceView;
    private IDXGIOutputDuplication? _dup;
    private RawRect _dupBounds;
    private IntPtr _window;
    private RawRect _shownRect;
    private uint _adapterIndex;
    private uint _width, _height;
    private WndProc? _wndProc;

    private void Run()
    {
        FilterLog.Write("enhanced: starting");
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // real pixels on every monitor
            CreateWindow();
        }
        catch (Exception ex)
        {
            // Without the window there's no way at all – stop for good.
            Fail(ex.Message);
            Release();
            _thread = null;
            return;
        }

        // Anything else (Windows pausing the screen capture, the graphics driver resetting, the game
        // window changing) is temporary: start the graphics again after a short pause.
        var errorsInRow = 0;
        while (true)
        {
            try
            {
                Loop();
            }
            catch (Exception ex)
            {
                errorsInRow++;
                var wait = TimeSpan.FromMilliseconds(Math.Min(10_000, 300 * Math.Pow(2, Math.Min(errorsInRow - 1, 6))));
                FilterLog.Write($"enhanced: error ({ex.GetType().Name}: {ex.Message}) – trying again in {wait.TotalSeconds:0.#} s (#{errorsInRow})");
                Running = false;
                Status = "Retrying: " + ex.Message;
                ReleaseGraphics();
                if (_window != IntPtr.Zero) ShowWindow(_window, SW_HIDE);
                Thread.Sleep(wait);
                continue;
            }
        }
    }

    private int _frames, _timeouts;
    private DateTime _statsSince = DateTime.UtcNow, _lastNewFrame = DateTime.UtcNow, _lastRaise;

    private void Loop()
    {
        var lastShown = false;
        while (true)
        {
            Pump();
            var game = _target;
            if (game == IntPtr.Zero || !IsWindow(game))
            {
                if (lastShown) { ShowWindow(_window, SW_HIDE); lastShown = false; FilterLog.Write("enhanced: Roblox not in front – hidden"); }
                // Let go of the screen duplication while there's nothing to draw.
                _dup?.Dispose();
                _dup = null;
                Running = false;
                Status = "Waiting for Roblox";
                Thread.Sleep(60);
                continue;
            }

            // Where the game's picture is on the screen.
            if (!GetClientRect(game, out var client) || client.Right <= 0 || client.Bottom <= 0) { Thread.Sleep(30); continue; }
            var origin = new POINT();
            ClientToScreen(game, ref origin);
            var rect = new RawRect(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom);

            if (_device == null) CreateDevice(rect);
            EnsureSize((uint)client.Right, (uint)client.Bottom);
            EnsureDuplication(rect);
            if (!lastShown || !rect.Equals(_shownRect))
            {
                SetWindowPos(_window, HWND_TOPMOST, rect.Left, rect.Top, client.Right, client.Bottom, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                if (!rect.Equals(_shownRect)) FilterLog.Write($"enhanced: over Roblox at {rect.Left},{rect.Top} size {client.Right}×{client.Bottom}");
                _shownRect = rect;
                _lastRaise = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _lastRaise > TimeSpan.FromMilliseconds(200))
            {
                // Roblox in fullscreen keeps putting itself on top – stay above it, without taking the focus.
                SetWindowPos(_window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                _lastRaise = DateTime.UtcNow;
            }
            lastShown = true;
            LogStats();

            // Wait for a new picture of the screen.
            var result = _dup!.AcquireNextFrame(20, out var info, out var resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout) { _timeouts++; continue; }
            if (result.Failure)
            {
                // Desktop switched (UAC, lock screen, resolution, a game changing its display mode) – start the capture again.
                FilterLog.Write($"enhanced: screen capture interrupted ({result}) – starting it again");
                _dup.Dispose();
                _dup = null;
                Thread.Sleep(150);
                continue;
            }

            try
            {
                if (info.LastPresentTime != 0 || info.AccumulatedFrames > 0)
                {
                    using var desktop = resource.QueryInterface<ID3D11Texture2D>();
                    CopyGame(desktop, rect);
                }
            }
            finally
            {
                resource.Dispose();
                _dup.ReleaseFrame();
            }

            Draw();
            _frames++;
            _lastNewFrame = DateTime.UtcNow;
            if (!Running) FilterLog.Write("enhanced: running");
            Running = true;
            Status = "Running";
        }
    }

    /// <summary>Every 30 s: how many pictures were drawn – shows when the game stopped sending new ones.</summary>
    private void LogStats()
    {
        var since = DateTime.UtcNow - _statsSince;
        if (since < TimeSpan.FromSeconds(30)) return;
        FilterLog.Write($"enhanced: {_frames / since.TotalSeconds:0} pictures/s drawn, {_timeouts} waits" +
                        (DateTime.UtcNow - _lastNewFrame > TimeSpan.FromSeconds(3) ? " – NO NEW PICTURE FROM THE SCREEN FOR " + (DateTime.UtcNow - _lastNewFrame).TotalSeconds.ToString("0") + " s" : ""));
        _frames = _timeouts = 0;
        _statsSince = DateTime.UtcNow;
    }

    private void CopyGame(ID3D11Texture2D desktop, RawRect rect)
    {
        // Part of the game that is on this monitor, in the monitor's own coordinates.
        var left = Math.Max(rect.Left, _dupBounds.Left);
        var top = Math.Max(rect.Top, _dupBounds.Top);
        var right = Math.Min(rect.Right, _dupBounds.Right);
        var bottom = Math.Min(rect.Bottom, _dupBounds.Bottom);
        if (right <= left || bottom <= top) return;
        var box = new Box(left - _dupBounds.Left, top - _dupBounds.Top, 0, right - _dupBounds.Left, bottom - _dupBounds.Top, 1);
        _context!.CopySubresourceRegion(_source!, 0, (uint)(left - rect.Left), (uint)(top - rect.Top), 0, desktop, 0, box);
        _context.GenerateMips(_sourceView!);
    }

    private void Draw()
    {
        var c = _constants ?? Constants.From(new GameFilterProfile());
        c.Size = new Vector4(_width, _height, 1f / _width, 1f / _height);
        var mapped = _context!.Map(_cb!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(c.Data(), mapped.DataPointer, false);
        _context.Unmap(_cb!, 0);

        _context.OMSetRenderTargets(_rtv!);
        _context.RSSetViewport(0, 0, _width, _height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetShaderResource(0, _sourceView!);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetConstantBuffer(0, _cb);
        _context.Draw(3, 0);
        var presented = _swapChain!.Present(1, PresentFlags.None);
        if (presented.Failure)
            throw new InvalidOperationException($"showing the picture failed ({presented}, device {_device!.DeviceRemovedReason})");
    }

    // ---------------- Setup ----------------

    private void CreateDevice(RawRect game)
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        // The device must be on the graphics card that drives the game's monitor (for desktop duplication).
        _adapterIndex = AdapterFor(game);
        _factory.EnumAdapters1(_adapterIndex, out var adapter).CheckError();
        using (adapter)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            _device = device;
            _context = context;
            FilterLog.Write($"enhanced: graphics card #{_adapterIndex}: {adapter.Description1.Description}");
        }

        var vs = Compiler.Compile(Shader, "VS", "orbit-filter", "vs_4_0");
        var ps = Compiler.Compile(Shader, "PS", "orbit-filter", "ps_4_0");
        _vs = _device!.CreateVertexShader(vs.Span);
        _ps = _device.CreatePixelShader(ps.Span);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _cb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ConstantData>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _comp.CreateTargetForHwnd(_window, true, out var target).CheckError();
        _compTarget = target;
        _visual = _comp.CreateVisual();
    }

    private void EnsureSize(uint width, uint height)
    {
        if (width == _width && height == _height && _swapChain != null) return;
        _width = width;
        _height = height;

        _rtv?.Dispose();
        _sourceView?.Dispose();
        _source?.Dispose();

        if (_swapChain == null)
        {
            var desc = new SwapChainDescription1(width, height, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                Scaling.Stretch, SwapEffect.FlipSequential, AlphaMode.Premultiplied);
            _swapChain = _factory!.CreateSwapChainForComposition(_device!, desc);
            _visual!.SetContent(_swapChain).CheckError();
            _compTarget!.SetRoot(_visual).CheckError();
            _comp!.Commit().CheckError();
        }
        else
        {
            _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm).CheckError();
        }

        using (var back = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _rtv = _device!.CreateRenderTargetView(back);

        // The game's picture, with smaller copies (mipmaps) for blur-based effects.
        _source = _device!.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, width, height, 1, 0,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
        _sourceView = _device.CreateShaderResourceView(_source);
    }

    /// <summary>Desktop duplication of the monitor the game is on.</summary>
    private void EnsureDuplication(RawRect game)
    {
        var cx = (game.Left + game.Right) / 2;
        var cy = (game.Top + game.Bottom) / 2;
        if (_dup != null && cx >= _dupBounds.Left && cx < _dupBounds.Right && cy >= _dupBounds.Top && cy < _dupBounds.Bottom) return;
        _dup?.Dispose();
        _dup = null;

        _factory!.EnumAdapters1(_adapterIndex, out var adapter).CheckError();
        using (adapter)
        {
            for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
            {
                using (output)
                {
                    var bounds = output.Description.DesktopCoordinates;
                    if (cx < bounds.Left || cx >= bounds.Right || cy < bounds.Top || cy >= bounds.Bottom) continue;
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    _dup = output1.DuplicateOutput(_device!);
                    _dupBounds = bounds;
                    FilterLog.Write($"enhanced: capturing monitor {output.Description.DeviceName} ({bounds.Left},{bounds.Top} – {bounds.Right},{bounds.Bottom})");
                    return;
                }
            }
        }
        throw new InvalidOperationException("The Roblox window is on a monitor of another graphics card.");
    }

    /// <summary>The graphics card whose monitor shows the middle of the game.</summary>
    private uint AdapterFor(RawRect game)
    {
        var cx = (game.Left + game.Right) / 2;
        var cy = (game.Top + game.Bottom) / 2;
        for (uint a = 0; _factory!.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
                {
                    using (output)
                    {
                        var b = output.Description.DesktopCoordinates;
                        if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom) return a;
                    }
                }
            }
        }
        return 0;
    }

    private void CreateWindow()
    {
        _wndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "OrbitEnhancedFilter",
        };
        RegisterClassEx(ref cls);
        _window = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "OrbitEnhancedFilter", "Orbit filter", WS_POPUP, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new InvalidOperationException("Couldn't create the filter window.");
        SetLayeredWindowAttributes(_window, 0, 255, LWA_ALPHA);
        // Never part of screen captures – otherwise the filter would read its own picture again and again.
        if (!SetWindowDisplayAffinity(_window, WDA_EXCLUDEFROMCAPTURE))
            throw new InvalidOperationException($"Windows didn't allow leaving the filter out of screen captures (error {Marshal.GetLastWin32Error()}) – needs Windows 10 version 2004 or newer.");
        FilterLog.Write("enhanced: filter window ready");
    }

    private void Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void Fail(string reason)
    {
        Failed = true;
        Running = false;
        Status = "Couldn't start: " + reason;
        FilterLog.Write("enhanced: can't run on this PC – " + reason);
    }

    private void Release()
    {
        ReleaseGraphics();
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
    }

    /// <summary>Everything except the window – made again on the next try.</summary>
    private void ReleaseGraphics()
    {
        Running = false;
        _dup?.Dispose(); _dup = null;
        _rtv?.Dispose(); _rtv = null;
        _sourceView?.Dispose(); _sourceView = null;
        _source?.Dispose(); _source = null;
        _swapChain?.Dispose(); _swapChain = null;
        _visual?.Dispose(); _visual = null;
        _compTarget?.Dispose(); _compTarget = null;
        _comp?.Dispose(); _comp = null;
        _cb?.Dispose(); _cb = null;
        _sampler?.Dispose(); _sampler = null;
        _ps?.Dispose(); _ps = null;
        _vs?.Dispose(); _vs = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _factory?.Dispose(); _factory = null;
        _width = _height = 0;
        _shownRect = default;
    }

    // =================== Shader ===================

    private sealed class Constants
    {
        public Vector4 R0, R1, R2, R3, Tone, Detail, Frame, Size;

        public static Constants From(GameFilterProfile p)
        {
            var m = GameFilterMath.Build(p); // the linear part, same as the normal filter
            return new Constants
            {
                R0 = new Vector4(m[0], m[1], m[2], 0),
                R1 = new Vector4(m[5], m[6], m[7], 0),
                R2 = new Vector4(m[10], m[11], m[12], 0),
                R3 = new Vector4(m[20], m[21], m[22], 0),
                Tone = new Vector4(p.Shadows / 100f, p.Highlights / 100f, MathF.Pow(2, -p.Gamma / 100f), p.Vibrance / 100f),
                Detail = new Vector4(p.Sharpen / 100f, p.Clarity / 100f, p.HdrToning / 100f, p.Bloom / 100f),
                Frame = new Vector4(p.Vignette / 100f, p.Letterbox switch { "2.39" => 2.39f, "1.85" => 1.85f, _ => 0 }, 0, 0),
            };
        }

        public ConstantData Data() => new() { R0 = R0, R1 = R1, R2 = R2, R3 = R3, Tone = Tone, Detail = Detail, Frame = Frame, Size = Size };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConstantData { public Vector4 R0, R1, R2, R3, Tone, Detail, Frame, Size; }

    private const string Shader = """
        Texture2D src : register(t0);
        SamplerState samp : register(s0);
        cbuffer Params : register(b0)
        {
            float4 r0; float4 r1; float4 r2; float4 r3;
            float4 tone;   // shadows, highlights, gamma exponent, vibrance
            float4 detail; // sharpen, clarity, hdr toning, bloom
            float4 frame;  // vignette, letterbox ratio
            float4 size;   // width, height, 1/width, 1/height
        };

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint id : SV_VertexID)
        {
            VSOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

        float4 PS(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float2 t = size.zw;
            float3 c = src.SampleLevel(samp, uv, 0).rgb;

            // Sharpen: push the pixel away from the average of its neighbours.
            if (detail.x > 0)
            {
                float3 n = src.SampleLevel(samp, uv + float2(t.x, 0), 0).rgb + src.SampleLevel(samp, uv - float2(t.x, 0), 0).rgb
                         + src.SampleLevel(samp, uv + float2(0, t.y), 0).rgb + src.SampleLevel(samp, uv - float2(0, t.y), 0).rgb;
                c += (c - n * 0.25) * detail.x * 1.6;
            }

            // Clarity: more contrast in the details (compared to a blurred copy).
            float3 near = src.SampleLevel(samp, uv, 3).rgb;
            c += (luma(c) - luma(near)) * detail.y * 1.4;

            // HDR toning: lifts dark areas and tames bright ones, judged by the wider surroundings.
            float wide = luma(src.SampleLevel(samp, uv, 5).rgb);
            c += (0.5 - wide) * detail.z * 0.45 * (1.0 - abs(luma(c) - 0.5));

            // Bloom: bright areas glow into their surroundings.
            float3 glow = (src.SampleLevel(samp, uv, 4).rgb + src.SampleLevel(samp, uv, 6).rgb) * 0.5;
            c += max(glow - 0.55, 0) * detail.w * 1.8;
            c = saturate(c);

            // Shadows and highlights.
            float l = luma(c);
            float sh = 1.0 - smoothstep(0.0, 0.55, l);
            float hi = smoothstep(0.45, 1.0, l);
            c = saturate(c + (tone.x * sh + tone.y * hi) * 0.3);

            // Gamma.
            c = pow(max(c, 0.0001), tone.z);

            // Exposure, contrast, saturation, temperature, tint and look – the same matrix as the normal filter.
            c = c.r * r0.rgb + c.g * r1.rgb + c.b * r2.rgb + r3.rgb;

            // Vibrance: dull colors get more color than already colorful ones.
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float lv = luma(c);
            c = lerp(lv.xxx, c, 1.0 + tone.w * (1.0 - saturate(mx - mn)));

            // Vignette.
            float r = length(uv - 0.5) / 0.7071;
            c *= 1.0 - frame.x * 0.95 * smoothstep(0.45, 1.0, r);

            // Letterbox.
            if (frame.y > 0)
            {
                float visible = (size.x / frame.y) / size.y;
                float bar = (1.0 - visible) * 0.5;
                if (uv.y < bar || uv.y > 1.0 - bar) c = 0;
            }
            return float4(saturate(c), 1);
        }
        """;

    // =================== Win32 ===================

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint WS_POPUP = 0x80000000, LWA_ALPHA = 0x2, WDA_EXCLUDEFROMCAPTURE = 0x11, PM_REMOVE = 0x1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
