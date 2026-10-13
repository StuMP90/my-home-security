# Microsoft Store listing

Text and answers for the My Home Security submission in Partner Center, ready to paste in. Limits in
brackets are Partner Center's. The Store logos are next to this file.

## Product identity

| Field | Value |
|---|---|
| Product name | My Home Security |
| Developed by / publisher display name | Stuart Millington (must match Partner Center) |
| Category | Security (or Utilities & tools) |
| Pricing | Free, no in-app purchases, no ads |
| Markets | All |
| Package | `MyHomeSecurity_<version>_x64.msix` from the release workflow, built with the Partner Center identity values (README → Packaging) |
| Minimum OS | Windows 10 version 1809 (build 17763) or later, x64 |

## Links

| Field | Value |
|---|---|
| Privacy policy URL | <https://github.com/StuMP90/my-home-security/blob/main/PRIVACY.md> |
| Website | <https://github.com/StuMP90/my-home-security> |
| Support contact | <https://github.com/StuMP90/my-home-security/issues> |

The privacy policy is needed because the app connects to the network and keeps camera passwords
and a Home Assistant token. It must be live at that URL, so push PRIVACY.md to `main` before
submitting.

## Short description [1,000 characters]

> Watch your security cameras and control your Home Assistant, privately, from your own PC.
> My Home Security shows all your cameras in a grid or one full size in high resolution, finds
> cameras on your network for you, and saves snapshots and recordings to your own folders. It's
> light on the CPU and talks only to your own devices: no account, no cloud, no telemetry.

## Description [10,000 characters]

> My Home Security is a free, open-source app for watching your security cameras and controlling
> your Home Assistant. Everything stays between your computer and your own devices. There is no
> account, no subscription, no cloud and no telemetry.
>
> **CAMERAS**
>
> See all your cameras at once in a grid, or double-click one to watch it full size using its
> high-resolution stream. Use the arrow keys for the next camera and F11 for full screen. Works
> with Tapo and other RTSP and ONVIF cameras, and MJPEG (HTTP) cameras.
>
> **EASY TO SET UP**
>
> Press Find cameras… and the app searches your local network (ONVIF discovery and the usual
> camera ports), shows what it found, and adds the ones you pick. For ONVIF cameras it reads the
> stream addresses from the camera itself. Or add a camera by its address. Everything is set up
> in the app; there are no files to edit.
>
> **SNAPSHOTS AND RECORDINGS**
>
> Save a full-resolution snapshot (JPEG or PNG) with one click, or record a camera's main stream
> to MP4 or MKV, with an optional time limit. Recordings copy the video without re-encoding, so
> they use almost no CPU, and they keep running while the app is locked. The Captures tab lists
> everything you've saved.
>
> **HOME ASSISTANT**
>
> Add tiles for switches, lights, sensors, climate and more, and click a switch to turn it on or
> off. Boost thermostats that support it, such as Hive, to a set temperature for 30 minutes to
> 2 hours. Enter your Home Assistant address and a long-lived access token in Settings.
>
> **LIGHT ON YOUR PC**
>
> Only what's on screen streams. The grid uses each camera's small stream at a low frame rate,
> and a key-frames-only mode goes lighter still. Switching tab, minimising or locking stops the
> streams. Hardware decoding uses your graphics card when it can.
>
> **PRIVATE BY DESIGN**
>
> Camera passwords and your Home Assistant token are kept only in an encrypted file (AES-256-GCM),
> unlocked with your password, and never appear on a command line. The app locks itself when
> you're away. It searches your network only when you ask, and only your own subnet. It collects
> nothing: no accounts, analytics, telemetry, ads, crash reports or update checks.
>
> My Home Security is open source (MIT) and includes FFmpeg (LGPL). Licences are under Help →
> Licences.

## What's new in this version [1,500 characters]

For the first submission:

> First Microsoft Store release.

## Product features [up to 20, 200 characters each]

1. All your cameras in a grid, or one full size in high resolution
2. Works with Tapo, ONVIF and other RTSP cameras, and MJPEG (HTTP) cameras
3. Find cameras on your local network and add them in a couple of clicks
4. One-click full-resolution snapshots in JPEG or PNG
5. Record to MP4 or MKV without re-encoding, with an optional time limit
6. Recordings keep running while the app is locked
7. Home Assistant tiles for switches, lights, sensors and climate, with thermostat boost
8. Light on the CPU: only what's on screen streams, with low-rate grid and hardware decoding
9. Passwords and tokens kept in an encrypted vault, never on a command line
10. Auto-lock after a period of inactivity
11. No account, no cloud, no telemetry: it talks only to your own devices

## Search terms [up to 7, 30 characters each, 21 words in total]

1. security cameras
2. Home Assistant
3. Tapo camera viewer
4. RTSP viewer
5. ONVIF
6. CCTV
7. camera recorder

That's 13 words.

## Copyright and trademark info [200 characters]

> © 2026 Stuart Millington. Open source under the MIT licence. Includes FFmpeg (LGPL) and other
> third-party components; see Help → Licences.

## Additional licence terms

Leave this empty to use the standard Microsoft application licence terms. The app's own MIT licence
and the third-party notices ship inside the package (`THIRD-PARTY-NOTICES.txt`, Help → Licences).

## Screenshots [up to 10; PNG; 1366×768 or larger; captions 200 characters]

Not made yet. Take them on Windows at 1920×1080 or larger. Use only cameras you own and that show
nothing private (or a test stream), and blur or avoid addresses, names and entity IDs that
identify your home. Suggested shots and captions:

