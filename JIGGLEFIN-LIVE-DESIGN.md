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

### Actual ZIP upgrade and long-path checkpoint

The actual archived `7d5e6d75` Windows release now creates a synthetic older profile
through its own API. Its copied profile is upgraded by the new self-contained ZIP,
not a test-host simulation. Accounts, tokens, disabled/restricted folder permissions,
per-user positions and favorites survive. Startup/import succeeds with all fixture
media content exclusively locked. The old catalog/state remain intact and only
three saved addresses/ancestors are imported. An unavailable book retains its saved
place and reappears after its location reconnects, without a refresh or scan.

A fresh headless browser resumes the imported audiobook at 31 seconds, seeks past
47 seconds and saves. Another server restart keeps that newer position rather than
reimporting 31 seconds; the other account retains its independent 12-second place.
Selection-time NFO, byte-range playback and cache clearing also pass. The original
profile and media bytes/mtimes stay identical. These tests never use the installed
profile or `Z:\Media`.

This checkpoint also caught and fixed a Windows long-path regression in the read
lease: the native open failed beyond `MAX_PATH` even though managed file access
worked. Only already-validated canonical paths receive an internal extended-length
prefix ([Microsoft long-path reference](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation));
caller-provided device/extended aliases are still rejected. Real >300-character
audio/video paths now pass selection, artwork/subtitles, probing, direct/range and
transcoded playback, cache eviction and restart/resume. The full integration suite
passes **190 cases, 0 failures, 3 existing skips**, and all **224 controller cases**
pass. The other complete suites pass too: API 201; implementations 1,014 (17 existing
skips); server 63; media encoding 100 (1 existing skip). That is 1,792 passes,
zero failures and 21 existing skips across these six suites. Test evidence is in
`publish/test-results/live-upgrade`.

Both clean-profile UI and copied-profile upgrade checks passed against the rebuilt
`publish/Jigglefin-live-upgrade-check-20260926-r2.zip`. The own UI's clean-package run
again covered direct/HLS audio/video, subtitles, accounts, disconnected roots and
restart/resume, with zero external browser attempts, unchanged media and clean
shutdown. Successful harnesses retain machine-readable reports and screenshots.

- Older ZIP SHA-256: `B8380C4BF071690B7F87113D54D966DFE13EC87E2FD97E6A55791A78DCE7F630`.
- Tested new ZIP SHA-256: `07EE1C2CA4FF6666FCEBD5F521DE7F622DC3B00DA828D004B98C7F9033055744`.
- Clean UI fixture: `%TEMP%\jigglefin-folder-web-hXncsm`.
- Upgrade fixture: `%TEMP%\jigglefin-zip-upgrade-fe66cebcb56e47edad206cb589e918f2`.

### Server/native-helper outbound checkpoint

The outbound audit found a real bypass of the HTTP transport boundary: host parsing
used DNS for hostname-based proxy settings and request-host selection. Production
host parsing now accepts numeric addresses and the literal `localhost` without a
resolver call; other names fail closed. This does not prevent clients resolving
the server's name on their own side. Bracketed IPv6 parsing also preserves its final
character and honors the configured address families. Forwarded headers are enabled
only for explicitly configured numeric proxies/subnets or literal localhost; invalid
legacy hostnames cannot retain the framework's implicit loopback trust. Six actual
middleware cases cover trusted and rejected peers (three failed before the fix).
All 153 networking tests and 69 server tests pass; the complete HTTP integration run
still passes 190 cases with three existing skips. A real name-resolution event listener has a localhost positive
control and requires zero resolver starts from the production parser.

`scripts/audit-offline-win.ps1` now observes the newly created server/launcher and
every native child from startup using process-local module/function instrumentation.
It does not capture other apps' traffic or change firewall/system tracing settings.
Its positive control must see managed DNS/TCP/UDP, incoming accepts and an actual
FFprobe child making a loopback HTTP request. During the product run it records
attempted Winsock/DNS/HTTP/UNC operations, not just successful connections, and fails
on incomplete observation. The observer does not block calls. Details, exact hook
coverage and limitations are documented in `tests/OfflineNetworkAudit/README.md`.

