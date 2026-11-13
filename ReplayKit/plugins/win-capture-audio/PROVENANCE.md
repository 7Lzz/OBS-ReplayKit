# win-capture-audio, the ReplayKit build

The two audio sources of the ReplayKit scene, "Desktop Audio (excl. Discord)" and "Discord Audio (record only)", are `audio_capture` sources of the OBS plugin win-capture-audio. The build writes the plugin files to `ReplayKit/dist/win-capture-audio/`. The setup project packs them into `win-capture-audio.zip` inside the installer, which extracts them into the OBS folder (`apps/setup/WinCapture.cs`).

## Why it is not the old release

Until 2026-10-02 ReplayKit shipped win-capture-audio 2.2.3-beta (bozbez, July 2022, abandoned). Three OBS crashes on that machine (8/31, 9/1, 9/27) have one signature: an access violation on a Windows audio thread jumping through a freed object, with the plugin's threads in every report. The cause is in `AudioCaptureHelper::InitClient()` of that version: the object Windows calls back when an application-audio activation finishes (`CompletionHandler`, a COM object) lives on the stack of the function that waits for it, and is gone when Windows still uses it. A fix for it, and for two other crash bugs, has sat unmerged as pull request #240 of the original project since April 2023.

The source here is the maintained fork by Ayrton09, which has the same fix with the reason written next to it, a bounded wait, exceptions caught in every thread, and more (see its CHANGELOG).

## Why rk2

rk1 (2026-10-02) ended the startup crash of the old release. On 2026-10-06 the same machine had two more failures in one obs session, and the code behind both is in this source too.

The first is on record twice: in the obs crash report of 19:44:40 and in ReplayKit's own fault recorder (`%APPDATA%\obs-studio\crashes\replaykit\replaykit-native.jsonl`, 19:44:39, the same thread). It is an access violation (c0000005) in `memmove`, called from `Mixer::SubmitPacket`, called from `AudioCaptureHelper::ForwardPacket` on the capture thread of one program. The packet is copied out of the buffer of a Windows capture client, and that buffer belongs to the audio service: it takes it away when the captured program goes, when another program takes the device over, or when the device changes. A read that is in progress at that moment faults, and nothing in the plugin catches an access violation. The same obs process went on logging until 19:57:47, and its Application Error event is a heap corruption fast fail (c0000374, ntdll) stamped 19:57:44.

Patch 3 handles the first failure. Patch 4 removes the most likely source of the second: callback objects of the session monitor that were members of their owners although Windows holds them by pointer.

Neither failure could be provoked on demand (see the testing paragraph below), so the fixes rest on the crash records and on reading the source, and the guard itself was tested on its own.

## Why rk3

A user reported noise like a detuned TV on the PC audio track of recordings, on a machine that is not this one. Two candidates were checked. The first was the mixer, which places every packet at its own timestamp with no smoothing, so timestamps that wobble would leave gaps and doubled samples between packets. The probe saw no gaps or overlaps on this machine: all 998 packets were contiguous at 48 kHz, and the same held with Windows resampling to 44.1, 32 and 96 kHz.

The second is a real defect, fixed by patch 5. `WorkerUpdate` skips the sessions of obs itself, so obs is never in the excluded set, and `DeDuplicateCaptureList` only keeps the ancestors of excluded programs out of the capture. A program that started obs and has an audio session of its own (explorer, which launches obs, or a launcher) becomes a capture root, and Windows captures a root with its whole process tree, obs inside it. Desktop audio then records what obs plays: with the share preview on, obs monitors desktop audio to the share cable, so desktop audio is fed back into itself about every tenth of a second and builds up into noise; without it, the clip sound and the sound of the obs browser pages. Upstream avoids this with its native exclude client, which patch 1 takes out, so the fix belongs in this patch set. On this machine explorer held no session at the time of checking, so the loop could not be shown here.

## Why rk4

The capture helper forwarded packets even when WASAPI flagged their timestamps as unreliable. A far-future timestamp could reset the shared mix timeline, causing valid packets from all applications to be dropped as late. Patch 6 releases these packets without mixing them. A synthetic capture sequence reproduced the reset on rk3 and preserved both applications' valid samples on rk4.

## Why rk5

