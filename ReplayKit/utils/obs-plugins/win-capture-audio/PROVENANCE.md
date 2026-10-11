# win-capture-audio, the ReplayKit build

The two audio sources of the ReplayKit scene, "Desktop Audio (excl. Discord)" and "Discord Audio (record only)", are `audio_capture` sources of the OBS plugin win-capture-audio. This folder holds the source of the build of that plugin ReplayKit ships and the script that makes it. The result is `ReplayKit/assets/win-capture-audio.zip`, which the installer extracts into the OBS folder (`ReplayKitSetup/WinCapture.cs`).

## Why it is not the old release

Until 2026-10-02 ReplayKit shipped win-capture-audio 2.2.3-beta (bozbez, July 2022, abandoned). Three OBS crashes on that machine (8/31, 9/1, 9/27) have one signature: an access violation on a Windows audio thread jumping through a freed object, with the plugin's threads in every report. The cause is in `AudioCaptureHelper::InitClient()` of that version: the object Windows calls back when an application-audio activation finishes (`CompletionHandler`, a COM object) lives on the stack of the function that waits for it, and is gone when Windows still uses it. A fix for it, and for two other crash bugs, has sat unmerged as pull request #240 of the original project since April 2023.

The source here is the maintained fork by Ayrton09, which has the same fix with the reason written next to it, a bounded wait, exceptions caught in every thread, and more (see its CHANGELOG).

## Why rk2

rk1 (2026-10-02) ended the startup crash of the old release. On 2026-10-06 the same machine had two more failures in one obs session, and the code behind both is in this source too.

The first is on record twice: in the obs crash report of 19:44:40 and in ReplayKit's own fault recorder (`%APPDATA%\obs-studio\crashes\replaykit\replaykit-native.jsonl`, 19:44:39, the same thread). It is an access violation (c0000005) in `memmove`, called from `Mixer::SubmitPacket`, called from `AudioCaptureHelper::ForwardPacket` on the capture thread of one program. The packet is copied out of the buffer of a Windows capture client, and that buffer belongs to the audio service: it takes it away when the captured program goes, when another program takes the device over, or when the device changes. A read that is in progress at that moment faults, and nothing in the plugin catches an access violation. The same obs process went on logging until 19:57:47, and its Application Error event is a heap corruption fast fail (c0000374, ntdll) stamped 19:57:44.

Patch 3 handles the first failure. Patch 4 removes the most likely source of the second: callback objects of the session monitor that were members of their owners although Windows holds them by pointer.

Neither failure could be provoked on demand (see the testing paragraph below), so the fixes rest on the crash records and on reading the source, and the guard itself was tested on its own.

## What is here

| path | what |
| --- | --- |
| `upstream/` | 32 files, byte for byte as git stores them, of https://github.com/Ayrton09/win-capture-audio at commit `d11b381d41333964735a57d60bda54747f2232d9` (tag 2.3.4, root tree `fd06520a6586a6947bcd186b03a82fac070c477c`): `CMakeLists.txt`, `LICENSE`, `src/`, `data/locale/`, `installer/installer.iss.in` (the cmake file reads it) |
| `MANIFEST.sha256` | the sha256 of every file of `upstream/`; the build refuses to run when one differs, is missing or is extra |
| `patches/` and `PATCHES.sha256` | the four ReplayKit patches and their hashes, checked the same way |
| `build.ps1` | fetches the pinned dependencies, verifies everything, builds, checks the dll, packs the zip |
| `BUILD-RECORD.json` | what the zip in `assets/` was made of: the hashes of the manifest and the patches, the toolchain, the sha256 of the dll and of the zip, the modules the dll imports |

Left out on purpose: README, CHANGELOG, CI files, the issue templates and `cmake/bootstrap-obs-sdk.ps1` (it would only run when libobs is not found, and the build supplies libobs itself, so it can never run). The git attributes of this folder (`-text`) keep the vendored files and the patches free of line ending conversion, or the hashes would depend on the checkout.

## The patches

