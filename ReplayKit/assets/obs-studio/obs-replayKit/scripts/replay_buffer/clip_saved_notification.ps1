param(
    [ValidateSet('clip', 'recording-started', 'recording-stopped')][string]$Kind = 'clip',
    [ValidateRange(1, 1200)][int]$Seconds = 90,
    [ValidateRange(500, 10000)][int]$DurationMs = 3200
)

$ErrorActionPreference = 'Stop'

# the hand-picked colours this popup shipped with, used whenever the live theme cannot be read -- so a helper hiccup degrades to the old look instead of a blank or half-coloured window.
$fallbackColors = @{
    Panel    = '#E61D1F26'
    IconWell = '#E015171D'
    Border   = '#FF343844'
    Text     = '#FFF2F4F8'
}

function Resolve-ThemeHex($value, $alphaHex, $fallback) {
    if ($value -and ($value -match '^#?[0-9a-fA-F]{6}$')) { return '#' + $alphaHex + $value.TrimStart('#') }
    return $fallback
}

# "90" -> "1m 30s", "120" -> "2m" (no dangling "0s"), "45" -> "45s" -- matches how the rest of the ui writes durations
function Format-ClipDuration([int]$totalSeconds) {
    if ($totalSeconds -lt 60) { return "${totalSeconds}s" }
    $minutes = [Math]::Floor($totalSeconds / 60)
    $remainder = $totalSeconds % 60
    if ($remainder -eq 0) { return "${minutes}m" }
    return "${minutes}m ${remainder}s"
}

# pulls the same resolved theme the dock pages and the tray plugins own popups use, so this one stops being the odd one out with hardcoded colours. per-field fallback (not just one big try/catch) so a single missing token cannot blank the whole popup, and the helper being unreachable just falls back to the original look entirely.
function Get-PopupThemeColors {
    try {
        $settings = Invoke-RestMethod -Uri 'http://127.0.0.1:8767/settings' -TimeoutSec 2 -ErrorAction Stop
        $m = $settings.menuColors
        if (-not $m) { return $fallbackColors }
        return @{
            Panel    = Resolve-ThemeHex $m.panel  'E6' $fallbackColors.Panel
            IconWell = Resolve-ThemeHex $m.tip    'E0' $fallbackColors.IconWell
            Border   = Resolve-ThemeHex $m.border 'FF' $fallbackColors.Border
            Text     = Resolve-ThemeHex $m.text   'FF' $fallbackColors.Text
        }
    } catch {
        return $fallbackColors
    }
}

function ConvertTo-Brush([string]$hex) {
    return New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
}

