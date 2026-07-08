# YT Music Uploader

&nbsp;

[![N|Solid](https://portfolio.jb-net.co.uk/shared/yt_logo-64.png)](https://github.com/jamesbrindle/YTMusicUploader)



&nbsp;
## 2026 revival

This project was archived in 2021 after Google kept changing the YouTube Music web API. It has now been brought back to life:

- **The built-in (native) request layer has been updated to the current YouTube Music protocol** — date-derived client version (Google rejects the old `0.1` since Sept 2022), dynamically fetched visitor ID, `__Secure-3PAPISID`-based request signing, modern headers and trimmed request contexts, plus support for the new playlist response layout and continuation protocol.
- **Optionally, the app can delegate all YouTube Music calls to [sigma67's ytmusicapi](https://github.com/sigma67/ytmusicapi)** (the actively maintained Python library this app was originally ported from) via a bundled bridge. When a suitable Python runtime is found, the bridge is preferred automatically — meaning future Google protocol changes can be absorbed by simply updating the `ytmusicapi` package instead of reworking this app. To provision a self-contained runtime next to the exe, run `PythonBridge\Setup-PythonBridge.ps1`; alternatively any Python 3.10+ on PATH with `pip install ytmusicapi` works. No Python? No problem — the app falls back to the native implementation. Set `PreferPythonBridge` to `false` in `YTMusicUploader.exe.config` to force native mode.

Connecting still works the same way: paste your music.youtube.com cookie into the connect dialog (see [ytmusicapi's browser auth guide](https://ytmusicapi.readthedocs.io/en/stable/setup/browser.html) for how to copy it — the session stays valid for about 2 years). Note that uploads are only possible with cookie ("browser") authentication; Google provides no official upload API.

&nbsp;
&nbsp;

## Getting Started

&nbsp;

### 1. Install and run

Either build from source (see **Building from Source** below) or run the built `YTMusicUploader.exe`. The app starts **minimised to the system tray** (near the clock) — click the tray icon to open the window. By default it uses the built-in (native) backend and needs **no Python**.

&nbsp;

### 2. Connect your YouTube Music account

YouTube Music has no official upload API, so the app authenticates with your browser session cookie. There are two ways to provide it.

**Option A — Sign in with browser (recommended).** In the **Connect to YouTube Music** dialog, click **"Sign in with browser (automatic)"**. An embedded browser window opens; sign in to your Google account as normal. The window detects when you're connected, captures the cookie for you, and closes automatically — no developer tools required. (This uses the Microsoft Edge WebView2 Runtime, which ships with Windows 11 and current Windows 10; the app will tell you if it needs installing.)

**Option B — Paste the cookie manually (fallback).** If the embedded sign-in ever fails (Google occasionally tightens what it allows in embedded browsers):

1. In a browser, open **[music.youtube.com](https://music.youtube.com)** and make sure you are **logged in**.
2. Open **Developer Tools** (F12) and select the **Network** tab.
3. Interact with the page so requests appear, then click a request named **`browse`** (type it into the filter box to find one).
4. In that request's **Request Headers**, find the **`cookie:`** header and copy its **entire** value.
5. Paste the value into the cookie box in the Connect dialog.

Either way, the app validates the cookie immediately — a green **"Validation Successful"** means you're connected. The session stays valid for roughly two years unless you sign out. (See [ytmusicapi's browser auth guide](https://ytmusicapi.readthedocs.io/en/stable/setup/browser.html) for annotated screenshots of the manual process.)

&nbsp;

### 3. Add your music

Add one or more library folders in the app's settings. It will scan them, skip anything already uploaded, and upload the rest (supported formats: `.flac, .m4a, .mp3, .oga, .wma`, up to 300 MB per file). A file-system watcher picks up changes to those folders automatically. That's all that's required for normal use.

&nbsp;

### 4. (Optional) Enable the ytmusicapi bridge

The native backend is the default and works on its own. The optional [ytmusicapi](https://github.com/sigma67/ytmusicapi) bridge is worth enabling if you want the protocol to be maintained by that library — when Google next changes the API, you update a Python package instead of waiting on an app fix. To enable it:

1. From the `PythonBridge` folder (next to the exe, or in the project), run in PowerShell:
   ```powershell
   .\Setup-PythonBridge.ps1
   ```
   This downloads a self-contained Python 3.12 runtime into `.\PythonBridge\Python` and installs `ytmusicapi` — no system-wide Python needed. (Alternatively, any Python 3.10+ on your PATH with `pip install ytmusicapi` also works.)
2. In `YTMusicUploader.exe.config` (next to the exe), set:
   ```xml
   <add key="PreferPythonBridge" value="true" />
   ```
3. Restart the app. It now routes through ytmusicapi, and automatically falls back to the native backend if no usable Python is found.

To force native mode again, set `PreferPythonBridge` back to `false`.

&nbsp;
&nbsp;

## Building from Source

&nbsp;

Requires Visual Studio 2019/2022 with the **.NET Framework 4.7.2 Developer Pack** (targeting pack). Then:

1. Restore NuGet packages: `msbuild YTMusicUploader.sln /t:Restore /p:RestorePackagesConfig=true` (or **Restore NuGet Packages** in Visual Studio).
2. Build the `YTMusicUploader` project (Debug or Release). The output is `YTMusicUploader\bin\Debug\YTMusicUploader.exe`.

The `AppData\*.json` request templates and the `PythonBridge\` folder are copied to the build output automatically. See `NOTES.md` for the backend architecture and protocol-maintenance notes.

&nbsp;


**[Download Version 1.8.0 Installer](https://github.com/jamesbrindle/YTMusicUploader/releases/tag/v1.8.0)** *(note: the v1.8.0 installer predates the 2026 revival and will not connect. For a working build, see [Building from Source](#building-from-source) below.)*

&nbsp;
&nbsp;

### Application

This is a .Net application written in C# that uploads your personal music library to YouTube. It has a minimalistic UI for basic settings such as:

- Choosing library folders.
- Option to start the application with Windows (will start hidden, accessible via the System Tray):

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc2.png)](https://github.com/jamesbrindle/YTMusicUploader)

- Option to throttle the upload speed so it doesn't use all your bandwidth.

&nbsp;

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc1c.png)](https://github.com/jamesbrindle/YTMusicUploader)

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc6c.png)](https://github.com/jamesbrindle/YTMusicUploader)

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader_ytmusicmanagec.png)](https://github.com/jamesbrindle/YTMusicUploader)
&nbsp;
&nbsp;

### Features

- Connect and authenticate with YouTube Music.
- Upload music to YouTube Music.
- Automatic creation of YouTube Music playlists from local .m3u, .m3u8, .wpl, .pls, ,zpl playlist files.
- Delete uploaded music from YouTube Music.
- Delete playlists from YouTube Music.
- Checks YouTube Music to see if the music file has already been uploaded. ***It will perform this check once per month on all watched library files and is quite dependant on music file meta data being present and accurate.***
- Add and remove library watch folders.
- File system watcher to monitor for changes in watched folders.
- Throttle upload bandwidth.
- Start with Windows, minimized to the system tray area.
- Reads music file tags, including cover art thumbnail.
- If not all data is found in the tags of the music file, it will use the MusicBrainz API to look it up (including the cover art thumbail) (Fetching the details is purely for UI purposes. It has now impact of uploading to YouTube and doesn't write the results to the music file).
- Show an upload log dialogue.
- Show an issues log dialogue.

&nbsp;
- **This application does not send any telemetry data of any kind to its source if the 'Send Diagnostic Data' checkbox is not set'**
- **Valid music file formats are the same as YouTube Music:  .flac, .m4a, .mp3, .oga, .wma**
- **Valid music playlist file formats are:  .m3u, .m3u8, .wpl, .pls, .zpl**
- **Maximum number of files you can upload to YouTube Music is 100, 000**
- **Maximum number of playlist items YouTube Music will allow is 5,000**
- **Maximum file size YouTube Music will allow is 300 MB**
- **Although you have the ability to delete from YouTube Music within the app, this application is strictly a one way synchronisation app.**
&nbsp;

&nbsp;


### Reason for Creation

I used to have Google Play music and liked uploading my own content via its automatic library uploader application; I have a large library of music and you could stream your own uploaded songs from Google Play music without paying for a subscription...

Google Play music is on its way out in December and its replacement, YouTube Music doesn't currently have a library watcher application. You can only drag and drop manually into the browser for a limited number of songs... So, I decided to create one.

I got a subscription in the end, so some might consider this pointless in the world of streaming anything you want these days... So I suppose the only real benefit is the ability to:

- Upload songs that aren't on YouTube music.
- Backup your songs *(you can't download them again from YouTube music, but you can use [Google Takeout](https://takeout.google.com/settings/takeout?pli=1) to get them).*
&nbsp;
&nbsp;

### How it Works

YouTube Music has no official upload API, so this application mimics the HTTP requests and responses the YouTube Music site itself uses (F12 in your browser is your friend). It does this in one of two interchangeable ways: a **built-in (native) C# implementation** of those requests (the default), or, optionally, by delegating to **[sigma67's ytmusicapi](https://github.com/sigma67/ytmusicapi)** through a bundled Python bridge (see *Getting Started → Enable the ytmusicapi bridge*).

YouTube Music authenticates with a session cookie plus an `Authorization` header containing a SAPISID hash derived from that cookie (specifically the `__Secure-3PAPISID` value). Rather than embedding a browser, the app asks you to copy the cookie from your own logged-in browser session and paste it into the connect dialog (see *Getting Started → Connect your YouTube Music account*):
&nbsp;

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc3.png)](https://github.com/jamesbrindle/YTMusicUploader)

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc4.png)](https://github.com/jamesbrindle/YTMusicUploader)

[![N|Solid](https://portfolio.jb-net.co.uk/shared/ytmusicuploader-sc5.png)](https://github.com/jamesbrindle/YTMusicUploader)
&nbsp;

### Technology

- .Net Framework 4.7.2
- WinForms
- SQLite
- Optional: Python 3.10+ with [ytmusicapi](https://github.com/sigma67/ytmusicapi) (for the bridge backend)
&nbsp;
&nbsp;

### IDE / Extensions

- Microsoft Visual Studio 2019 / 2022 (with the .NET Framework 4.7.2 Developer Pack)
- Microsoft Visual Studio Installer Project
&nbsp;
&nbsp;

### Libraries

- [Brotli.Net (Decompress Google HTTP resonse body)](https://www.nuget.org/packages/Brotli.NET) 
- [xxHash - Fast file hash generator](https://www.nuget.org/packages/System.Data.HashFunction.xxHash/)
- [Dapper](https://github.com/StackExchange/Dapper) 
- [Metro Framework (UI Styling)](https://github.com/dennismagno/metroframework-modern-ui) 
- [Ookii Dialogues](http://www.ookii.org/software/dialogs)
- [MusicBrainz API - Zastai](https://github.com/Zastai/MetaBrainz.Common.Json)
- [MusicBrainz CoverArt - Zastai](https://github.com/Zastai/MetaBrainz.MusicBrainz.CoverArt)
- [MusicBrainz API implementation](https://github.com/avatar29A/MusicBrainz)
- [TagLibSharp - Read music file tags](https://www.nuget.org/packages/TagLibSharp/)
&nbsp;
&nbsp;

### Tools

- [Doxygen (Source code HTML documentation)](https://www.doxygen.nl/index.html)
&nbsp;
&nbsp;

### Special Thanks

- [sigma67](https://ytmusicapi.readthedocs.io/en/latest/) - Who created a Python YouTube Music API that I could reference. [sigma67: Github](https://github.com/sigma67/ytmusicapi).
- [wilsone8](https://www.codeproject.com/Articles/38959/A-Faster-Directory-Enumerator) - Who created a very fast Windows directory enumerator.
- [Dave Thomas](https://stackoverflow.com/users/984724/dave-thomas) - Who worked out how to get the SAPISID hash from the the YouTube Music authentication cookie on a post on [StackOverflow](https://stackoverflow.com/a/32065323/5726546).
- [0xDEADBEEF](https://stackoverflow.com/users/909365/0xdeadbeef) - Who made a simple class to bandwidth throttle a byte stream on a post on [StackOverflow](https://stackoverflow.com/questions/371032/bandwidth-throttling-in-c-sharp).
- [avatar29A](https://github.com/avatar29A/MusicBrainz) - For the MusicBrainz .Net API implementation.
- [DjSt3rios](https://github.com/DjSt3rios) - For some WebView2 insight.
- [tmk907](https://github.com/tmk907) - Who created a very good, easy to use, multi-type playlist reader. [tmk907: Github](https://github.com/tmk907/PlaylistsNET).

### Thanks for the Coffee

- EdgeGuy13
- Mew
- CowtownChina
- Someone
- Someone
- Stephen M
- Brian A
- @NourishedAIO
- nishantranacrm
- Oak
- Emoniz
- Ileach

&nbsp;
&nbsp;

