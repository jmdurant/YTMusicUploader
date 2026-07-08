# PythonBridge

A small Python sidecar that gives YTMusicUploader access to YouTube Music via
[ytmusicapi](https://github.com/sigma67/ytmusicapi).

## What it is

`ytm_bridge.py` is spawned by the C# app as a long-lived child process:

```
python ytm_bridge.py
```

The app and the bridge exchange JSON, one object per line, over
stdin/stdout (stdout is exclusively protocol JSON; logging goes to stderr):

```
-> {"id": 1, "cmd": "ping", "args": {}}
<- {"id": 1, "ok": true, "result": "pong"}
```

Call `init` with the browser cookie first; every other command (except
`ping` and `shutdown`) fails with a "not initialized" error until then.
During `upload_song`, progress events are interleaved on stdout:
`{"event": "progress", "id": <request id>, "sent": <bytes>, "total": <bytes>}`.

## How the app finds Python

The app looks for a self-contained runtime at `.\Python\python.exe` next to
this directory (provisioned by the setup script below). Any Python 3.10+
with the packages from `requirements.txt` installed also works.

## How to provision

From this directory, in PowerShell:

```powershell
.\Setup-PythonBridge.ps1
```

This downloads the official python.org 3.12 embeddable runtime into
`.\Python`, bootstraps pip, and installs `requirements.txt`. It is
idempotent — re-running it verifies the existing install and only
re-provisions if something is missing. Verify manually with:

```powershell
.\Python\python.exe -c "import ytmusicapi"
```