1. `0001-keep-exclude-mode-on-per-app-capture.patch` removes the "native exclude" shortcut of 2.3.0 from `AudioCapture::WorkerUpdate()`. With one excluded program playing, that shortcut switches to a single Windows client that captures everything except that one process tree. ReplayKit's exclude list names several programs (Discord, the obs processes, the cef pages, the helper) and Windows can leave out only one tree, so obs's own audio, the monitoring output of the share preview, would be captured again, and the capture would restart every time an excluded program starts or stops playing. Without the shortcut, exclude mode follows the sessions of every other program, as 2.2.x did. Everything else of the fork stays.
2. `0002-build-id.patch` changes one line of build metadata: the obs log line `[win-capture-audio] Version 2.3.4 (d11b381+rk2)` names the upstream commit and the revision of this patch set (rk2), so a running dll can be traced to its source. Raise rk when a patch changes.
3. `0003-guard-the-capture-buffer-read.patch` makes the copy out of the engine buffer in `Mixer::SubmitPacket` behind an SEH handler that takes only an access violation or in-page error whose address lies inside the buffer being read; a fault anywhere else stays the crash it is. A packet that could not be read ends that capture, and the capture starts over on a new client as it does after any other loss of the stream. `ForwardPacket` also stops reading a success code that carries no buffer (`AUDCLNT_S_BUFFER_EMPTY`) as a packet, and nothing of any kind can leave the capture thread or the worker thread of a mixer. The plugin is built without /EHa, so the handler sits in a function of its own with no object that has a destructor.
4. `0004-heap-allocated-wasapi-callbacks.patch` makes the three notification clients of the session monitor (devices, new sessions, events of one session) heap objects held in `ComPtr` and made with `Make<>()`, instead of members of the watchers that register them with Windows. A member dies with its watcher, and a callback or a release that Windows still has on its way then lands in freed memory. It is the mistake of the 2022 release (a COM object on the stack) in three more places.

## The build

`build.ps1` verifies the manifest and the patch hashes, then takes only pinned inputs: the libobs headers of obs-studio tag 28.0.0 (commit `09da3ac4397ee9b1431c9c97cd2c71484b240382`, so the module reports API 28.0.0 and loads on obs 28 to 32), the import library made from the exports of the installed `obs.dll`, and microsoft/wil at the commit upstream pins as its submodule (`f02a61c68590c22de2efeeaf881032419f6fef54`, an ancestor of wil master). Both checkouts are verified by commit and by a clean `git status`. The unmodified cmake file of the fork does the configuring, with the libobs package supplied. The dll is compiled with `/Brepro`, so the same inputs on the same toolchain give the same bytes: two builds of the unpatched source on 2026-10-02 gave the identical dll (sha256 `b076592553a461c0ce50af948fc407d601c3ba6f80032c06714d468b6b0ab815`).

The build then refuses a dll that imports anything outside the C runtime, kernel32, user32, ole32, oleaut32, advapi32, mmdevapi, ksuser, avrt, psapi, ntdll and obs, or that holds a url. `build.ps1 -Pristine` builds the exact upstream source with no patch into the work folder, for comparison. The pdb stays in `%LOCALAPPDATA%\ReplayKit\plugin-build\wca-build-replaykit\out\`; keep it, it symbolizes any crash dump of that dll.

## What was reviewed (2026-10-02)

Every source file of the commit was read line by line, and a scan of the whole tree found no network, process, registry, file or injection code in the plugin (the one `GetProcAddress` is `RtlGetVersion` for the Windows build check), no obfuscated data and no url outside the build files. The installer script of the fork is not used. Not reviewed: the audio quality and sync of the rewritten mixer, which can only be judged by listening.

## What was tested (2026-10-09)

The guard of patch 3 was built on its own with the flags of the plugin (/EHsc) and run in separate processes: a read that runs into an unreadable page, a read of a released page and a thousand such faults in a row were all taken and left the process healthy, and a fault outside the source range (a null destination) was not taken and ended the process with c0000005, so real memory damage stays visible.

For the plugin as a whole there is a throwaway rig: a portable copy of obs with its own config and the two audio sources of the ReplayKit scene, and a small native program that opens a render stream and plays noise at about -85 dBFS under a name the real scene excludes. The sources showed that noise at -84 dB, so the packets are real and not flagged silent, and real desktop audio of the machine came thru at the same level with rk1 and with rk2. In one run per build that program was ended abruptly about ten times a second, twelve copies at a time, for 400 seconds (4,060 endings with rk1, 3,895 with rk2); every ending is a session that comes and goes in the session monitor, and every capture helper is created and destroyed on the way. Neither build crashed, the plugin wrote no error line, and both obs copies closed normally in 0.3 seconds afterwards.

That shows the patched build is not worse, not that the fixes work: ending a program does not make the audio service take buffers away, and the failure needs that, or a device change, at the moment of a read, which cannot be caused from a user account without changing the audio settings of the machine. The rig is a throwaway and is not kept in the repository.

## Updating to a newer upstream

Read the diff between the two commits first. Then copy the files listed above from the new commit with `git cat-file blob` (no line ending conversion), regenerate `MANIFEST.sha256`, re-cut the four patches against the new files, set the new `$upstreamCommit` and `$wilCommit` (the gitlink of `deps/wil`) in `build.ps1`, raise rk, run `build.ps1 -Force`, and listen to both audio sources before shipping.

License: GPL-2.0 (see `upstream/LICENSE`). The zip carries the license text and a `SOURCE.txt` that points here.
