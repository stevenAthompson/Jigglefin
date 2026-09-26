# Live, offline Jigglefin

This supersedes the scan-backed architecture in `JIGGLEFIN.md`. The previous release
is not an implementation of this design. Work is on `codex/live-filesystem` until
the replacement passes its end-to-end gates.

## Product contract

- A configured root is a location, not a catalog import. Mounting and startup do
  not enumerate its contents, schedule scans, probe files, or fetch metadata.
- Each browse request reads that directory's immediate entries from the filesystem.
  It never recursively visits descendants, even when a legacy client asks for a
  recursive catalog query. Additions/removals appear on the next request.
- Listings use filesystem names and basic attributes. Selecting an item reads its
  local sidecars/artwork; playback may probe that selected media. No listing-time
  metadata enrichment, recursive child counts, or thumbnail-generation sweep.
- Durable state consists of roots/access rules, users, item identity lookups,
  playback/favorite state, and bounded on-demand caches. A cache is never the source
  of directory membership. Deleting metadata caches must not lose bookmarks or
  require a library rebuild.
- No installed-product internet features or opt-in switches: no scrapers, online
  images/subtitles/lyrics, update checks, plugin downloads, remote URL media, or
  sidecar-triggered network fetches. Enforce this in the server, media helpers,
  and bundled web UI, and test both attempted and successful outbound connections.
  Client/server connections and explicitly configured local/LAN media remain usable.
- The bundled UI is a simple folder browser with account/access, root-location,
  playback, basic file sorting, and cache controls. Do not fork Android yet.

## Compatibility boundary

Retain Jellyfin authentication, client/session identity, item IDs, DTOs, playback
negotiation, streams, and playback reports. Replace the catalog-backed resolution
path rather than projecting another stored catalog as folders. Keep upstream
streaming/transcoding components where they can meet the offline boundary.

Unsupported catalog endpoints return valid empty results or a navigable, non-media
"Use Folder View" entry where that endpoint's schema and client behavior permit it.
Do not invent playable files, fake actors/albums, or trigger a whole-tree traversal
to satisfy those endpoints. Client UI caching and hard-coded native menus cannot
all be controlled by the server; verify the official clients' browse/play flows.

Filesystem access must remain within an authorized root. Link/reparse entries may
be displayed, but must not silently traverse outside the root. Media trees are
read-only; profile state and caches live separately. Keep bookmarks separate from
disposable probe/sidecar caches and revalidate paths before using remembered IDs.

## Implementation order

1. A read-only directory source, stable path identities, and instrumented tests
   proving bounded filesystem access and fresh listings. No database/metadata/network
   dependency in the listing layer.
2. Persistent root/identity configuration and live Jellyfin browse/item resolution;
   disable initial/startup/manual/background scans and library-monitor ingestion.
   Migrate root configuration only, without walking existing libraries.
3. Selection-time local metadata, playback-time probing, media streaming, and durable
   resume using the existing playback engine. Audit all entry points, including
   direct IDs and client requests that bypass a details page.
4. Remove network features and enforce outbound denial, including native helpers,
   remote URLs embedded in sidecars, and plugin/update infrastructure.
5. Simplify the bundled web UI; provide schema-correct catalog fallbacks; verify
   unmodified Jellyfin clients. Existing scan-oriented tests must be replaced with
   tests of this contract, not relaxed into mere successful HTTP responses.
6. Package and test from a clean profile, then from an older Jigglefin profile.

## Completion gates

- Mount a large synthetic tree: zero descendant enumeration and zero media records
  imported/probed. Repeat at startup and through legacy refresh/configuration APIs.
- Browse a directory: exactly that directory enumerated, no sidecar/media reads,
  no descendant enumeration, no writes to media. Fresh adds/removes visible without
  a scan; empty, mixed, multi-root, and inaccessible folders behave explicitly.
- Select a file/folder: local metadata loads/caches only for that selection; editing
  or removing a sidecar is reflected without a library scan.
- Play through standard APIs and Web/native clients: direct/range/transcoded media,
  local subtitles, stop/restart/resume, stable bookmarks after cache eviction.
- Unsupported catalog requests do not crash clients or cause recursive work.
- Unauthorized IDs, traversal paths, and link escapes cannot expose another root.
- Network traps/capture show no internet attempts during setup, idle/startup, browse,
  details, playback, refresh, and formerly online settings/endpoints.
- Simplified UI and final Windows ZIP pass clean-profile end-to-end tests. Real-media
  tests stay CLI/headless and use only safe local copies; never write to Z:\Media.

The user has deferred native Apple-device UI validation. That remains explicitly
unverified, not a reason to claim Apple playback coverage. The current Android TV
audiobook auto-seek limitation must be retested/documented without pretending the
server can change every behavior of an unmodified client.

