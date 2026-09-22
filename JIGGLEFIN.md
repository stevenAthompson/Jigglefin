# Jigglefin architecture

Jigglefin is a Windows-focused, folder-first fork of Jellyfin Server. Its compatibility boundary
is the Jellyfin HTTP/WebSocket API: unmodified Jellyfin applications should continue to discover,
authenticate with, browse, and stream from a Jigglefin server.

## Product rules

1. The filesystem is authoritative. A folder or playable file must not disappear because online
   metadata is absent, ambiguous, or unavailable.
2. Local metadata wins. Kodi NFO, Emby XML, local artwork, embedded tags, and filenames are used
   before any synthesized fallback.
3. API compatibility wins over internal purity. Jigglefin may synthesize Jellyfin entity types and
   fields from paths so existing clients receive the contracts they already understand.
4. Network metadata is opt-in. A new library must work offline without provider configuration.
5. Upstream changes stay mergeable. Jigglefin-specific changes should be small, covered by tests,
   and concentrated at library-resolution and default-policy seams.

## Initial compatibility strategy

Jellyfin clients select layouts and queries from `CollectionType` and item kinds such as `Movie`,
`Series`, `MusicAlbum`, and `AudioBook`. Replacing those public values with a new Jigglefin-only
type would require custom clients, so Jigglefin keeps them. Folder and filename information will be
translated into those existing DTO shapes where clients require it.

The first milestone establishes local-first policy without changing the wire protocol:

- the existing Jellyfin physical `Folders` view is enabled on new installations;
- remote metadata providers are disabled by default for new libraries;
- Kodi/Emby sidecar readers remain enabled;
- local artwork, screen grabbing, and image extraction remain available;
- direct play, transcoding, authentication, and all existing client endpoints stay upstream code.

The library entry views for Movies, TV Shows, and Books now list the immediate children of their
physical media folders. This keeps an `Action/Example Movie/Example Movie.mkv` tree navigable as
`Action` then `Example Movie` through the existing `UserViews` and `Items` endpoints. Music and
other folder-oriented library views already use the physical children path. Recursive queries still
serve searches and client features that request the full library.

The resolver chain now leaves an arbitrary TV grouping directory as a `Folder` unless it contains
`tvshow.nfo` or episode/season evidence. A music grouping directory is not inferred to be a
`MusicArtist` merely because it contains an album; an explicit `artist.nfo` identifies an artist
folder. The media beneath those groups still resolves to standard Jellyfin item kinds. These rules
are covered by unit tests and an end-to-end API browse test.

Further resolver work will preserve more physical directory arrangements while assigning compatible
Jellyfin item kinds from deterministic path rules and local sidecars. In particular, file/folder
name collisions and mixed-content directories need dedicated coverage.

## Upstream workflow

The Git remotes are intentionally split:

- `origin`: `https://github.com/stevenAthompson/Jigglefin.git`
- `upstream`: `https://github.com/jellyfin/jellyfin.git`

`master` is reserved as a clean mirror of `upstream/master`. Jigglefin changes live on the
`jigglefin` branch. To update:

```powershell
git fetch upstream
git switch master
git merge --ff-only upstream/master
git push origin master
git switch jigglefin
git merge master
dotnet test Jellyfin.sln
git push origin jigglefin
```

Keeping product changes in focused commits makes conflicts reviewable and allows individual
Jigglefin patches to be rebased, replaced by an upstream implementation, or temporarily reverted.

## Windows development

The current upstream baseline requires the .NET 10 SDK and FFmpeg. Build and test with:

```powershell
dotnet restore Jellyfin.sln --locked-mode
dotnet build Jellyfin.sln --configuration Debug --no-restore
dotnet test Jellyfin.sln --configuration Debug --no-build
```

Run without an embedded web client during server work:

```powershell
.\scripts\dev-run.ps1
```

The launcher prefers the per-user .NET 10 SDK and the official `Jellyfin.FFmpeg` WinGet package,
even if another `ffmpeg.exe` appears earlier in the system path. Pass server arguments through it,
for example `.\scripts\dev-run.ps1 --datadir D:\JigglefinData`.

A separately installed Jellyfin client can then connect to `http://localhost:8096`.
