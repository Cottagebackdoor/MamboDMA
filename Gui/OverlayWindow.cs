// MamboDMA/OverlayWindow.cs
using System;
using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;
using static MamboDMA.Misc;

namespace MamboDMA;

public sealed class OverlayWindow : IDisposable
{
    private bool _running = true;
    public void Close() => _running = false; 
    private bool _useVsync = false;      // let user pick
    private int  _fpsCap   = 144;        // used when vsync is off
    public OverlayWindow(string title = "MamboDMA", int width = 1200, int height = 800)
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.UndecoratedWindow | ConfigFlags.AlwaysRunWindow);
        Raylib.InitWindow(width, height, title);
        Image icon = Raylib.LoadImage("Assets/Img/Logo.png"); // must be square, e.g. 256x256
        Raylib.SetWindowIcon(icon);
        Raylib.UnloadImage(icon);
        Win32IconHelper.SetWindowIcons("Assets/Img/Logo.ico");
        Misc.ApplyAll();
        // Overlay must paint over the taskbar; the work-area-respecting default leaves the bottom strip uncovered.
        Misc.CoverTaskbarPrimaryMonitor();
        Raylib.ClearWindowState(ConfigFlags.VSyncHint);
        Raylib.SetTargetFPS(_fpsCap);

        // ImGui context + renderer hookup
        rlImGui.Setup();

        // Load config (if present) then apply current settings
        StyleConfig.Load(); // AppData/Roaming/MamboDMA/Examples/config.json
        StyleConfig.ApplyToImGui(StyleConfig.Settings);

        // Fonts & base style
        SetupImGuiStyleAndFonts();
    }

    public void Run(Action drawUI)
    {
        while (_running && !Raylib.WindowShouldClose())
        {
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);

            rlImGui.Begin();
            DockspaceOverMainViewport();
            drawUI();               // your app UI
            rlImGui.End();

            // Multi-viewport support (lets panels float outside the main window)
            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }

            Raylib.EndDrawing();
        }
    }
    
    public void SetFramePacing(bool useVsync, int fpsCap)
    {
        _useVsync = useVsync;
        _fpsCap = fpsCap;
        UpdateFramePacing();
    }
    private void UpdateFramePacing()
    {
        if (_useVsync)
        {
            // vsync on: framerate matches monitor refresh
            Raylib.SetWindowState(ConfigFlags.VSyncHint);
            Raylib.SetTargetFPS(0); // let swap interval pace frames
        }
        else
        {
            Raylib.ClearWindowState(ConfigFlags.VSyncHint);
            Raylib.SetTargetFPS(Math.Max(0, _fpsCap)); // 0 = uncapped, or any cap you want
        }
    }

    public void Dispose()
    {
        try { rlImGui.Shutdown(); } catch { }
        try { if (!Raylib.WindowShouldClose()) Raylib.CloseWindow(); } catch { }
    }

    private static unsafe void SetupImGuiStyleAndFonts()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;

        // Optional multi-viewport polish:
        io.ConfigViewportsNoTaskBarIcon = true; // floating panels don't spam the taskbar
        // io.ConfigViewportsNoDecoration = false; // keep native drag/resize chrome

        ImGui.StyleColorsDark();

        var style = ImGui.GetStyle();
        style.WindowRounding = 8f;
        style.FrameRounding = 8f;
        style.GrabRounding = 8f;

        const float baseSize = 16f;
        if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        {
            style.WindowRounding = 6f; // (optional) slightly sharper with OS windows
            style.Colors[(int)ImGuiCol.WindowBg].W = 1.00f;
        }
        // Create native font config
        ImFontConfigPtr cfg = ImGuiNative.ImFontConfig_ImFontConfig();
        cfg.OversampleH = 3;
        cfg.OversampleV = 2;
        cfg.PixelSnapH = false;
        cfg.MergeMode = false;
        cfg.GlyphOffset = Vector2.Zero;

        // Load weights from your Assets/Fonts/static/
        Fonts.Regular = io.Fonts.AddFontFromFileTTF("Assets/Fonts/AlanSans-Medium.ttf", baseSize, cfg);
        Fonts.Medium  = io.Fonts.AddFontFromFileTTF("Assets/Fonts/AlanSans-SemiBold.ttf", baseSize, cfg);
        Fonts.Bold    = io.Fonts.AddFontFromFileTTF("Assets/Fonts/AlanSans-Bold.ttf", baseSize, cfg);

        cfg.Destroy();

        rlImGui.ReloadFonts();

        unsafe { io.NativePtr->FontDefault = Fonts.Regular.NativePtr; }
    }

    private static void DockspaceOverMainViewport()
    {
        var vp = ImGui.GetMainViewport();

        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        ImGui.SetNextWindowViewport(vp.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));

        var flags =
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoBackground;               // ← IMPORTANT

        // Optional: ensure WindowBg is fully transparent for this host
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));

        ImGui.Begin("##DockRoot", flags);
        ImGui.PopStyleVar(3);

        var dockspaceId = ImGui.GetID("MamboDockspace");
        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

        ImGui.End();
        ImGui.PopStyleColor(); // pop WindowBg
    }
    
}