try {
    Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

    $message = switch ($Kind) {
        'clip' { "Saved the last " + (Format-ClipDuration $Seconds) }
        'recording-started' { 'Recording started' }
        'recording-stopped' { 'Recording stopped' }
    }
    $xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="420" Height="92"
        WindowStyle="None"
        ResizeMode="NoResize"
        AllowsTransparency="True"
        Background="Transparent"
        ShowInTaskbar="False"
        ShowActivated="False"
        Topmost="True">
  <Border x:Name="CardBorder"
          CornerRadius="0,16,16,0"
          BorderThickness="0,1,1,1">
    <Border.RenderTransform>
      <TranslateTransform x:Name="CardTranslate" X="0"/>
    </Border.RenderTransform>
    <Border.Effect>
      <DropShadowEffect BlurRadius="28"
                        ShadowDepth="6"
                        Direction="270"
                        Opacity="0.42"
                        Color="#000000"/>
    </Border.Effect>
    <Grid ClipToBounds="True">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="104"/>
        <ColumnDefinition Width="*"/>
      </Grid.ColumnDefinitions>

      <Border x:Name="IconWell"
              Grid.Column="0"
              CornerRadius="0">
        <Grid>
          <Image x:Name="ObsIcon"
                 Width="58"
                 Height="58"
                 Stretch="Uniform"
                 HorizontalAlignment="Center"
                 VerticalAlignment="Center"
                 RenderOptions.BitmapScalingMode="HighQuality"/>
        </Grid>
      </Border>

      <Grid Grid.Column="1" Margin="30,0,34,0">
        <TextBlock x:Name="MessageText"
                   VerticalAlignment="Center"
                   FontFamily="Segoe UI Semibold"
                   FontSize="26"
                   FontWeight="SemiBold"
                   TextTrimming="None"/>
      </Grid>
    </Grid>
  </Border>
</Window>
'@

    $xml = [xml]$xaml
    $reader = [System.Xml.XmlNodeReader]::new($xml)
    $window = [System.Windows.Markup.XamlReader]::Load($reader)
    $messageText = $window.FindName('MessageText')
    $messageText.Text = $message

    $theme = Get-PopupThemeColors
    $window.FindName('CardBorder').Background = ConvertTo-Brush $theme.Panel
    $window.FindName('CardBorder').BorderBrush = ConvertTo-Brush $theme.Border
    $window.FindName('IconWell').Background = ConvertTo-Brush $theme.IconWell
    $messageText.Foreground = ConvertTo-Brush $theme.Text

    $iconPath = Join-Path $PSScriptRoot 'obs-replaykit.ico'
    if (Test-Path -LiteralPath $iconPath) {
        $decoder = [System.Windows.Media.Imaging.IconBitmapDecoder]::new(
            [Uri]::new($iconPath),
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        )
        $frame = $decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
        if ($frame) {
            $window.FindName('ObsIcon').Source = $frame
        }
    }

    $cardTranslate = $window.FindName('CardTranslate')

    $workArea = [System.Windows.SystemParameters]::WorkArea
    $messageText.Measure([System.Windows.Size]::new([double]::PositiveInfinity, [double]::PositiveInfinity))
    $textWidth = [Math]::Ceiling($messageText.DesiredSize.Width)
    $targetWidth = 104 + 30 + $textWidth + 40
    $window.Width = [Math]::Min([Math]::Max(360, $targetWidth), $workArea.Width)
    # the real OS window is placed at its resting spot ONCE and never moved again -- a second monitor sitting to the left of the primary one lives at negative coordinates in the exact space "off the left edge" would occupy, so sliding Window.Left across that range drags the window onto the other monitor instead of staying invisible. the slide instead moves a RenderTransform on the content: the windows own rectangle never leaves the main monitor, so the content sliding out past its left edge is simply not drawn (a window only paints its own rect).
    $window.Left = $workArea.Left
    $window.Top = $workArea.Top + 56
    $window.Opacity = 0
    $cardTranslate.X = -$window.Width

    $slideIn = [System.Windows.Media.Animation.DoubleAnimation]::new(
        -$window.Width, 0.0, [TimeSpan]::FromMilliseconds(320)
    )
    $slideIn.EasingFunction = [System.Windows.Media.Animation.BackEase]::new()
    $slideIn.EasingFunction.Amplitude = 0.25
    $slideIn.EasingFunction.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut

    $slideOut = [System.Windows.Media.Animation.DoubleAnimation]::new(
        0.0, -$window.Width, [TimeSpan]::FromMilliseconds(220)
    )
    $slideOut.EasingFunction = [System.Windows.Media.Animation.CubicEase]::new()
    $slideOut.EasingFunction.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseIn

    $fadeIn = [System.Windows.Media.Animation.DoubleAnimation]::new(
        0.0, 1.0, [TimeSpan]::FromMilliseconds(140)
    )
    $fadeOut = [System.Windows.Media.Animation.DoubleAnimation]::new(
        1.0, 0.0, [TimeSpan]::FromMilliseconds(220)
    )
    $fadeOut.Add_Completed({
        $window.Close()
    })

    # shared by the auto-hide timer and click-to-dismiss so there is one exit path (and one Close() call site, on fadeOut)
    $beginExit = {
        $timer.Stop()
        $window.BeginAnimation([System.Windows.Window]::OpacityProperty, $fadeOut)
        $cardTranslate.BeginAnimation([System.Windows.Media.TranslateTransform]::XProperty, $slideOut)
    }

    $timer = [System.Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds([Math]::Max(700, $DurationMs - 220))
    $timer.Add_Tick($beginExit)

    $window.Add_MouseLeftButtonUp($beginExit)
    $window.Add_Closed({
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvokeShutdown(
            [System.Windows.Threading.DispatcherPriority]::Background
        )
    })

    [void]$window.Show()
    $window.BeginAnimation([System.Windows.Window]::OpacityProperty, $fadeIn)
    $cardTranslate.BeginAnimation([System.Windows.Media.TranslateTransform]::XProperty, $slideIn)
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::Run()
} catch {
    exit 0
}
