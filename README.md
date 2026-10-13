# My Home Security

A private, cross-platform (Windows + Linux) app for watching your security cameras and
controlling your Home Assistant. It is the first part of a home security monitoring system.
Built with C#/.NET 10 and [Avalonia](https://avaloniaui.net/), MIT licensed.

No accounts, no cloud, no telemetry. The app only talks to your cameras, your Home Assistant and,
when you ask it to search, your local network. See [PRIVACY.md](PRIVACY.md).

## Features

- **Cameras**: a grid of all your cameras, and a single camera full size in high resolution
  (double-click a tile; Left/Right for the next camera, F11 for full screen). Works with Tapo
  and other RTSP/ONVIF cameras, and with MJPEG (HTTP) cameras or any stream URL.
- **Easy discovery**: *Find cameras…* searches your network (ONVIF discovery plus a check of the
  usual camera ports), shows what it finds, and adds the ones you pick. For ONVIF cameras the
  stream addresses are read from the camera: the highest resolution for the single view, the
  lowest for the grid. For other cameras, *Test connection* finds the stream paths if the ones
  entered don't work, trying those Tapo, Hipcam (Ctronics), Hikvision, Dahua, Reolink and others use.
- **Snapshots and recordings**: a full-resolution snapshot (JPEG or PNG) with one click, and
  recordings of a camera's main stream to MP4 or MKV, with an optional time limit. Recordings
  keep running while the app is locked, and the *Captures* tab lists what you've saved.