The complete packaged own-UI workflow passed with zero observed outbound attempts
across both startups, including 56 native helper processes in the proxy-fixed build.
Old enabled plugin/update/tuner settings and a hostname proxy cannot revive online behavior. Legacy download,
search, configuration and refresh endpoints were exercised using a hostile Host
header. Renamed HLS/concat files and NFO remote artwork/trailers could not contact
their loopback trap. Direct/HLS playback, subtitles, account restrictions, cache
eviction, unavailable mounts and restart/resume still work; media bytes/mtimes are
unchanged. Repeated packaged runs pass. A disconnect control verified that losing
the test controller stops only its owned process tree. The harness itself exposed
and fixed a Windows venv process-parent mismatch and an interpreter-shutdown race;
neither was accepted as a passing run.

The tested package is `publish/Jigglefin-live-offline-check-20260926-r2.zip`, SHA-256
`10B8178D6B8E91AAC43E30305F2FF1F164AF66637EE050CFEFA46BC3FA56838F`.
It also passed the actual older-ZIP upgrade harness again. Local evidence is in
`publish/test-results/live-offline-audit`; the proxy-fixed packaged native audit
fixture is `%TEMP%\jigglefin-folder-web-qgHzyG`, and the upgrade fixture is
`%TEMP%\jigglefin-zip-upgrade-ce4e916a77eb4797977aa9ff27e790c8`.
The `proxy-fixed-*` evidence reports include the explicit range-request regression.
Earlier complete native audit fixtures remain available too; they do not substitute
for the rebuilt package after the proxy fix. A repeated complete run with the final
observer also passes in `%TEMP%\jigglefin-folder-web-ULVP6m` (`repeat-*` reports).

Still required before release: final query/authorization/path-open and private-write
audits, especially mapped-drive/LAN-share behavior and private hardlink aliases;
unmodified native-client regression against this live implementation; and final
distribution validation after those changes. The native capture proves the exercised
local-media paths, not kernel SMB redirectors or arbitrary drivers/binaries. Incoming
client connections and passive discovery replies remain permitted by the contract.
The older ZIP migration gate is covered, but not every historical Jellyfin version
or the user's production profile. The installed server and previous release ZIP
have not been replaced. No real Apple-device UI coverage has been added.

### Private-write and drive-alias checkpoint

The private-storage audit reproduced ten concrete failures in the previous build:
hardlinks at six writable private locations, two nested private directory links,
a substituted-drive overlap, and legacy logging redirected into synthetic media.
Those regressions now pass. Startup checks existing **private storage only** for
reparse points and multiply-linked files, before creating markers/logs/databases;
it does not enumerate media or read media content. Private configuration readers
also reject hardlinks before parsing their content. Link counts come from read-only
Windows file-information handles ([Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information)).
An unsafe profile is rejected, not repaired, deleted or silently rewritten.
The live configuration database and rollback journal are checked before SQLite
opens them. Hardlink tests hold their media-side targets exclusively locked;
rejection needs attributes, not a content read or a write to either link.

Root/private-location comparison now consults the local DOS-device namespace to
detect substituted drives and volume aliases without statting media
([Microsoft reference](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-querydosdevicew)).
It runs before mount validation and again for saved-ID/browse access. Tests change
an owned temporary mapping between requests: private data stays inaccessible and
unaffected roots remain available. A dangling network-device mapping still permits
startup with local private storage without resolving/opening its share. Network
provider names are needed only when comparing two network locations; inability to
establish that comparison fails closed. No user drive mapping or share is changed.

Logging uses fixed console/private-file/startup sinks. Legacy JSON/environment
settings can set supported log levels only; arbitrary sink assemblies, paths and
network destinations are not passed to the configurable sink loader. The bundled
logging defaults no longer advertise those options. Tests cover a real redirected
file sink, an unknown requested assembly and retained Debug-level output.

This is not yet the complete storage-alias proof: real SMB/reconnect behavior,
short-name and local-share aliases, and a drive mapping changed during an active
native read still need checking. Startup inspection does not defend against a
storage administrator replacing private files after validation; the trusted
OS/storage-administrator assumption still applies. Final API/native-client and
distribution gates remain open. The installed profile and `Z:\Media` are untouched.

