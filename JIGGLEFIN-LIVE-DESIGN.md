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
mounting or normal startup. A one-time legacy migration restores addresses only for
saved user state and their parent paths, without visiting media. Group configuration
is independent of media content.

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
offline/security audit. Static reparse-point rejection has since been strengthened
with Windows read-path leases, described at the latest checkpoint below.

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

The simplified bundled UI now lives in `Jigglefin.Web`: minimal setup/login,
immediate folder listings, local filename filtering/sorting, selected NFO/artwork,
direct playback and local HLS conversion, text subtitles, audio-track controls,
playback speed, seeking, favorites and resume. Settings contain only passwords,
folder locations, explicit per-account folder access and selection-cache clearing.
New accounts start with no accessible folders. The elevated cache-clear endpoint
does not touch the independent bookmark store. There is no metadata dashboard,
scan button, catalog navigation, plugin manager, online setup or CDN configuration.

Browser scripts/assets, including the pinned hls.js bundle, are local. Same-origin
CSP headers cover server pages, with an additional HTML policy, no-referrer and
disabled worker/remote-playback features. The actual browser test records attempted
external requests before refusing them and asserts zero attempts/CSP violations.
Real startup also exposed remaining similarity-provider construction; similarity
and external search providers are now never instantiated.

The Windows packager now verifies the offline UI asset manifest and includes only
its listed files/licenses. The new `scripts/smoke-live-package-win.ps1` exercises
the extracted self-contained ZIP through the actual Windows PowerShell 5.1 launcher
from outside the package, with spaced profile/package paths, bundled Web/FFmpeg
defaults, a random loopback port and verified child-process/listener ownership.
Help/version/invalid options do not create a profile. Clean setup, fresh adds/removes,
local artwork/NFO, direct and HLS audio/video, seeking/speed, WebVTT, favorites,
password changes, restricted accounts/cache access, safe root removal, and resume
after cache clear and complete server restart passed. Test media bytes/mtimes stayed
identical; the browser made no external requests or unexpected failed HTTP requests.
Desktop/mobile screenshots were inspected. Fixtures were synthetic and isolated.
The ZIP tested at this checkpoint is `publish/Jigglefin-live-ui-check-20260926-r3.zip`;
it is a development artifact, not an upgrade release or deployed installation.

Latest validation: API 196 passed; server implementations 1,007 passed/17 skipped;
server 30 passed; live HTTP browse plus two real-helper playback scenarios passed.
A complete integration-suite run was also attempted: 91 passed, **102 failed**,
3 skipped. Failures include old scan/type-enrichment/sidecar-import expectations,
catalog fallback and blocked-feature status expectations, and test-only controller
interactions with the capability boundary. Each still needs triage and appropriate
replacement coverage; the full CI suite is explicitly NOT green. No tests were
disabled to hide these failures.

Legacy upgrade now reads only private folder configuration and existing saved user
state. Library permission IDs, enabled/disabled groups, offline paths, per-user
bookmarks/favorites/play counts/stream preferences and known durations are preserved.
Only saved paths and their ancestors enter the address book; the old catalog is
left intact, not copied into a new folder-shaped index. Retry imports never overwrite
new live playback state. Conflicting/overlapping configurations fail explicitly.
Cached legacy file IDs need fresh navigation once; they cannot open old catalog paths.
Disabled groups can be enabled from the minimal settings UI without touching media.

Startup code migrations are allowlisted for private account/database work. Legacy
metadata, playlist, trickplay and other media-processing routines are not constructed
or executed. An early storage guard checks legacy/live root configuration before
startup creates private directories, marker files or logs, and rejects overlapping
cache/log/metadata/transcode paths and existing private-path reparse points. These
are lexical/static protections, not a claim of complete atomic path-race protection.

An isolated profile-upgrade HTTP test seeds legacy settings/catalog/user state,
including 1,000 unrelated entries, restricted accounts and disconnected roots. It
starts with media locked and every media stat/enumeration forbidden, preserves
permissions/bookmarks, imports only three saved-path addresses, plays the selected
file through real helpers after restart and retains a newer bookmark on another
restart. Old configuration/media bytes and mtimes remain unchanged. This is a
current-schema simulated legacy profile, NOT yet an actual older release ZIP upgrade.
Latest checks: server 63 passed; implementations 1,010 passed/17 skipped; four live
HTTP scenarios passed with real helpers; headless UI direct/HLS playback, access,
cache eviction, enable/disable and restart/resume passed with zero external browser
requests and unchanged fixture media. Full legacy integration failures remain open.

Compatibility regression triage has now replaced the obsolete plugin/catalog,
configuration-directory and typed-folder playback assumptions with assertions of
the live/offline contract. Creating a disabled group commits it disabled atomically;
display labels never become filesystem paths. Catalog recommendations/theme media
are empty, catalog singleton fallbacks lead to a real authorized folder view,
remote/provider options cannot be opted into, tuner/SyncPlay creation stays blocked,
and lost WebSockets still close their sessions. Plugin pages are 404, SyncPlay lists
are valid empty arrays and branding CSS has an explicit charset. The test-only URL
echo controller uses a separate encoding fixture; the production unknown-controller
boundary is unchanged and directly tested. Direct/range audio and native MP3/HLS
tests navigate physical folders and negotiate playback instead of asking scans to
collapse folders into playable items. Authorized entry DTOs include their real paths.