- **Home Assistant**: tiles for switches, lights, sensors, climate and more. Click a switch to
  turn it on or off. The address and access token are entered in the app. Some Tuya sensors
  (an air purifier's PM2.5, for one) reach Home Assistant as a running total of their readings; a
  measurement over 10,000 is taken to be one, and its tile shows the latest step instead.
- **Thermostat boost**: thermostats with a `boost` preset get a *Boost…* button: pick a
  temperature and 30 min, 1 h or 2 h. Hive uses `hive.boost_heating_on`, so the boost ends on its
  own and the tile shows when; other thermostats switch to their boost preset. Click the chip to
  cancel. Hive's cloud can take a few minutes to report a boost, so the tile says *Starting…* until
  the new temperature comes through. Home Assistant doesn't report a boost's end time, so the app
  remembers it (in the vault).
- **Configured in the app**: everything is entered in the app and kept in one encrypted vault.
  There is no config file to edit.

### Light on the CPU

Watching cameras was the expensive part, so the app is built to do as little as possible:

- Only what's on screen streams. Switching tab, minimising the window or locking stops the streams.
- The grid uses each camera's small sub-stream at a low frame rate (5 fps by default), scaled
  down by ffmpeg before it reaches the app. *Key frames only* mode goes further and decodes about
  one picture a second or two per camera.
- The single view uses the main stream, with optional frame-rate and resolution caps.
- Hardware decoding (ffmpeg picks the graphics card's decoder) is on by default and can be turned off in Settings.
- Recordings copy the camera's video without re-encoding, so they cost almost no CPU.

### Privacy and security

- Camera passwords and the Home Assistant token are kept only in an encrypted vault
  (AES-256-GCM, with a key derived from your password by PBKDF2-SHA256), never in plain text.
- Camera passwords never appear on a command line. ffmpeg connects to a relay inside the app,
  on `127.0.0.1` only, with a one-time code, and the relay logs in to the camera.
- Auto-lock after a period of inactivity (Ctrl+Shift+L locks straight away). Locking forgets the
  decrypted settings, stops the live views and stops talking to Home Assistant.
- Network discovery only runs when you press *Find cameras…*, and stays on your own subnet.
- Each camera's last grid picture is saved, encrypted with a key derived from the vault's, so the
  grid shows it instead of a blank tile while the cameras connect.
- Snapshots and recordings are ordinary, unencrypted files: keep them somewhere only you can read.

## Requirements

- **ffmpeg** for video. On Linux, install it with your package manager (`sudo apt install ffmpeg`).
  The Windows package includes an LGPL build in its `ffmpeg` folder. The app looks in
  `$MYHOMESECURITY_FFMPEG_DIR`, then beside the app (`ffmpeg/` or `ffmpeg/bin/`), then on the `PATH`.
  Settings → About shows which one it found.
- Building needs the .NET 10 SDK.

## Run

```bash
dotnet run --project src/MyHomeSecurity.App
```

Set `MYHOMESECURITY_DATA_DIR` to run the app against a separate data folder.

## Tests

```bash
dotnet test
```

The tests need neither ffmpeg nor cameras. The UI tests run headless and save screenshots to
`$MYHOMESECURITY_SCREENSHOT_DIR` (by default `myhomesecurity-screenshots` in the temp folder).

## Packaging

```bash
packaging/publish-linux.sh 0.1.0        # artifacts/linux/myhomesecurity-0.1.0-linux-x64.tar.gz
packaging/publish-windows.sh 0.1.0      # artifacts/windows/myhomesecurity-0.1.0-win-x64.zip
```

Both are self-contained. The Linux tarball has an `install.sh` that installs for the current user
under `~/.local` and adds a menu entry; it uses the system's ffmpeg. The Windows script fetches an
LGPL ffmpeg build ([BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), checksum-checked)
with `tools/fetch-ffmpeg.sh` and puts it in the `ffmpeg` folder. Each package includes
`THIRD-PARTY-NOTICES.txt`, generated by `tools/generate-third-party-notices.py`, and
`ffmpeg/FFMPEG-SOURCE.txt`, which says where ffmpeg's source is.

### MSIX and the Microsoft Store

On Windows, with the Windows 10/11 SDK installed:

```powershell
# Store submission (unsigned; the Store signs it). Use the identity from Partner Center.
./packaging/build-msix.ps1 -Version 1.0.0.0 -IdentityName 12345YourName.MyHomeSecurity `
    -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "Your Name"

# Local sideload test, signed with a self-signed certificate
./packaging/build-msix.ps1 -SelfSign
```

The package (`artifacts/msix/MyHomeSecurity_<version>_x64.msix`) includes the same LGPL ffmpeg.
Its manifest is [`packaging/msix/AppxManifest.xml`](packaging/msix/AppxManifest.xml); besides
`runFullTrust` it declares `internetClient` (Home Assistant outside the home) and
`privateNetworkClientServer` (cameras and Home Assistant on the LAN, discovery and the loopback
relay). The listing text, capability justifications and certification notes are in
[`packaging/store/STORE-LISTING.md`](packaging/store/STORE-LISTING.md), next to the Store logos.
Tile and Store images come from `tools/make_icons.py`.

### CI and releases

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs the tests, then builds the Linux
tarball. Pushes and pull requests build version `0.0.<run>` as workflow artifacts. Pushing a tag
`vX.Y.Z` builds that version and creates the GitHub release with the tarball attached:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

The Windows zip and the MSIX are built (and attached to releases) only when the repository variable
`BUILD_WINDOWS` is `true`. Before a Store submission, also set the repository variables `MSIX_IDENTITY_NAME`, `MSIX_PUBLISHER` and
`MSIX_PUBLISHER_DISPLAY_NAME` to the Partner Center values (Settings → Secrets and variables →
Actions → Variables). Without them the MSIX is built with a development identity.

## Data

| | Windows | Linux |
|---|---|---|
| Vault and logs | `%LOCALAPPDATA%\MyHomeSecurity` | `~/.config/myhomesecurity` |
| Snapshots | `Pictures\MyHomeSecurity` | `~/Pictures/MyHomeSecurity` |
| Recordings | `Videos\MyHomeSecurity` | `~/Videos/MyHomeSecurity` |

The snapshot and recording folders can be changed in Settings.

## Roadmap

- **Stage 2: enhancement and correction** of snapshots and recordings: denoising, sharpening,
  low-light and colour correction, and upscaling, using the lighter models from MyVideoEditor,
  run on your computer.
- Later stages of the home security system: motion events, alerts and schedules.

## Licence

MIT. See [LICENSE](LICENSE). Third-party licences are listed in `THIRD-PARTY-NOTICES.txt` in each
package and in the app under Help → Licences.