Verification for this checkpoint: all **84 server tests** (15 new storage/logging
cases), **190 integration tests** (3 existing skips), and **1,014 implementation
tests** (17 existing skips) pass. The actual package/launcher passes the complete
headless UI/native-network workflow with legacy logging directed into media,
unchanged media hashes/mtimes, zero external browser attempts and zero observed
outbound calls across 56 native helpers. The older-ZIP upgrade passes again,
including locked-media migration, permissions and independent audiobook progress.

- Final tested ZIP: `publish/Jigglefin-live-storage-check-20260926-r3.zip`.
- SHA-256: `F8295E10A52FFF4A4071033A37F2BE619EDC58EA019D580E7F0A44C1E1B85B8C`.
- Final UI/native fixture: `%TEMP%\jigglefin-folder-web-9YWQWU`.
- Final upgrade fixture: `%TEMP%\jigglefin-zip-upgrade-14c40cd37ac34a54ae22895ac02f4c95`.
- Evidence: `publish/test-results/live-storage-audit`, including the original
  failing `storage-before.trx` and the final `final-*` package reports.

### Stock Android compatibility checkpoint

Unmodified official Android TV 0.19.10 and Android 2.7.3 libre APKs are now exercised
in an owned, windowless Android 34 guest. `tests/NativeClientSmoke` pins their release
hashes, uses a separate ADB server/AVD/profile and synthetic media, denies guest
internet access and checks media hashes/mtimes on cleanup. It does not use the
user's AVD, default ADB listener, desktop input, installed server or `Z:\Media`.
An extrapolated session clock is not accepted as playback proof: the harness
requires actual client-reported durable positions and captures rendered frames.

The tests found and corrected three concrete compatibility failures:

- Android TV constructed anonymous direct-stream URLs. For an authenticated TV
  request whose profile already supports direct playback, negotiation supplies an
  authenticated static-byte URL through the client's alternate stream branch.
  This does not enable anonymous access or actually transcode. Tokens stay in the
  per-request source clone, never in the shared probe cache.
- Immediate video Resume crashed the TV app when it inspected an absent source
  before `PlaybackInfo`. Physical media DTOs now include an identity-only source
  with empty streams. This uses existing directory attributes, without probing or
  reading sidecars. Locked-content/no-enumeration tests retain those assertions;
  selected details may reuse a probe already in cache.
- The phone shell recognizes a `main.*.bundle.js` request as its ready signal and
  injects globals before that script. The small offline UI now uses that bootstrap
  name and an isolated scope. Its hardware-Back hook saves/stops playback, closes
  details, navigates parents and exits at the root. No catalog/cast/download plugins
  are loaded. This tests the official app hosting our WebView player, not native
  ExoPlayer integration, background audio or Android media-session controls.

Video testing also exposed an ignored subtitles-Off choice when a client omitted
`MediaSourceId`: the default local subtitle could be burned into an unnecessary
conversion. A live item has one source, so explicit track choices now apply to
that source without a redundant ID. Four actual HTTP/helper cases cover ordinary
and long-path audio/video. The original failing tests are retained as local evidence.

Finally, complete integration runs exposed an async-void notification callback
outliving service disposal. Shutdown now stops queued work and waits for in-flight
notifications; dedicated queued/in-flight regressions pass, and the full integration
process exits cleanly. This changes notification lifetime, not bookmark persistence.

Android TV folder-audio reopening still starts from zero despite a saved bookmark;
this is an explicitly recorded stock-client limitation, not claimed auto-resume
coverage. TV video stop/immediate Resume and the phone WebView's audiobook
stop/app-restart/Resume have been demonstrated. Apple-device UI testing is still
deferred, and physical Android/background behavior is not established by an emulator.

