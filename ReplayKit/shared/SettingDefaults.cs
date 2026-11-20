namespace ReplayKitShared
{
    // the plain default of every setting the installer and the helper both know; the helper schema (GetDefaultSettings) and the installer prefs (Prefs.cs) read them from here, and defaults computed on the machine stay with their owner
    public static class SettingDefaults
    {
        public const string RecordingPreset = "balanced";
        public const string CompressionMode = "balanced";
        // h.264 plays everywhere; hevc stays a choice for smaller files that some phones, browsers and apps cannot play
        public const string CodecPreference = "h264";
        public const int ReplaySeconds = 90;
        // the overlay ships off -- the input-overlay and bongo cat plugins still install so Settings can switch to either, the scene just starts clean
        public const string OverlayStyle = "off";
        public const string BongoMode = "standard";
        public const int OverlayOpacity = 100;
        public const int OverlayScale = 100;
        public const int OverlayRotation = 0;
        public const bool OverlayFlipH = false;
        public const int OverlayHueShift = 0;
        public const string OverlayColorMultiply = "#ffffff";
        public const string OverlayColorAdd = "#000000";
        public const bool ObsStartupEnabled = true;
        public const bool DisableObsCloseWarning = true;
        public const bool CloseToTray = true;
        public const bool AllowSleepWhileActive = true;
        public const bool PinObsTrayIcon = true;
        public const bool ClipNotificationEnabled = true;
        public const bool RecordingNotificationEnabled = true;
        public const bool TrimPreciseDefault = true;
        public const bool DebugLoggingEnabled = false;
        // the pc audio, discord and the microphone as tracks of their own in every recording, next to the mixed track 1; on for a new install, and DefaultsMigration turns it on once for an install that existed before
        public const bool RecordingSeparateAudioTracks = true;
        public const int ClipSoundVolume = 25;
        public const int RecordingSoundVolume = 25;
        public const string MicDeviceId = "default";
        public const string ShareMode = "projector";
        public const bool DiscordScreenshareEnabled = false;
        public const string DiscordOutputMode = "projector";
        public const bool DiscordProjectorEnabled = true;
        public const int DiscordProjectorWidth = 0;
        public const int DiscordProjectorHeight = 0;
        public const int DiscordProjectorVisiblePixels = 1;
        public const int DiscordProjectorMonitorIndex = 0;
        public const string DiscordProjectorEdge = "bottom";
        public const string DiscordProjectorTitleHint = "OBS ReplayKit Discord Share";
        public const bool DiscordProjectorHideTaskbar = true;
        // how long the share window keeps showing its card once obs closed for real or crashed
        public const string ShareWindowKeep = "5m";
        // the card of the share window: its background, how an own image or video fills it, the colour of its text and of the box behind the text over an own image or video (#rrggbb, or #rrggbbaa with the opacity in the last byte as the shared colour picker gives it), whether an own video plays its own sound in place of the card sound, the card sound and how loud; the text itself has no constant since its default is the be right back message in the language of obs
        public const string ShareCoverBackground = "theme";
        public const string ShareCoverFit = "fit";
        public const string ShareCoverTextColor = "#ffffff";
        public const string ShareCoverBoxColor = "#00000096";
        public const bool ShareCoverVideoSound = true;
        public const string ShareCoverSound = "default";
        public const int ShareCoverSoundVolume = 25;
        public const string ScreenshareCaptureMode = "hybrid_auto";
        public const string ScreenshareGameWindow = "";
        public const bool ScreenshareAutoGameKeepFocused = true;
        // the game out of focus card stands in for a listed game that is open but not in front
        public const bool ScreenshareAutoGameOutOfFocus = false;
        public const bool MotionBlurEnabled = false;
        public const double MotionBlurStrength = 0.075;
    }
}
