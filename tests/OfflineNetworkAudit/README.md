# Windows process-local offline audit

This development-only harness observes newly created, hidden test processes and
their descendants from startup. It never attaches to an existing process, captures
other applications' traffic, changes firewall rules or requests elevation. Frida is
not included in the server package. See its [installation documentation](https://frida.re/docs/installation/),
[module observers/interceptors](https://frida.re/docs/javascript-api/) and
[child-gating example](https://github.com/frida/frida-python/blob/main/examples/child_gating.py).

## Run

From the repository root, with the .NET 10 SDK, prepared FFmpeg and the existing
`tests/WebClientSmoke` Playwright installation:

```powershell
py -3.13 -m venv publish/offline-audit-venv
./publish/offline-audit-venv/Scripts/python.exe -m pip install -r tests/OfflineNetworkAudit/requirements.txt
./scripts/audit-offline-win.ps1
# Or audit the actual self-contained package and its PowerShell launcher:
./scripts/audit-offline-win.ps1 -PackageDirectory publish/<new-package>
```

The development dependency installation may download packages. The installed
Jigglefin product does not include or invoke these tools.

## What is proved

First, a separate positive control resolves **localhost**, performs loopback TCP
and UDP, and starts a real FFprobe child that requests a loopback HTTP URL. The
runner requires the actual managed/native calls and incoming accepts to appear.
It cannot pass merely because instrumentation produced an empty file.

The server then runs the complete own-UI workflow with native observation:
setup, immediate-folder browsing, local NFO/art/subtitles, direct/range/HLS playback,
seeking, accounts, permissions, cache eviction, unavailable mounts and restart/resume.
Old configuration deliberately includes an enabled remote plugin repository, enabled
auto-updates, a remote tuner and a hostname-based proxy. Requests exercise remote
search/download/plugin/configuration and refresh endpoints with a hostile Host
header. Three files disguised as playable media contain HLS/concat references to
a loopback trap; NFO artwork/trailer URLs point to that trap too. None may connect.
Legacy logging settings deliberately point a file sink into synthetic media; that
destination must be ignored and the media snapshot must remain unchanged.
Mount requests also try to expose the private profile through local-share aliases,
including a deliberately unresolvable host and a missing 8.3-spelled tail. They must be rejected from the local
share configuration alone, with no attempted lookup/open of the supplied host.
An allowed but unreachable short-spelled root is retained through setup, home views
and restart without browsing it; none may contact that root. Its presence is verified
after restart before it is removed from the owned test profile.

Native module observers hook Winsock connect/WSAConnect, dynamically obtained
ConnectEx/AcceptEx, name-based connections, UDP sends, synchronous/asynchronous
address resolution, DNS query APIs, WinHTTP/WinInet request entry points and explicit
UNC opens, attribute reads, directory enumeration and short/long-name expansion.
Both kernel32 and kernelbase exports are observed (deduplicating shared addresses);
.NET's short-name expansion was missed when only kernelbase was observed.
Hooks record attempts before results, including unsuccessful
calls. They do not deny calls, so the test cannot mistake injected blocking for
product behavior. Browser interception separately records and rejects non-server
requests and requires zero attempts/CSP violations.

Every observed process has an executable path/hash and ready event; native helpers
must have network hooks installed. Missing reports, observer errors, nonzero exits,
outbound attempts or forced child cleanup fail the run. Retained Windows process
handles and a kill-on-close job contain cleanup to the test process tree. The
controller pipe is polled without a blocking Python I/O thread; losing the controller
terminates only its owned tree and cannot produce a pass.

Successful runs retain `web-smoke-report.json`, individual `native-network-N.json`
reports and screenshots in their own `%TEMP%\jigglefin-folder-web-*` fixture. The
positive-control report is in `%TEMP%\jigglefin-network-audit-*`. Synthetic media
must remain byte/timestamp identical. No real profile or `Z:\Media` is used.

## Pure configuration-path normalization

Windows .NET `Path.GetFullPath` may expand 8.3 names through a filesystem call.
Configuration/address identities instead use lexical `GetFullPathNameW`. Selected
reads may resolve paths normally. Private-storage overlap checks inspect short-name
aliases of the **private** path only, never expand the supplied media location.

```powershell
./scripts/audit-path-normalization-win.ps1 `
  -BaselineAssembly publish/Jigglefin-live-share-check-20260926-r1/Emby.Server.Implementations.dll `
  -CurrentAssembly publish/<new-package>/Emby.Server.Implementations.dll
```

This loads each implementation's actual normalizer in a separate observed process
using an owned existing short-spelled directory. It requires at least one native
`GetLongPathNameW` call from the baseline and zero from the new implementation,
alongside clean exits and no observer errors/outbound attempts. An observer that
misses both calls cannot pass. Existing volume 8.3 support is required; the test
does not enable it or change a volume setting.

## Limits

This is scoped evidence for the exercised Windows application/native-helper paths,
not an OS-wide packet capture or proof against malicious replacement binaries,
drivers or administrators. Incoming client connections and passive discovery replies
are allowed by the product contract; the isolated test disables shared-port discovery
to avoid interfering with a user's server. Explicit UNC opens are detected, but
mapped-drive/SMB redirector behavior and private-storage aliases require separate
validation. Native client UI compatibility is also a separate release gate.