| File | Caption |
|---|---|
| `01-grid.png` | All your cameras at once, light on your PC |
| `02-single.png` | One camera full size in high resolution, with snapshot and record |
| `03-find-cameras.png` | Find cameras on your network and add them in a couple of clicks |
| `04-home.png` | Control Home Assistant switches, lights and sensors |
| `05-captures.png` | Snapshots and recordings saved to your own folders |
| `06-settings.png` | Everything set up in the app, with passwords kept encrypted |

Use 01 as the first screenshot; the Store uses it as the hero image.

## Store logos

Partner Center → Store listing → Store logos: `StoreLogo-300x300.png` (required), plus the 150 and 71
sizes. The tile and taskbar icons are inside the package. All come from `tools/make_icons.py`.

## Additional system requirements

| | Minimum | Recommended |
|---|---|---|
| Memory | 4 GB | 8 GB |
| Graphics | — | A graphics card with video decoding (for many cameras at once) |
| Network | Cameras and/or Home Assistant reachable from this PC | |
| Disk | 300 MB for the app, plus space for your recordings | |

## Product declarations

| Declaration | Answer |
|---|---|
| This app allows users to make purchases, but does not use the Microsoft Store commerce system | No |
| This app has been tested to meet accessibility guidelines | No; it hasn't been formally tested |
| Customers can install this app to alternate drives or removable storage | Yes |
| This app depends on non-Microsoft drivers or NT services | No |
| Windows can include this app's data in automatic backups to OneDrive | No (the vault holds camera passwords; it is encrypted, but there's no need to copy it off the PC) |
| This app collects, stores or transmits personal information | No; it stores camera passwords and a token on the PC only, encrypted, and sends them only to the user's own devices. See the privacy policy |

## Age rating (IARC questionnaire)

Choose the "Utility, productivity, communication or other" category, then answer:

- Violence, fear, sexuality, nudity, gambling, drugs, alcohol, tobacco, crude humour, bad
  language: **No**. The app contains no content of its own; it shows the user's own cameras.
- Users can interact or exchange content with each other: **No**. Nothing is shared or uploaded.
- Shares the user's location: **No**.
- Digital purchases: **No**.
- Unrestricted internet access (a web browser or search): **No**.

The expected result is 3+ / Everyone.

## Capabilities and their justification

The package declares three capabilities ([`AppxManifest.xml`](../msix/AppxManifest.xml)):

| Capability | Type | Why |
|---|---|---|
| `runFullTrust` | Restricted | My Home Security is a .NET desktop (Win32) app packaged as MSIX, not a UWP app. Every packaged desktop app needs this capability to run. The app uses it to start its bundled FFmpeg program, which decodes and records the camera streams. |
| `internetClient` | General | Connects to the user's Home Assistant when its address is outside the home network (for example Home Assistant Cloud or the user's own domain). Shown on the Store page as "Access your Internet connection". |
| `privateNetworkClientServer` | General | Connects to the user's cameras and Home Assistant on the home network; searches the local subnet for cameras (ONVIF WS-Discovery multicast and a check of the usual camera ports), only when the user presses *Find cameras…*; and runs a relay on 127.0.0.1 that passes camera streams to FFmpeg so that camera passwords never appear on a command line. Shown as "Access your home or work networks". |

Full-trust apps aren't network-isolated, so the two network capabilities don't change what the
app can do. They're declared so the Store page states plainly that the app uses the network.

Paste this into **Submission options → Restricted capabilities** for `runFullTrust`:

> My Home Security is a desktop (Win32) .NET application packaged with MSIX. runFullTrust is required
> for any packaged desktop app. The app uses it to run the FFmpeg program bundled in its package,
> which decodes the user's own camera streams for display and records them to files the user
> chooses. It does not install services or drivers and does not change system settings.

**Not requested**, and why:

- **`broadFileSystemAccess`**. A full-trust desktop app already saves snapshots and recordings to
  the folders the user picks, with the user's own permissions.
- **`webcam`** and **`microphone`**. The app shows network cameras only; it never uses the PC's
  own camera or microphone.
- **`internetClientServer`**. The app accepts no connections from outside: its only listening
  socket is the relay on 127.0.0.1.

## Notes for certification

Paste into **Submission options → Notes for certification**:

> No account or sign-in is needed, and the app is free with no purchases.
>
> On first start the app asks for a password to create its encrypted settings vault; choose any
> password. The app shows network security cameras and controls a Home Assistant server, which
> the tester may not have. Both are optional, and the app runs and is fully navigable without
> them.
>
> To test without a camera: **Cameras → Add camera…** opens the camera form (type, address, port,
> login and stream paths); **Test connection** reports that nothing answers, which is expected.
> With any RTSP server or MJPEG (HTTP) camera reachable from the test PC, enter its address and
> path to see it in the grid; double-clicking shows it full size, with **Snapshot** and
> **Record**. **Find cameras…** searches the local subnet (it finds nothing if there are no
> cameras, which is expected). **Settings** shows all options; **Help** (Ctrl+Shift+H) lists the
> keys, and Help → Licences shows the third-party licences.
>
> The package includes FFmpeg (LGPL, a separate executable in the `ffmpeg` folder; its source
> location is in `ffmpeg/FFMPEG-SOURCE.txt` and under Help → Licences). The app connects only to
> addresses the user enters and, on request, the local subnet. It downloads nothing, has no
> telemetry, and sends nothing about the user anywhere.

## Before each submission

- [ ] Build with the Partner Center identity values (repository variables `MSIX_IDENTITY_NAME`,
      `MSIX_PUBLISHER`, `MSIX_PUBLISHER_DISPLAY_NAME`).
- [ ] Check that PRIVACY.md on `main` is current. Update its date if the data the app keeps has
      changed.
- [ ] Retake the screenshots if the UI has changed visibly.
- [ ] Fill in "What's new".
- [ ] Run the Windows App Certification Kit on the MSIX (optional; Partner Center runs its own).
