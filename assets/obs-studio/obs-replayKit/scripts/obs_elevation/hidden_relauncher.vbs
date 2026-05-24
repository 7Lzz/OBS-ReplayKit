Option Explicit

Dim fso, shell, wmi, obsPath, existingObsPid, obsDir, handoffPath
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
Set wmi = GetObject("winmgmts:\\.\root\cimv2")

rem two invocation modes: shellexecuteex fallback (the original flow). the lua passes "<obspath> <pid>" on the command line, the uac prompt fires, and the elevated wscript receives both args directly. scheduled-task flow (the no-uac flow). schtasks /run cant forward arguments to the task action, so the lua writes the pair into %temp%\obsreplaykit_elevate.txt right before calling schtasks. the task action invokes this script with no args, so we look for that handoff file as the fallback. format is two lines, plain ascii: <absolute path to obs64.exe> <integer pid of the running obs to terminate> missing/malformed handoff file falls thru to the hardcoded defaults below, which is intentionally degraded behaviour: we still launch obs, we just cant terminate any specific stale instance first.
handoffPath = shell.ExpandEnvironmentStrings("%TEMP%") & "\obsreplaykit_elevate.txt"

If WScript.Arguments.Count >= 1 Then
  obsPath = WScript.Arguments.Item(0)
ElseIf fso.FileExists(handoffPath) Then
  obsPath = ReadHandoffLine(handoffPath, 1)
End If
If Len(obsPath) = 0 Then
  obsPath = "C:\Program Files\obs-studio\bin\64bit\obs64.exe"
End If

If WScript.Arguments.Count >= 2 Then
  existingObsPid = CLng(WScript.Arguments.Item(1))
ElseIf fso.FileExists(handoffPath) Then
  existingObsPid = TryCLng(ReadHandoffLine(handoffPath, 2), 0)
Else
  existingObsPid = 0
End If

rem delete the handoff file as soon as weve consumed it -- the values are stale the instant this script terminates, and a leftover file would feed the wrong pid into the next launch if the user manually invoked the task. best-effort; suppress errors if it was already cleaned up or is locked.
On Error Resume Next
If fso.FileExists(handoffPath) Then fso.DeleteFile handoffPath, True
On Error GoTo 0

Function ReadHandoffLine(ByVal path, ByVal lineNum)
  Dim ts, i, line
  ReadHandoffLine = ""
  On Error Resume Next
  Set ts = fso.OpenTextFile(path, 1, False, 0) ' forreading, ascii
  If Err.Number <> 0 Then Err.Clear: Exit Function
  On Error GoTo 0
  i = 0
  Do Until ts.AtEndOfStream
    i = i + 1
    line = ts.ReadLine
    If i = lineNum Then
      ts.Close
      ReadHandoffLine = Trim(line)
      Exit Function
    End If
  Loop
  ts.Close
End Function

Function TryCLng(ByVal value, ByVal fallback)
  On Error Resume Next
  TryCLng = CLng(value)
  If Err.Number <> 0 Then
    Err.Clear
    TryCLng = fallback
  End If
  On Error GoTo 0
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
