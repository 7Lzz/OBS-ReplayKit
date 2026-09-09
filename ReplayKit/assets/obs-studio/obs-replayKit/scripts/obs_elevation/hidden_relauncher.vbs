Option Explicit

Dim fso, shell, wmi, obsPath, existingObsPid, obsDir
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
Set wmi = GetObject("winmgmts:\\.\root\cimv2")

rem invoked directly by the uac-elevated wscript.exe call with "<obspath> <pid>": <absolute path to obs64.exe> <integer pid of the running obs to terminate>. a missing arg falls thru to the hardcoded defaults below, which is intentionally degraded behaviour: we still launch obs, we just cant terminate any specific stale instance first.
If WScript.Arguments.Count >= 1 Then
  obsPath = WScript.Arguments.Item(0)
End If
If Len(obsPath) = 0 Then
  obsPath = FindObsPath()
End If

If WScript.Arguments.Count >= 2 Then
  existingObsPid = CLng(WScript.Arguments.Item(1))
Else
  existingObsPid = 0
End If

Function CleanExePath(ByVal value)
  Dim s, comma
  s = Trim(CStr(value))
  If Left(s, 1) = """" Then s = Mid(s, 2)
  If Right(s, 1) = """" Then s = Left(s, Len(s) - 1)
  comma = InStr(1, s, ",")
  If comma > 0 Then s = Left(s, comma - 1)
  CleanExePath = Trim(s)
End Function

Function ObsPathFromRoot(ByVal root)
  If Len(root) = 0 Then
    ObsPathFromRoot = ""
  Else
    ObsPathFromRoot = fso.BuildPath(root, "bin\64bit\obs64.exe")
  End If
End Function

Function FindObsPath()
  Dim value, candidate, roots, i
  FindObsPath = "C:\Program Files\obs-studio\bin\64bit\obs64.exe"

  On Error Resume Next
  value = shell.ExpandEnvironmentStrings("%OBS_REPLAYKIT_OBS_EXE%")
  On Error GoTo 0
  candidate = CleanExePath(value)
  If fso.FileExists(candidate) Then FindObsPath = candidate: Exit Function

  On Error Resume Next
  value = shell.ExpandEnvironmentStrings("%OBS_REPLAYKIT_OBS_DIR%")
  On Error GoTo 0
  candidate = ObsPathFromRoot(CleanExePath(value))
  If fso.FileExists(candidate) Then FindObsPath = candidate: Exit Function

  On Error Resume Next
  value = shell.RegRead("HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe\")
  If Err.Number <> 0 Then Err.Clear: value = ""
  On Error GoTo 0
  candidate = CleanExePath(value)
  If fso.FileExists(candidate) Then FindObsPath = candidate: Exit Function

  On Error Resume Next
  value = shell.RegRead("HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe\")
  If Err.Number <> 0 Then Err.Clear: value = ""
  On Error GoTo 0
  candidate = CleanExePath(value)
  If fso.FileExists(candidate) Then FindObsPath = candidate: Exit Function

  roots = Array( _
    shell.ExpandEnvironmentStrings("%ProgramFiles%") & "\obs-studio", _
    shell.ExpandEnvironmentStrings("%ProgramW6432%") & "\obs-studio", _
    shell.ExpandEnvironmentStrings("%ProgramFiles(x86)%") & "\obs-studio" _
  )
  For i = 0 To UBound(roots)
    candidate = ObsPathFromRoot(CleanExePath(roots(i)))
    If fso.FileExists(candidate) Then FindObsPath = candidate: Exit Function
  Next
End Function

WScript.Sleep 300

Function NormalizedPath(ByVal value)
  On Error Resume Next
  NormalizedPath = LCase(fso.GetAbsolutePathName(value))
  If Err.Number <> 0 Then
    Err.Clear
    NormalizedPath = LCase(CStr(value))
  End If
  On Error GoTo 0
End Function

Sub StopLaunchingObs()
  Dim query, procs, proc, expected, actual, i
  If existingObsPid <= 0 Then Exit Sub

  expected = NormalizedPath(obsPath)
  query = "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE ProcessId=" & CStr(existingObsPid)
  On Error Resume Next
  Set procs = wmi.ExecQuery(query)
  If Err.Number <> 0 Then
    Err.Clear
    Exit Sub
  End If
  On Error GoTo 0

  For Each proc In procs
    actual = NormalizedPath(proc.ExecutablePath)
    If actual = expected Then
      On Error Resume Next
      proc.Terminate 0
      On Error GoTo 0
      For i = 1 To 50
        WScript.Sleep 100
        If wmi.ExecQuery(query).Count = 0 Then Exit For
      Next
    End If
  Next
End Sub

Sub DeletePathIfExists(ByVal path)
  On Error Resume Next
  If fso.FileExists(path) Then fso.DeleteFile path, True
  If fso.FolderExists(path) Then fso.DeleteFolder path, True
  Err.Clear
  On Error GoTo 0
End Sub

Sub ClearSentinel(ByVal folderPath)
  On Error Resume Next
  If fso.FolderExists(folderPath) Then
    fso.DeleteFile fso.BuildPath(folderPath, "*"), True
    fso.DeleteFolder fso.BuildPath(folderPath, "*"), True
  End If
  Err.Clear
  On Error GoTo 0
End Sub

StopLaunchingObs
DeletePathIfExists shell.ExpandEnvironmentStrings("%APPDATA%") & "\obs-studio\safe_mode"
ClearSentinel shell.ExpandEnvironmentStrings("%APPDATA%") & "\obs-studio\.sentinel"

obsDir = fso.GetParentFolderName(obsPath)
If Len(obsDir) > 0 Then shell.CurrentDirectory = obsDir
shell.Run """" & obsPath & """ --background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays", 1, False