Source verification: **1,970 passing tests, 21 existing skips**, across implementations
(1,016/17), server (84), controller (224), media encoding (100/1), networking (153),
API (203) and HTTP integration (190/3). The integration and API suites were rerun
after the final track-choice fix. Two Node checks cover shell globals/bootstrap.
The final package's headless Web/native-outbound audit and actual older-ZIP upgrade
also pass: zero external browser requests, zero observed outbound attempts across
55 native helpers/two startups, unchanged synthetic media and clean shutdowns.

- Tested ZIP: `publish/Jigglefin-live-native-check-20260926-r6.zip`.
- SHA-256: `B97624F663D5643AF8667E6A11D869CE59822727D1A5AFDA5835532B45F01D6E`.
- Web/native-network fixture: `%TEMP%\jigglefin-folder-web-FROkbU`.
- Actual older-ZIP upgrade fixture: `%TEMP%\jigglefin-zip-upgrade-c94869cf477347a0a61a6ee9ee90fe78`.
- Official Android guest fixture: `%TEMP%\jigglefin-native-client-pdQiYP`.
- Evidence: `publish/test-results/live-native-client`; retain final `r6-*` reports
  separately from earlier failing regression/driver-diagnostic reports.

Both official-client runs completed on that r6 package without guest crashes.
The phone resumed its audiobook from 7.68 seconds to 15.13 after reopening and
played the video directly with `SubtitleStreamIndex=-1`; the rendered frame confirms
no burned-in text. TV video resumed from 10.65 seconds to 13.24. Owned-fixture
shutdown completed with unchanged media and no cleanup errors. The first phone
driver attempt had not focused the host field; the corrected fresh-login run
verifies focus and entered address rather than accepting that attempt as a pass.

This checkpoint does not close the remaining storage-alias/real-SMB and final
API/path-open review gates described above. The installed profile is unchanged;
the ZIP is a development checkpoint, not production-upgrade approval.

### Local-share aliases and actual SMB checkpoint

