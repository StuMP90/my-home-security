# My Home Security privacy policy

_Last updated: 13 October 2026_

My Home Security shows your security cameras and controls your Home Assistant. It is developed by
Stuart Millington and released as open source under the MIT licence. This policy covers the app
however you install it.

## In short

My Home Security does not collect, sell or share your personal data. It has no accounts, analytics,
telemetry, advertising, crash reporting, update checks or cloud service. The developer receives
nothing from the app.

## Network connections

My Home Security only connects to what you set up in it, and only when it needs to:

- **Your cameras**, at the addresses you enter, using RTSP, HTTP (MJPEG) and, when you add or
  search for a camera, ONVIF. A camera is only streamed while it is on screen or being recorded.
- **Your Home Assistant**, at the address you enter, using its access token. It is only asked for
  its state while the Home Assistant tab is showing, and when you switch something on or off.
- **Your local network, when you ask it to search for cameras** (*Find cameras…*). The app sends
  an ONVIF discovery message to the local multicast address and checks the usual camera ports
  (554, 2020, 80, 81 and 8080) on the computers of your own subnet. It never searches on its own.

Video and commands go straight between your computer and your devices. They never pass through
the developer or anyone else.

Camera passwords are never put on a command line. The app runs ffmpeg (a separate video program)
for the video, and ffmpeg connects to a private relay inside the app, on your own computer only
(`127.0.0.1`), using a one-time code. The relay adds the password when it talks to the camera.
Many cameras only support unencrypted RTSP and HTTP, so keep them on your home network and don't
expose them to the internet. The app warns you if the Home Assistant address isn't encrypted and
isn't on your home network.

## Data stored on your computer

My Home Security keeps its data in a folder in your user profile:

- **Windows:** `%LOCALAPPDATA%\MyHomeSecurity`
- **Linux:** `~/.config/myhomesecurity` (or `$XDG_CONFIG_HOME/myhomesecurity`)

That folder holds three things.

- **The vault (`vault.json`).** It contains your cameras and their usernames and passwords, your
  Home Assistant address and token, your Home tiles and your settings. It is encrypted with
  AES-256 using a key derived from your password. The password itself is never saved, so without
  it the vault can't be read, by the developer or anyone else. A copy of the previous version
  (`vault.json.bak`) is kept beside it.
- **Each camera's last picture (`pictures/`).** When a camera stops streaming, its last small grid
  picture is saved so the grid isn't blank while the cameras connect next time. The pictures are
  encrypted with AES-256 using a key derived from the vault's, so they can't be read without your
  password either. Removing a camera, or changing its details, deletes its picture.
- **Diagnostic logs (`logs/`).** Plain-text logs of events such as streams starting and stopping,
  connection errors and app errors. They can include camera names and addresses. They never
  include passwords, tokens or pictures. Logs older than 14 days are deleted automatically. They
  stay on your computer: the app never sends them anywhere.

**Snapshots and recordings are not encrypted.** They are ordinary picture and video files, saved
by default in `Pictures/MyHomeSecurity` and `Videos/MyHomeSecurity`, or in the folders you choose
in Settings. Anyone who can read those folders can see them, so keep them somewhere only you can
read. The app never uploads them.

While the app is unlocked, your decrypted settings are held in memory, and only there. Locking it
discards the vault key and the decrypted settings and stops the live views and Home Assistant
updates. Recordings you started keep running while it is locked, so they keep the camera details
they need until they finish.

## Deleting your data

To remove the app's data, delete the folder listed above. Snapshots and recordings are in their
own folders: delete them there.

If you forget your password and start again, the old vault is not deleted. It is renamed to
`vault.forgotten-<date>.json` and kept, still encrypted, in the same folder. Delete it there if
you no longer need it.

## Children

My Home Security is a tool for watching your own cameras. It is not aimed at children and collects
no data from anyone.

## Third parties

ffmpeg, which decodes and records the video, runs on your computer as a separate program and
makes no connections of its own except to the app's private relay or to the camera address the
app gives it.

Your cameras and Home Assistant are run by you. Some cameras also talk to their maker's cloud
service on their own, whatever app you use. That is between the camera and its maker, and their
privacy policy applies.

## Changes

If this policy changes, the new version will be published at this address with a new date. The
full history of this file is kept in the repository.

## Contact

Questions about privacy can be raised as an issue at
<https://github.com/StuMP90/my-home-security/issues>.
