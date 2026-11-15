# OBS ReplayKit Privacy Policy

Last updated: November 14, 2026

OBS ReplayKit ("ReplayKit") is a free clipping app that runs inside OBS Studio on your Windows PC. It is an independent open-source project maintained by one developer and published at [github.com/7Lzz/OBS-ReplayKit](https://github.com/7Lzz/OBS-ReplayKit). This policy explains what ReplayKit does with information about you.

## The short version

- ReplayKit has no accounts and no servers of its own. It does not collect, sell or share your personal information, and it contains no analytics, telemetry, tracking or ads.
- Your clips, settings and history stay on your PC.
- ReplayKit only contacts outside services for the features listed below. Most of those requests happen only when you use the feature.
- Discord Rich Presence shows the game you are playing on your Discord profile. You can turn it off in **Settings > Privacy & Security**.

## What stays on your PC

ReplayKit stores the following on your own computer, in your Windows user profile. None of it is sent to the developer.

- **Your clips, screenshots and recordings**, in the folder you choose.
- **Your settings**, such as hotkeys, audio and appearance.
- **Clip information**, such as which game a clip is from, its links and your edit drafts.
- **Game information** that game recognition builds up: the games you have played and their icons. To recognize a game, ReplayKit looks at the program OBS is capturing, your Steam and launcher libraries, and, for Roblox, the experience names in Roblox's own log files. This all happens on your PC.
- **Deleted history**: clips you delete are kept in a hidden folder for a number of days you choose (7 by default) so you can restore them, and are then removed.
- **Logs** that help diagnose problems, in `%TEMP%\ReplayKit\logs`. Detailed logging is off unless you turn it on in Advanced settings.
- **Your Streamable sign-in**, if you sign in. It is encrypted with Windows' per-user data protection and is only ever sent to Streamable.

Uninstalling ReplayKit removes its own files and settings and empties its deleted history. Your clips, screenshots and recordings stay where they are.

## Outside services ReplayKit contacts

When ReplayKit contacts a service, that service receives what any website receives from a visit: your IP address and basic request details. ReplayKit does not add any account or device identifier of its own. Each service handles that information under its own privacy policy.

| Service | When | What is sent |
|---|---|---|
| **GitHub** (github.com, api.github.com, raw.githubusercontent.com) | Checking for ReplayKit updates and release notes, downloading updates, OBS Studio, FFmpeg and the speech-to-text tool | Requests for those files |
| **Microsoft** (go.microsoft.com, aka.ms) | During setup, if the WebView2 runtime or the Visual C++ runtime is missing | Requests for those installers |
| **Hugging Face** (huggingface.co) | Only when you first use automatic subtitles in the clip editor | Requests for the speech model files |
| **Streamable** (streamable.com and the Amazon S3 storage it uploads to) | Only when you upload or share a clip, or sign in | The clip you chose, and your Streamable sign-in |
| **Discord** (cdn.discordapp.com) | When ReplayKit shows the icon of a recognized game | The ID of that game's icon |
| **Steam** (store.steampowered.com, steamcommunity.com, steamstatic.com) | When ReplayKit looks up the icon of a recognized Steam game | The Steam ID of that game |
| **Roblox** (games.roblox.com, thumbnails.roblox.com) | When you clip a Roblox experience, to show its name and icon | The ID of that experience |
| **Google Fonts and jsDelivr** (fonts.gstatic.com, cdn.jsdelivr.net) | Only when you open the emoji and sticker picker in the clip editor | Requests for the emoji images |

Links you click in ReplayKit, such as a graphics driver download page, open in your browser and are governed by that website.

## Discord Rich Presence

When Discord Rich Presence is on, ReplayKit tells the Discord app running on your PC which game you are playing. Discord then shows it on your profile as an activity, the way it shows other apps and games. The activity shows:

- the name of the game (for Roblox, the name of the experience) and its icon,
- how long you have been playing,
- "Clipping with ReplayKit",
- a button that links to the ReplayKit download page.

ReplayKit sends this only to the Discord app on your own PC, never over the internet itself. Discord decides who can see it, based on your Discord settings, and handles it under [Discord's Privacy Policy](https://discord.com/privacy). The activity disappears when you close the game, turn off the setting, or close OBS.

Discord Rich Presence is on by default and only works while **Recognize games in clips** is on. To turn it off, go to **Settings > Privacy & Security** in ReplayKit. You can also hide all activities in Discord under **User Settings > Activity Privacy**.

## Children

ReplayKit is not directed at children under 13, and it does not knowingly collect information from anyone, including children.

## Changes to this policy

If ReplayKit starts handling information differently, this policy will be updated in the repository and the date at the top will change.

## Contact

Questions about this policy can be asked by opening an issue at [github.com/7Lzz/OBS-ReplayKit/issues](https://github.com/7Lzz/OBS-ReplayKit/issues).