Four new regressions reproduced a private-write boundary bypass: a media location
spelled through `\\localhost\C$\...` or `\\127.0.0.1\C$\...` was treated as
unrelated to the same local directory used for cache storage. Both directions now
reject the overlap before startup creates files or a library is added. An unknown
hostname can also be an alias, so the guard consults **only the local share table**
and conservatively compares the same-named local share's path. It never resolves
or contacts the supplied server. This can reject a genuinely remote same-named
share if its relative path would overlap local private storage; that conservative
ambiguity is intentional. Failure to inspect the local table fails closed.
[NetShareGetInfo with a null server](https://learn.microsoft.com/en-us/windows/win32/api/lmshare/nf-lmshare-netsharegetinfo)
provides that local-only lookup. No shares, service configuration or credentials
are created or altered by the implementation or tests.

LanmanRedirector/Mup DOS-device spellings now yield their recorded UNC identity
without asking the provider to reconnect. Tests cover three provider forms plus
the previous dangling-provider case. Real SMB browsing also succeeds through the
existing local share: added/removed files are immediately visible, unavailable
owned directories recover without scanning, and direct reads preserve bytes/mtime.
A separate test creates a non-persistent mapping on a verified unused drive letter,
disconnects only that verified owned mapping, reconnects it, and checks saved IDs.
Cleanup removes that mapping; the user's X:, Y: and Z: mappings are unchanged.
This exercises the actual Windows SMB redirector, not a simulated filesystem. It
does not simulate a NAS/server crash or disconnect another application's transport.

The full selected-file HTTP/native-helper regression now has eight cases: ordinary
and >300-character audio/video paths, both local and SMB. All eight pass, including
selected local metadata/art/subtitles, direct/range/transcoded playback, TV-auth URL
compatibility, explicit subtitles Off, cache eviction and server-restart bookmarks.
SMB cases require `JIGGLEFIN_TEST_LOCAL_SMB=1` and a readable existing local
administrative share; they never enable a share or request elevation. That flag and
the real FFmpeg helper were supplied for the recorded runs.
The complete reruns pass **194 HTTP integration cases** (3 existing skips),
**96 server cases** (including all 27 storage-boundary cases), and **1,016
implementation cases** (17 existing skips), with successful test-process exits.

Two real 8.3 overlap tests already pass on the previous code because .NET expands
short spellings. That is **not** proof that normalization is purely lexical:
[Path.GetFullPath may consult the filesystem](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath).
The implementation comments now state that caveat. The zero-startup-media-access
audit must still address implicit 8.3 expansion, alongside mapping changes during
an active native read and the final authorization/path-open review. These findings
are not being reclassified as completed gates merely because the normal tests pass.

The packaged own-UI/native-outbound workflow additionally attempts private-profile
mounts through localhost and an unresolvable share hostname. Both reject without
contacting the supplied host: zero observed outbound calls across 55 native helpers
and two server startups, zero external browser requests, unchanged synthetic media,
and clean shutdowns. The actual older-ZIP upgrade passes again.

- Tested ZIP: `publish/Jigglefin-live-share-check-20260926-r1.zip`.
- SHA-256: `403EEFCCEB07369D0D9FB7B4103D2EC1B36EB270A931F078BD4E2E5AC4013147`.
- Web/native fixture: `%TEMP%\jigglefin-folder-web-qKnDYi`.
- Upgrade fixture: `%TEMP%\jigglefin-zip-upgrade-16b8ca5667bd4e01a9083d05563ac7bd`.
- Evidence: `publish/test-results/live-alias-audit`, including the four original
  failing `local-share-before.trx` cases and final package reports.

The installed server/profile and `Z:\Media` remain untouched. This remains a
development checkpoint pending the explicitly listed final gates.

### Configuration without filesystem access, including Windows short names

Configuration and saved-address normalization no longer use Windows .NET's
implicit 8.3 expansion. They call the lexical
[GetFullPathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfullpathnamew)
instead; relative IDs/containment also avoid `Path.GetRelativePath`'s implicit
normalization. Selected filesystem reads retain the configured logical spelling
even when Windows returns a long physical name. IDs remain tied to configured path
spelling, not a global physical-file identity: changing between short/long root
spellings is a different root. Already stored long-spelled live roots are unchanged.
The tests do not claim migration of every historical mixture of alias spellings.

Private-write overlap protection is retained by resolving short aliases of the
**private storage** path (or its nearest existing ancestor), never the supplied
media path. Regressions cover whole and mixed short spellings, nonexistent private
tails and a private SUBST drive whose target uses a short spelling. Selected
audio/video HTTP/native-helper tests now have ten cases: ordinary/deep local and
SMB paths, plus two short-spelled roots with direct/conversion and restart/resume.

The expanded native audit then caught a separate issue: AddLibrary/AddPath still
statted the configured root. They now save only validated addresses; unavailable
paths are accepted and checked when opened. Shared HTTP and store tests require
zero media stats as well as zero enumeration for configuration and home queries.
A missing multi-root location is shown as unavailable only during navigation.

The packaged audit keeps an unvisited `\\jigglefin-short-root.invalid\NoSuchShare\MEDIA~1`
root through setup, home views and restart, verifies it is still configured, then
removes it without browsing. The first package failed with two native UNC opens;
the corrected package passes with zero native outbound/UNC attempts over 55 helpers
and two startups, zero external browser attempts, unchanged fixture media and clean
shutdowns. Private-share alias requests with missing short-spelled tails also reject
without contacting the supplied hostname. The actual older-ZIP upgrade passes again.

The separate before/after observer control invokes each build's actual path
normalizer on an existing short-spelled fixture. The baseline makes one native
`GetLongPathNameW` call and the new package makes zero. Both kernel32 and kernelbase
exports must be observed: an earlier kernelbase-only trial missed .NET's call and
is deliberately **not** counted as proof. The reproducible script fails if it
misses the baseline positive control; no volume setting is changed for this test.

- Tested ZIP: `publish/Jigglefin-live-short-check-20260926-r2.zip`.
- SHA-256: `196DD50E38723028431752994E6F93D3CB069C41F3246C9CDB3C52D2928F60B3`.
- Web/native fixture: `%TEMP%\jigglefin-folder-web-0Bln8d`.
- Upgrade fixture: `%TEMP%\jigglefin-zip-upgrade-4764279db5c1474cb9f6fc6d91ddddf5`.
- Native normalization fixture: `%TEMP%\jigglefin-network-audit-normalize-be1f20fdf75b4e7d91b20763a50447a3`.
- Evidence: `publish/test-results/live-short-paths`; final package reports use the
  `r2-` prefix, separate from the original regression/observer failures.

Final source reruns pass **196 integration cases** (3 existing skips), **100
server cases**, and **1,017 implementation cases** (17 existing skips), with clean
test-process exits. Their reports use `r2-final-`. The preceding `r2-Integration`
run exposed one obsolete assertion expecting a root stat during configuration;
it was replaced by the stricter zero-stat assertion, also applied to home queries
and the shared HTTP fixture. No new tests were skipped to obtain this result.

This closes the implicit configuration-time short-name access finding, not the
remaining active-read mapping and final API/path-open/distribution gates. In
particular, pinning an ancestor/file handle does not make a later open by the
original drive letter immune to remapping. That path needs its own regression and
end-to-end consumer fix; it is not being claimed safe from these startup tests.
All tests used owned synthetic fixtures. The user's mappings, installed profile
and `Z:\Media` are unchanged; no development package has been deployed here.

### Stable addresses for active readers

An owned-drive regression reproduced the active-read bug in both managed and real
FFmpeg reads: handles pinned the original file, but subsequent readers opened the
replacement reached through the remapped letter. `LivePathLease.ReadPath` now uses
the opened root's volume GUID, or its UNC address for SMB. All subsequent component
opens use that address. Normal SUBST/mapped-share aliases are expanded before
pinning so they cannot hide path ancestors; an unsupported raw NT device alias
with a path suffix fails closed. The configured address/IDs remain logical and
unchanged. Internal read-address acquisition accepts only a validated volume GUID
extension; configuration and the original untrusted-path entry point still reject it.

The implementation uses
[GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)
with the opened-name flag, avoiding SMB component-normalization queries. A missing
local GUID does not silently fall back to a drive letter. Readers must keep the
lease alive; returning a stable string is not itself authorization or a lifetime lock.

Managed streams/listings, probes, image processing, subtitle/attachment extraction
and transcoding now consume their lease's address. Transcoding rewrites exactly the
expected generated `-i file:` argument before logging/launching; unexpected or
ambiguous commands fail closed. The job retains the lease until native exit. Source
image descriptors are copied, not mutated in the shared item. HTTP Last-Modified
uses the opened stream handle rather than reopening its original pathname.

The refined drive test uses two different valid WAV files: after remapping, the
logical address reaches the replacement while protected managed bytes and FFmpeg's
PCM checksum still match the original. Nested reads, hidden junction ancestors,
raw alias rejection, lock release and native argument binding are also covered.
All ten audio/video/long/short/SMB playback cases and the slow native-job case pass.
Complete suites pass: controller **229**, media encoding **107/1 existing skip**,
integration **196/3**, server **100**, implementations **1,017/17**, API **203** and
Skia **28**. No test-process failures or new skips remain.

- Development ZIP: `publish/Jigglefin-live-stable-read-check-20260926-r1.zip`.
- SHA-256: `62C50D5B0FB26D1348BB86E4BBBEFD4759D621A83C6FCC322CFBA327F1A0FEC4`.
- Own-UI/native fixture: `%TEMP%\jigglefin-folder-web-iPUj78`.
- Actual older-ZIP upgrade: `%TEMP%\jigglefin-zip-upgrade-d06c7b1451594bd59f790f614b221e97`.
- Evidence: `publish/test-results/live-stable-read`; retain `remap-before.trx`
  as the original two failures, separate from final passing results.

The package's complete own-UI/native-outbound and older-ZIP upgrade workflows pass
again, with unchanged synthetic media and no attempted outbound/UNC accesses.
Nothing was deployed and the user's media/profile/mappings were not changed.

Remaining before release: bind the **earlier root/private-storage validation** to
the physical address handed to these leases. In particular, the current browser
and sidecar service can still use the original logical root between separate
stat/read calls. The post-acquisition fix above must not be mistaken for proof of
that entire authorization-to-open handoff. Final API/path-open and distribution
review remain open; the goal is not complete.
