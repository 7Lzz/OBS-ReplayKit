namespace ReplayKitShared
{
    // the ranges and allowed values the installer prefs (Prefs.cs) and the helper normalizer (ReplaykitSettings.cs) both hold a setting to; the helper also writes the ranges into the pages as RK_SETTING_LIMITS
    public static class SettingLimits
    {
        public const int ReplaySecondsMin = 5;
        public const int ReplaySecondsMax = 1200;
        public const int FpsMin = 1;
        public const int FpsMax = 240;
        public const int OverlayOpacityMin = 0;
        public const int OverlayOpacityMax = 100;
        public const int OverlayScaleMin = 50;
        public const int OverlayScaleMax = 200;
        public const int OverlayRotationMin = -180;
        public const int OverlayRotationMax = 180;
        public const double OverlayHueShiftMin = -180.0;
        public const double OverlayHueShiftMax = 180.0;
        public const double MotionBlurStrengthMin = 0.0;
        public const double MotionBlurStrengthMax = 1.0;
        // the clip saved and recording sounds
        public const int SoundVolumeMin = 0;
        public const int SoundVolumeMax = 100;
        public const int DiscordProjectorWidthMax = 7680;
        public const int DiscordProjectorHeightMax = 4320;
        public const int DiscordProjectorMonitorIndexMax = 64;
        public const int DiscordProjectorVisiblePixelsMax = 32;
        public const int DiscordProjectorTitleHintMaxLength = 128;
        public const int ScreenshareGameWindowMaxLength = 512;

        public static readonly string[] OverlayStyles = { "input_overlay", "bongo_cat", "off" };
        // the modes of the Bango Cat folder, by their folder names
        public static readonly string[] BongoModes = { "standard", "keyboard", "feixue", "mania" };
        public static readonly string[] CompressionModes = { "lower_gpu", "balanced", "smaller_files", "tiniest" };
        public static readonly string[] DiscordProjectorEdges = { "right", "left", "top", "bottom" };
        public static readonly string[] ScreenshareCaptureModes = { "hybrid_auto", "desktop", "game_auto", "game_window" };
    }
}
