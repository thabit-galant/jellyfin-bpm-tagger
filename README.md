# BPM Tagger for Jellyfin

> [!WARNING]
> **This plugin was generated with AI and may contain bugs.** It edits your Jellyfin database. Test it on a dummy Jellyfin server with copies of a few albums before using it on your real library, and keep backups of your Jellyfin data.

Detects the tempo of every music track using Jellyfin's own ffmpeg, then **adds a Jellyfin tag** like `BPM:128`, the same format the MusicTags plugin uses, so SmartLists can filter on it immediately. Your audio files are not changed.

Targets **Jellyfin 10.11.x** (.NET 9).

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** (the **Manage Repositories** button on the catalog page) and add a repository with this URL:
   ```
   https://raw.githubusercontent.com/thabit-galant/jellyfin-bpm-tagger/main/manifest.json
   ```
2. Find **BPM Tagger** in the plugin catalog and install it.
3. Restart Jellyfin.


## How detection works

1. ffmpeg decodes 60 s of audio, starting 30 s in to skip intros, as 11 kHz mono.
2. A spectral-flux onset envelope marks where notes and drum hits start.
3. Autocorrelation finds the repeating period; each candidate tempo is backed up by its half- and double-length periods, with a gentle preference for ~130 BPM to settle ties.
4. The estimate is sharpened using peaks several beats apart, then folded into your BPM range.

It takes about 0.3–0.5 s per track on a normal CPU. Expect roughly 1–2 hours per 10,000 tracks with 2 in parallel.

**Tested** against synthetic house, rock, hip-hop and drum & bass loops from 75 to 174 BPM: every tempo came out exact, apart from the usual half/double-time ambiguity (160 BPM rock and 174 BPM DnB read as 80 and 87 with the default 70–180 range). Raising the lowest BPM to 90 fixes those but then doubles slow hip-hop (75 → 150). Pick the range that suits your library.

## Build

You need the .NET 9 SDK, or let GitHub build it for you.

**Option A: GitHub Actions (nothing to install)**
Push this folder to a GitHub repo. The included workflow builds it on every push; download the `bpm-tagger-1.0.0.0` artifact from the Actions run.

**Option B: Docker**
```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish Jellyfin.Plugin.BpmTagger/Jellyfin.Plugin.BpmTagger.csproj -c Release -o publish
```

**Option C: local .NET 9 SDK**
```bash
dotnet publish Jellyfin.Plugin.BpmTagger/Jellyfin.Plugin.BpmTagger.csproj -c Release -o publish
```

The only file you need is `publish/Jellyfin.Plugin.BpmTagger.dll`.

**Option D: dev container (VS Code or GitHub Codespaces)**
Open the folder in VS Code and choose **Reopen in Container** (needs Docker and the Dev Containers extension). You get:

- the .NET 9 SDK, ffmpeg and the C# Dev Kit, and
- a separate Jellyfin 10.11.11 test server at http://localhost:8096, whose config and test music live in the git-ignored `.dev/` folder.

To try the plugin:
1. Copy a few test albums (copies, not your real library) into `.dev/music`.
2. Run the VS Code task **Deploy plugin to dev Jellyfin** (Terminal → Run Task). It builds the plugin, installs it into the test server and restarts it.
3. Open http://localhost:8096, finish the setup wizard, add `/music` as a Music library, then run **Detect BPM of music** from Scheduled Tasks.
4. **Show dev Jellyfin logs** follows the server log, where the plugin reports each track.

## Use

1. Check the settings under **Dashboard → Plugins → BPM Tagger**.
2. Run **Dashboard → Scheduled Tasks → Library → Detect BPM of music**. It also runs nightly at 03:00 and only processes tracks it hasn't tagged yet.
3. In SmartLists, add a rule **Tags → matches regex**, e.g. for 120–160 BPM:
   ```
   (?i)\bBPM:(1[2-5][0-9]|160)\b
   ```

## Things to know

- **The tags only live in Jellyfin:** other music apps won't see them, and rebuilding the library loses them. The next run of the task adds them back, analysing each track again unless its file already has a BPM.
- **Read-only music folders are fine:** the plugin only reads your audio files, so Docker mounts ending in `:ro` work.
- **Existing BPM values are trusted:** if a file already has a BPM, it's copied to the Jellyfin tag, not re-analysed, unless you tick "Re-analyse tracks that already have a BPM".
- **Re-analysing:** remove the `BPM:` Jellyfin tags (or tick re-analyse) and run the task again.
- **Tracks with no clear beat** (ambient, classical, spoken word) are left untagged and retried on each run. Use "Maximum tracks per run" if that gets slow.
- **MusicTags plugin:** they work together, because both use the `BPM:` tag format. MusicTags reads BPM from the files, so it won't see values this plugin detects.
