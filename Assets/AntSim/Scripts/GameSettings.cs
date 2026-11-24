using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// Player settings: display, camera, interface and saving. Stored in PlayerPrefs, so they carry over between
    /// sessions and games, and shared by the start screen and the in-game menu (<see cref="Draw"/>).
    /// </summary>
    public static class GameSettings
    {
        const string Prefix = "AntSim.Settings.";

        // Display
        public static bool Fullscreen = true;
        public static bool VSync = true;
        public static int FpsCap = 0;                 // 0 = no cap (with VSync off)
        // Camera
        public static float PanSpeed = 1.2f;          // screen heights per second
        public static float ZoomSpeed = 1f;           // multiplier on each wheel step
        public static bool EdgePan = false;           // pan when the mouse touches the screen edge
        // Interface
        public static float UiScaleOffset = 0f;       // added to the automatic UI scale
        public static bool ShowStats = true;          // FPS / tick / ant counts in the top bar
        public static bool PauseInMenu = true;        // the Esc menu pauses the game
        // Saving
        public static int AutosaveMinutes = 5;        // 0 = off

        public static readonly int[] FpsCaps = { 0, 30, 60, 120, 144 };
        public static readonly int[] AutosaveChoices = { 0, 2, 5, 10 };

        static bool loaded;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            Fullscreen = GetBool("Fullscreen", Screen.fullScreen);
            VSync = GetBool("VSync", true);
            FpsCap = PlayerPrefs.GetInt(Prefix + "FpsCap", 0);
            PanSpeed = PlayerPrefs.GetFloat(Prefix + "PanSpeed", 1.2f);
            ZoomSpeed = PlayerPrefs.GetFloat(Prefix + "ZoomSpeed", 1f);
            EdgePan = GetBool("EdgePan", false);
            UiScaleOffset = PlayerPrefs.GetFloat("AntSim.UiScaleOffset", 0f); // older key, kept so A-/A+ choices survive
            ShowStats = GetBool("ShowStats", true);
            PauseInMenu = GetBool("PauseInMenu", true);
            AutosaveMinutes = PlayerPrefs.GetInt(Prefix + "AutosaveMinutes", 5);
        }

        public static void Save()
        {
            SetBool("Fullscreen", Fullscreen);
            SetBool("VSync", VSync);
            PlayerPrefs.SetInt(Prefix + "FpsCap", FpsCap);
            PlayerPrefs.SetFloat(Prefix + "PanSpeed", PanSpeed);
            PlayerPrefs.SetFloat(Prefix + "ZoomSpeed", ZoomSpeed);
            SetBool("EdgePan", EdgePan);
            PlayerPrefs.SetFloat("AntSim.UiScaleOffset", UiScaleOffset);
            SetBool("ShowStats", ShowStats);
            SetBool("PauseInMenu", PauseInMenu);
            PlayerPrefs.SetInt(Prefix + "AutosaveMinutes", AutosaveMinutes);
            PlayerPrefs.Save();
        }

        /// <summary>Puts the display settings into effect (window mode, VSync, frame cap).</summary>
        static float lastApply = -10f;

        public static void Apply()
        {
            lastApply = Time.unscaledTime;
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync || FpsCap <= 0 ? -1 : FpsCap;
            if (Application.isEditor) return; // the editor's Game view decides its own window
            if (Fullscreen && !Screen.fullScreen)
            {
                var r = Screen.currentResolution;
                Screen.SetResolution(r.width, r.height, FullScreenMode.FullScreenWindow);
            }
            else if (!Fullscreen && Screen.fullScreen)
            {
                var r = Screen.currentResolution;
                int w = Mathf.Max(960, r.width * 3 / 4), h = Mathf.Max(640, r.height * 3 / 4);
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
            }
        }

        public static void ResetToDefaults()
        {
            Fullscreen = true; VSync = true; FpsCap = 0;
            PanSpeed = 1.2f; ZoomSpeed = 1f; EdgePan = false;
            UiScaleOffset = 0f; ShowStats = true; PauseInMenu = true;
            AutosaveMinutes = 5;
            Save();
            Apply();
        }

        public static float ClampScaleOffset(float v) => Mathf.Clamp(v, -1f, 2f);

        static bool GetBool(string key, bool fallback) => PlayerPrefs.GetInt(Prefix + key, fallback ? 1 : 0) != 0;
        static void SetBool(string key, bool v) => PlayerPrefs.SetInt(Prefix + key, v ? 1 : 0);

        // ------------------------------------------------------------------ settings page

        /// <summary>Looks used by <see cref="Draw"/> (each screen passes its own).</summary>
        public class Styles
        {
            public UiStyle Header, Label, Small, Button, ButtonOn, Toggle;
        }

        /// <summary>Draws the settings page into the current UiKit container. Returns true when Back is pressed.</summary>
        public static bool Draw(UiKit ui, Styles s)
        {
            Load();
            bool changed = false, display = false;
            // Alt+Enter switches mode behind our back: show (and remember) what the window really is.
            if (!Application.isEditor && Screen.fullScreen != Fullscreen && Time.unscaledTime - lastApply > 1f)
            {
                Fullscreen = Screen.fullScreen;
                changed = true;
            }

            ui.Label("Display", s.Header);
            ui.BeginRow();
            if (ui.Button("Full screen", Fullscreen ? s.ButtonOn : s.Button)) { if (!Fullscreen) { Fullscreen = true; display = true; } }
            if (ui.Button("Window", !Fullscreen ? s.ButtonOn : s.Button)) { if (Fullscreen) { Fullscreen = false; display = true; } }
            ui.EndRow();
            bool vs = ui.Toggle(VSync, "VSync (smooth, matches the monitor's refresh rate)", s.Toggle);
            if (vs != VSync) { VSync = vs; display = true; }
            if (!VSync)
            {
                ui.Label("Frame rate cap", s.Label);
                ui.BeginRow();
                foreach (int cap in FpsCaps)
                    if (ui.Button(cap == 0 ? "None" : cap.ToString(), FpsCap == cap ? s.ButtonOn : s.Button)) { FpsCap = cap; display = true; }
                ui.EndRow();
            }
            ui.Label("Alt+Enter also switches between full screen and a window.", s.Small);

            ui.Space(8);
            ui.Label("Camera", s.Header);
            ui.Label($"Scroll speed (WASD / arrows): {PanSpeed / 1.2f:P0}", s.Label);
            float pan = Mathf.Round(ui.Slider(PanSpeed, 0.4f, 3f) * 10f) / 10f;
            if (!Mathf.Approximately(pan, PanSpeed)) { PanSpeed = pan; changed = true; }
            ui.Label($"Zoom speed (mouse wheel): {ZoomSpeed:P0}", s.Label);
            float zoom = Mathf.Round(ui.Slider(ZoomSpeed, 0.3f, 2.5f) * 10f) / 10f;
            if (!Mathf.Approximately(zoom, ZoomSpeed)) { ZoomSpeed = zoom; changed = true; }
            bool edge = ui.Toggle(EdgePan, "Scroll when the mouse touches the edge of the screen", s.Toggle);
            if (edge != EdgePan) { EdgePan = edge; changed = true; }

            ui.Space(8);
            ui.Label("Interface", s.Header);
            ui.Label($"Interface size: {(UiScaleOffset >= 0 ? "+" : "")}{UiScaleOffset * 100f:0}% (also A- / A+ in the top bar)", s.Label);
            float sc = Mathf.Round(ui.Slider(UiScaleOffset, -0.5f, 1f) * 20f) / 20f;
            if (!Mathf.Approximately(sc, UiScaleOffset)) { UiScaleOffset = ClampScaleOffset(sc); changed = true; }
            bool stats = ui.Toggle(ShowStats, "Show FPS, tick time and ant counts in the top bar", s.Toggle);
            if (stats != ShowStats) { ShowStats = stats; changed = true; }
            bool pause = ui.Toggle(PauseInMenu, "Pause the game while this menu is open", s.Toggle);
            if (pause != PauseInMenu) { PauseInMenu = pause; changed = true; }

            ui.Space(8);
            ui.Label("Saving", s.Header);
            ui.Label("Autosave", s.Label);
            ui.BeginRow();
            foreach (int m in AutosaveChoices)
                if (ui.Button(m == 0 ? "Off" : $"Every {m} min", AutosaveMinutes == m ? s.ButtonOn : s.Button)) { AutosaveMinutes = m; changed = true; }
            ui.EndRow();

            ui.Space(10);
            bool back = false;
            ui.BeginRow();
            if (ui.Button("Back", s.ButtonOn)) back = true;
            if (ui.Button("Reset to defaults", s.Button)) { ResetToDefaults(); changed = false; display = false; }
            ui.EndRow();

            if (changed || display) Save();
            if (display) Apply();
            return back;
        }

        /// <summary>Leaves the game (stops Play mode in the editor).</summary>
        public static void QuitToDesktop()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