## Implementation checkpoint (not a release)

The live reader, root configuration, and opaque ID address book are implemented.
The address book stores only `Id`, `RootId`, and `RelativePath` for encountered
entries. It has no membership, metadata, search, or child-query API; listings
always enumerate the selected directory. No address-book rows are created by
mounting or startup. Group configuration is independent of media content.

The authenticated Jellyfin folder routes now use this path. Generic catalog
lists return valid empty results, and individual catalog objects can redirect to
the virtual "Use Folder View" root. Upstream route authorization remains in
place; folder access is checked before filesystem access. Legacy rating/tag
restrictions fail closed until an administrator replaces them with explicit
folder permissions. Unsupported physical file formats remain visible as
non-playable DTOs (Jellyfin has no generic File kind).

Startup scan queuing, legacy library validation, watcher ingestion, external
plugin loading, background recording and NFO bookmark writing are disabled.
Scheduled tasks are restricted to private-state housekeeping. Factory-created
HTTP clients are denied before any transport or DNS operation. This is NOT yet
proof of full product offline behavior: raw sockets, helper subprocesses, remote
media paths, remaining settings/endpoints and bundled web still require audit.

The HTTP regression mounts a fixture with 1,000 hidden descendants and locked
media/NFO files, verifies no descent/content reads/catalog growth, checks both
current and legacy browse routes, and observes additions/removals without refresh.
Component tests also cover restart identity, removed roots, access checks,
no-op refresh, directory links and HTTP transport denial.

Selected-item playback is now implemented without catalog rows. Ordinary resolution
reads attributes only. Explicit details parse bounded local NFO/XML and discover
exact-name local artwork; playback probes only the chosen media and discovers its
matching immediate text-subtitle siblings. Probe/NFO caches are bounded and disposable.
Metadata edits/removal and subtitle edits are observed on subsequent selection.
Artwork URLs and metadata URLs are never followed. Images use the upstream local
image processor; subtitles can be delivered as WebVTT or burned into video.

Resume/favorites live in `live-user-state.db`, independently of the old catalog and
all disposable caches. Stable path IDs, per-user positions, stream preferences and
last-known playback duration survive restart/cache eviction. Short audio chapters
have no minimum-duration or percentage cutoff. Unknown duration does not mean
completed; a missing position report does not erase progress. Renaming a library
does not change file identity; moving/renaming the actual file still changes its ID.

The API capability filter blocks legacy media deletes, sidecar/image/subtitle/lyric
writes, metadata editing, online searches/downloads, plugin administration, raw
configuration writes, remote playback commands and unknown feature families. Known
catalog reads return their declared empty response shapes or the navigable folder
fallback. Stale catalog IDs cannot enter exposed playback routes. Folder played-state
updates apply to that folder only, never recursively visit descendants. Providers,
savers, external source providers and plugins are not initialized. Only the four
private-state housekeeping task types are instantiated. Media roots cannot overlap
configured cache, log, metadata, configuration, transcode or private data paths.

Native input arguments allow file transport and self-contained media demuxers only.
Disguised HLS/concat inputs are rejected with NoCompatibleStream. Text subtitle
burn-in uses a managed normalized ASS cache file with the libass-only filter: the
generic subtitles filter's independent demuxer is not allowed to bypass input
restrictions. This is additional defense, NOT completion of the whole-product
offline/security audit. Path validation rejects existing root/ancestor reparse
points, but atomic check-to-open/race protection still needs implementation.

Authenticated HTTP tests now exercise real m4b/MP4 probing, direct bytes, byte
ranges, MP3 transcoding, HLS segments with subtitle burn-in, local artwork and
WebVTT. They verify bookmarks and durations after cache clearing and a complete
host restart with the same profile, and unchanged source media/artwork/subtitles.
Renamed playlist/concat media and a disguised subtitle cannot reach a local TCP
trap. The no-scan test also exercises rejected write/download APIs and refresh
notifications while media/NFO content is locked and descendant access is tracked.
These are isolated synthetic fixtures, not the user's installed profile or Z:\Media.

Validation at this checkpoint: API 196 passed; server implementations 1,007 passed
and 17 skipped; controller 209 passed; server 29 passed; media encoding 100 passed
and 1 skipped; the three live HTTP scenarios passed with real native helpers.

Still required before release: legacy root/bookmark migration without tree walks;
remaining query/filter compatibility and authorization/path-open audits; complete
native-helper/server/browser outbound verification; simplified bundled web UI;
updated end-to-end/native-client regression; and clean/upgrade Windows packaging.
The old scan-oriented integration suite is not yet adapted or claimed passing.
The installed server and previous release ZIP have not been replaced by this
incomplete branch. No real Apple-device UI coverage has been added.