That integration checkpoint was **139 passed, 51 failed, 3 skipped** (193 total),
down from 102 failures. The remaining obsolete expectations in `FolderFirstLibraryTests`,
`FolderFirstRescanTests`, and `LocalSidecarLibraryTests` have now been replaced with
physical-layout matrices and stronger read-boundary assertions, not disabled.
The shared fixture asserts zero media access at startup, no enumeration on mount,
exactly one immediate-directory enumeration per browse, no child/sidecar stat during
listing, and unchanged legacy catalog row counts. Locked content, exact path sets,
all collection labels, same-named files/folders, DVD/disc/extras layouts, empty folders,
multi-root identities/playback and unauthorized endpoint access remain covered.
See `tests/LIVE-FOLDER-COVERAGE.md` for the mapping and reproducible commands.

Selection-time metadata now supports local OPF as well as additional Kodi/Emby
folder sidecars, nested XML genres and audio album/artist fields. Shared movie/book
metadata applies to a file only when that selected directory has one appropriate
file; a PDF and audiobook together are ambiguous. This bounded immediate check is
only made after selecting an item with an existing shared sidecar. No descendant
search, scan, probe or online lookup is performed. Specific-sidecar precedence,
edits/removal, malformed/oversized XML, external entities, folder identity and image
endpoints have direct HTTP coverage. Folder metadata never collapses its children.

Disconnected locations in multi-root groups retain their configured identities and
an explicit Offline/unavailable label without hiding available roots. Reconnection
appears on the next folder request, with no refresh task. Opening unavailable paths
still fails closed. HTTP, component and actual headless browser tests cover recovery.

Resume requests now support nested-folder scope, media/item-type/name filters,
pagination, omitted user data/counts and per-user active-session exclusion. They
validate only saved addresses, never enumerate media directories. Missing files
retain their saved position but are not advertised; same-prefix sibling folders,
completed/non-playable files and folder state do not appear as resumable media.
Immediate listings apply favorite/played/liked/resumable filters from independent
user state, with filename or saved-state sorting and no metadata hydration.
Metadata-only sort requests safely fall back to filenames. Recursive flags still
cannot turn a browse request into a traversal.

The complete integration suite now passes: **187 passed, 0 failed, 3 skipped**
(190 total), with real FFmpeg configured and no test-family exclusions. The focused
folder/sidecar/refresh replacement run passed all 53 cases before adding the offline
mount and two query cases. Current full-run evidence is in the ignored development
artifact `publish/test-results/live-contract-complete/integration-final.trx`.
Latest component checks: API 200 passed; implementations 1,012 passed/17 skipped;
server 63 passed; controller 209 passed; media encoding 100 passed/1 skipped. The
headless browser passed direct/HLS playback, subtitles, accounts, enable/disable,
disconnected-root recovery, cache clearing and real restart/resume with unchanged
fixture media and zero external browser requests. Desktop/mobile screenshots were
inspected. This is source-build validation, not a new release ZIP or deployment.

Windows selected-media reads now pin each path component from its volume/share
root with non-following, read-only handles. Directory listing holds its directory
lease through enumeration. Sidecar/subtitle streams and direct audio/video/image
responses own their leases through stream disposal; probes, attachment/subtitle
extraction and image decoding are protected while reading. Transcoding jobs retain
the input lease after the HTTP action returns and release it on native exit or
startup failure. Writable private HLS output keeps its separate upstream serving
path; it is not mistaken for immutable source media.

The implementation uses Windows `CreateFileW` sharing and reparse-point semantics
([Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)).
Actual Windows tests exposed that attribute-only handles do not protect a file from
renaming, so the lease requests read access without reading content. Tests verify
ancestor/file replacement and writes are blocked while leased, independent sibling
creation still works, leases release after failure and synchronous/asynchronous
disposal, and real junction targets cannot be opened. Device/extended aliases,
DOS device names, alternate streams and ambiguous path components are rejected
before any filesystem operation. The non-Windows development fallback rejects
existing links but does not claim Windows's atomic sharing guarantees.

A real slow FFmpeg job test observes the actual process running after startup,
verifies input/ancestor replacement is blocked, stops it and verifies replacement
is possible again. Missing-input startup also releases resources and removes the
failed job. The full integration run passed 188 cases (3 existing skips) and the
headless UI again passed direct/HLS playback, subtitles, accounts, mount recovery,
cache clearing and restart/resume with unchanged fixture media and no external
browser attempts. Component checks pass: controller 223; API 201; implementations
1,014 (17 existing skips); server 63; media encoding 100 (1 existing skip). Results
are recorded in `publish/test-results/live-path-lease`. These read-path improvements do **not**
close the remaining drive/share mapping, hardlink/private-storage alias, startup
write-boundary or whole-product network audits. They assume a trusted OS/storage
administrator; a remote storage server's own filesystem behavior is not proven by
local NTFS tests. No release ZIP or installed profile was changed in this checkpoint.

Still required before release: final query/authorization/path-open audits; complete
native-helper/server/browser outbound verification; unmodified native-client
regression; and actual older-release upgrade-profile Windows package validation. Clean-profile packaging
and the simplified browser are now tested, but not substitutes for these gates.
The installed server and previous release ZIP have not been replaced by this
incomplete branch. No real Apple-device UI coverage has been added.