A timestamp just below its intended frame boundary placed the packet one frame early. This doubled a sample at one boundary and left a zero at the next. Patch 7 backports [upstream's timestamp rounding fix](https://github.com/Ayrton09/win-capture-audio/commit/bfcf2e9b950f86e6a0359bc3265acfa276cfc75a) from 2.3.5. A synthetic capture sequence reproduced the overlap and gap at 48 and 44.1 kHz on rk4 and preserved every sample on rk5.

## Why rk6

The same PC Audio artifact remained after rk5. A [stock OBS capture comparison](https://github.com/obsproject/obs-studio/issues/8064) also showed periodic crackle in the per-app track while the output-device track stayed clean. Patch 8 adds an optional backend that takes PC Audio from OBS's output-device source. Only one backend forwards samples at a time, and device audio is blocked whenever monitoring is on.

## Why rk7

Patch 9 backports [the second mixer timing fix in 2.3.5](https://github.com/Ayrton09/win-capture-audio/releases/tag/2.3.5). When the buffer drained, a late packet could rewind the timeline OBS had already received. Patch 10 tracks the expected next frame independently for each WASAPI capture, absorbs timestamp deviations of up to 2 ms for normal packets, preserves real gaps, and trims overlapping frames before the shared mix. A synthetic packet sequence reproduced the rewind and jitter artifacts on rk6 and passed on rk7 at 44.1, 48 and 96 kHz.

## Why rk8

A single forward timestamp outlier could move a capture's cursor ahead and cause the next valid packets to be trimmed. Patch 11 holds one suspect packet and checks its timing against the next packet. A confirmed gap stays in the mix. If the next packet confirms the existing timeline, the suspect packet can be placed there instead. A timestamp more than 2 ms ahead of the capture clock cannot become an anchor. The output-device path bypasses this logic.

## Why rk9

A review of rk8 on 2026-11-13 ran the capture timing through a model: the real `packet-timeline.hpp` with the mixer and helper logic around it, fed ten minutes of packets per case (exact timestamps, jitter, clock drift, single outliers, pauses, two programs). rk8 was at least as good as rk6 in every case but one. Three things came out of it.

The timeline left no trace. Holding, correcting, trimming and dropping a packet all happened without a log line, so a log could not say whether timestamps had anything to do with a noise report. Patch 13 counts them per capture and writes one line a minute, and one when the capture ends, while any count is not zero. A gap is only counted when it is shorter than a tenth of a second, since a pause between two sounds is a gap as well.

A capture whose timestamps stay more than 2 ms ahead of the clock lost every packet: each one was held, then dropped when the next one was no better. In the model, timestamps that count samples at the nominal rate on a device running 100 ppm fast crossed that line after about two minutes and the capture stayed silent from there (22% of ten minutes came out; rk6 played all of it). No machine is known to do this. Patch 13 times such a capture by arrival after 100 such packets in a row, with one warning, and goes back to the timestamps when one is plausible again. The model then loses the first second and keeps the rest.

The stop request of a capture could be lost. The shutdown event was auto-reset and three waits read it: the activation, the capture loop and the retry delay. A capture destroyed while it was still connecting had the event taken by the activation wait, the retry loop went on, and the destructor waited on a thread that never ended, which freezes obs. Upstream fixed it in 2.3.5 with a manual-reset event, and patch 12 backports that.

What the review did not change: clock drift between the audio device and the system clock still costs one 2 ms cut or hole each time the two have moved 2 ms apart (every 20 seconds at 100 ppm, every 2 minutes at 20 ppm, against about five clicks a second on rk6), and a single timestamp that is too early costs a dropout of its size, where one that is too late costs nothing. The report line of patch 13 shows both, as trimmed frames and short gaps.

The noise report behind rk3 to rk8 matches [obs issue 8064](https://github.com/obsproject/obs-studio/issues/8064), "sound becomes noisy after a long time" in the application audio capture of obs itself. The obs maintainers put it down to Windows: fixed in Windows 11, not in Windows 10. obs places packets one after the other whenever a timestamp is within 70 ms of the expected one, and it had the noise all the same, so the noise is in the samples Windows hands over and no placement of packets removes it. The output-device capture of patch 8 is the way around it.

## Why rk10

Patch 13 now anchors the fallback to arrival once, then uses the capture timeline's next frame for subsequent packets. When WASAPI delivers several packets together, they keep their spacing instead of overlapping. A discontinuity starts a new anchor.

## Why rk11

Patch 13 checks the fallback clock at the start of each WASAPI read batch. More than 5 ms of drift triggers a new arrival anchor. The rest of the batch follows the sample cursor. Corrections can add a gap or trim overlap at the batch boundary, so the fallback cannot drift indefinitely.

## Why rk12

Patch 13 now counts the remaining frames in the WASAPI batch before anchoring to arrival. This keeps a larger batch from moving the cursor ahead and trimming later packets when the queue returns to single reads. Normal timestamped capture still forwards each packet as it reads it.

## What is here

| path | what |
| --- | --- |
| `upstream/` | 32 files, byte for byte as git stores them, of https://github.com/Ayrton09/win-capture-audio at commit `d11b381d41333964735a57d60bda54747f2232d9` (tag 2.3.4, root tree `fd06520a6586a6947bcd186b03a82fac070c477c`): `CMakeLists.txt`, `LICENSE`, `src/`, `data/locale/`, `installer/installer.iss.in` (the cmake file reads it) |
| `MANIFEST.sha256` | the sha256 of every file of `upstream/`; the build refuses to run when one differs, is missing or is extra |
| `patches/` and `PATCHES.sha256` | the thirteen ReplayKit patches and their hashes, checked the same way |
| `build.ps1` | fetches the pinned dependencies, verifies the inputs, builds and checks the dll, and stages the plugin files |
| `BUILD-RECORD.json` | the hashes of both manifests, the toolchain, the dll's SHA-256 and size, and its imported modules |

Left out on purpose: README, CHANGELOG, CI files, the issue templates and `cmake/bootstrap-obs-sdk.ps1` (it would only run when libobs is not found, and the build supplies libobs itself, so it can never run). The git attributes of this folder (`-text`) keep the vendored files and the patches free of line ending conversion, or the hashes would depend on the checkout.

## The patches

1. `0001-keep-exclude-mode-on-per-app-capture.patch` removes the "native exclude" shortcut of 2.3.0 from `AudioCapture::WorkerUpdate()`. With one excluded program playing, that shortcut switches to a single Windows client that captures everything except that one process tree. ReplayKit's exclude list names several programs (Discord, the obs processes, the cef pages, the helper) and Windows can leave out only one tree, so obs's own audio, the monitoring output of the share preview, would be captured again, and the capture would restart every time an excluded program starts or stops playing. Without the shortcut, exclude mode follows the sessions of every other program, as 2.2.x did. Everything else of the fork stays.
2. `0002-build-id.patch` changes one line of build metadata: the obs log line `[win-capture-audio] Version 2.3.4 (d11b381+rk12)` names the upstream commit and the revision of this patch set (rk12), so a running dll can be traced to its source. Raise rk when a patch changes.
3. `0003-guard-the-capture-buffer-read.patch` makes the copy out of the engine buffer in `Mixer::SubmitPacket` behind an SEH handler that takes only an access violation or in-page error whose address lies inside the buffer being read; a fault anywhere else stays the crash it is. A packet that could not be read ends that capture, and the capture starts over on a new client as it does after any other loss of the stream. `ForwardPacket` also stops reading a success code that carries no buffer (`AUDCLNT_S_BUFFER_EMPTY`) as a packet, and nothing of any kind can leave the capture thread or the worker thread of a mixer. The plugin is built without /EHa, so the handler sits in a function of its own with no object that has a destructor.
4. `0004-heap-allocated-wasapi-callbacks.patch` makes the three notification clients of the session monitor (devices, new sessions, events of one session) heap objects held in `ComPtr` and made with `Make<>()`, instead of members of the watchers that register them with Windows. A member dies with its watcher, and a callback or a release that Windows still has on its way then lands in freed memory. It is the mistake of the 2022 release (a COM object on the stack) in three more places.
5. `0005-obs-is-always-excluded.patch` puts obs itself into the excluded set in exclude mode, whether or not it has a session, so no ancestor of obs is ever a capture root and obs is never captured as part of another program's process tree (see "Why rk3").
6. `0006-drop-packets-with-timestamp-errors.patch` drops packets marked with a timestamp error and releases their WASAPI buffers before continuing capture (see "Why rk4").
7. `0007-round-mixer-timestamps-to-nearest-frame.patch` rounds mixer offsets to the nearest frame (see "Why rk5").
8. `0008-add-output-device-capture.patch` forwards OBS output-device audio in compatibility mode (see "Why rk6").
9. `0009-trim-late-packets-after-mix-drains.patch` preserves the emitted timeline when a late packet arrives after the mix drains (see "Why rk7").
10. `0010-track-timing-per-capture-stream.patch` stabilizes packet placement within each process-loopback capture before mixing (see "Why rk7").
11. `0011-verify-capture-timestamp-jumps.patch` prevents a single forward timestamp outlier from moving the capture cursor (see "Why rk8").
12. `0012-keep-the-stop-request.patch` makes the shutdown event of a capture manual-reset, so a stop request reaches every wait (see "Why rk9").
13. `0013-report-timing-and-recover-from-early-timestamps.patch` logs what the timeline did to the packets of each capture, and times a capture by arrival while its timestamps stay ahead of the clock (see "Why rk9").

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

Read the diff between the two commits first. Then copy the files listed above from the new commit with `git cat-file blob` (no line ending conversion), regenerate `MANIFEST.sha256`, re-cut the patches that are still needed against the new files, set the new `$upstreamCommit` and `$wilCommit` (the gitlink of `deps/wil`) in `build.ps1`, raise rk, run `build.ps1 -Force`, and listen to both audio sources before shipping.

License: GPL-2.0 (see `upstream/LICENSE`). The zip carries the license text and a `SOURCE.txt` that points here.
