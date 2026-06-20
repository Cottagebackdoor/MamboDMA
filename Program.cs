using System;
using MamboDMA.Games;
using MamboDMA.Games.ABI;
using MamboDMA.Games.CS2;
using MamboDMA.Games.DayZ;
using MamboDMA.Games.Example;
using MamboDMA.Games.Reforger;
using MamboDMA.Services;
using Raylib_cs;
using static MamboDMA.Misc;
using static MamboDMA.OverlayUI;
// using static MamboDMA.OverlayUI; // ← remove this

namespace MamboDMA
{
    internal static class Program
    {
        private enum UiChoice { Advanced, Simple, Game }

        private static UiChoice AskUiChoice(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i].Trim().ToLowerInvariant();
                if (a is "--ui" or "-u")
                {
                    if (i + 1 < args.Length)
                    {
                        var val = args[i + 1].Trim().ToLowerInvariant();
                        if (val.StartsWith("adv")) return UiChoice.Advanced;
                        if (val.StartsWith("simp")) return UiChoice.Simple;
                        if (val.StartsWith("game")) return UiChoice.Game;
                    }
                }
                else if (a is "advanced" or "adv") return UiChoice.Advanced;
                else if (a is "simple" or "simp") return UiChoice.Simple;
                else if (a is "game") return UiChoice.Game;
            }

            // Optional: skip prompt if no console (keeps Advanced as default)
            return UiChoice.Game;
        }

        private static (string title, Action draw) ResolveUi(UiChoice choice)
            => choice switch
            {
                UiChoice.Simple   => ("MamboDMA · Simple",   ServiceDemoUI.Draw),
                UiChoice.Advanced => ("MamboDMA · Advanced", OverlayUI.Draw),
                UiChoice.Game     => ("MamboDMA · Game", () => GameSelector.Draw()),
            };

        private static void Main(string[] args)
        {
            JobSystem.Start(workers: 3);
            var choice = AskUiChoice(args);
            var (title, drawLoop) = ResolveUi(UiChoice.Game);

            using var win = new OverlayWindow(title, 1100, 700);
            OverlayWindowApi.Bind(win);

            // Register all game plugins here:
            GameRegistry.Register(new ReforgerGame());
            GameRegistry.Register(new DayZGame()); 
            GameRegistry.Register(new ExampleGame());
            GameRegistry.Register(new ABIGame());
            GameRegistry.Register(new CS2Game());
            // GameRegistry.Register(new SomeOtherGame());
            // GameRegistry.Register(new YetAnotherGame());

            // Optional default selection (no Start() happens here):
            // If you want no default, just skip this and the combo shows the first name.
            //GameRegistry.Select("ExampleGame");
            
            Image icon = Raylib.LoadImage("Assets/Img/Logo.png");
            Raylib.SetWindowIcon(icon);
            Raylib.UnloadImage(icon);
            Win32IconHelper.SetWindowIcons("Assets/Img/Logo.ico");

            try { win.Run(drawLoop); }
            finally
            {
                try { DayZUpdater.Stop(); } catch { }
                try { VmmService.DisposeVmm(); } catch { }
                try { JobSystem.Stop(); } catch { }
            }
        }
    }
}
