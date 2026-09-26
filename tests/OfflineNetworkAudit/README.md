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

Native module observers hook Winsock connect/WSAConnect, dynamically obtained
ConnectEx/AcceptEx, name-based connections, UDP sends, synchronous/asynchronous
address resolution, DNS query APIs, WinHTTP/WinInet request entry points and explicit
UNC CreateFileW opens. Hooks record attempts before results, including unsuccessful
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

## Limits

This is scoped evidence for the exercised Windows application/native-helper paths,
not an OS-wide packet capture or proof against malicious replacement binaries,
drivers or administrators. Incoming client connections and passive discovery replies
are allowed by the product contract; the isolated test disables shared-port discovery
to avoid interfering with a user's server. Explicit UNC opens are detected, but
mapped-drive/SMB redirector behavior and private-storage aliases require separate
validation. Native client UI compatibility is also a separate release gate.
