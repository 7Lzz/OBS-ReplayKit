param(
    [ValidateSet('clip', 'recording-started', 'recording-stopped')][string]$Kind = 'clip',
    [ValidateRange(1, 600)][int]$Seconds = 90,
    [ValidateRange(500, 10000)][int]$DurationMs = 3200
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

    $message = switch ($Kind) {
        'clip' { "Saved the last ${Seconds}s" }
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
  <Border CornerRadius="0,16,16,0"
          Background="#E61D1F26"
          BorderBrush="#343844"
          BorderThickness="0,1,1,1">
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

      <Border Grid.Column="0"
              Background="#E015171D"
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
                   Foreground="#F2F4F8"
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

    $workArea = [System.Windows.SystemParameters]::WorkArea
    $messageText.Measure([System.Windows.Size]::new([double]::PositiveInfinity, [double]::PositiveInfinity))
    $textWidth = [Math]::Ceiling($messageText.DesiredSize.Width)
    $targetWidth = 104 + 30 + $textWidth + 40
    $window.Width = [Math]::Min([Math]::Max(360, $targetWidth), $workArea.Width)
    $window.Left = $workArea.Left
    $window.Top = $workArea.Top + 56
    $window.Opacity = 0

    $window.Add_MouseLeftButtonUp({
        $window.Close()
    })
    $window.Add_Closed({
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvokeShutdown(
            [System.Windows.Threading.DispatcherPriority]::Background
        )
    })

    $fadeIn = [System.Windows.Media.Animation.DoubleAnimation]::new(
        0.0, 1.0, [TimeSpan]::FromMilliseconds(140)
    )
    $fadeOut = [System.Windows.Media.Animation.DoubleAnimation]::new(
        1.0, 0.0, [TimeSpan]::FromMilliseconds(220)
    )
    $fadeOut.Add_Completed({
        $window.Close()
    })

    $timer = [System.Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds([Math]::Max(700, $DurationMs - 220))
    $timer.Add_Tick({
        $timer.Stop()
        $window.BeginAnimation([System.Windows.Window]::OpacityProperty, $fadeOut)
    })

    [void]$window.Show()
    $window.BeginAnimation([System.Windows.Window]::OpacityProperty, $fadeIn)
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::Run()
} catch {
    exit 0
}
