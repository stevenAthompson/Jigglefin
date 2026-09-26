'use strict';

// Test-only, process-local observation. No OS-wide capture or firewall changes.
// Call records contain destinations, never HTTP bodies, credentials or file data.
const installed = new Set();
function record(kind, api, destination) { send({ kind, api, destination }); }
function textAt(pointer, wide) {
  if (pointer.isNull()) return null;
  return wide ? pointer.readUtf16String() : pointer.readUtf8String();
}
function endpoint(address) {
  if (address.isNull()) return null;
  const family = address.readU16();
  const port = (address.add(2).readU8() << 8) | address.add(3).readU8();
  if (family === 2) return { family, port, ip: [4, 5, 6, 7].map(offset => address.add(offset).readU8()).join('.') };
  if (family === 23) return { family, port, ip: Array.from({ length: 8 }, (_, index) => ((address.add(8 + index * 2).readU8() << 8) | address.add(9 + index * 2).readU8()).toString(16)).join(':') };
  return { family };
}
function attach(address, api, callback) {
  if (!address || installed.has(address.toString())) return;
  installed.add(address.toString());
  Interceptor.attach(address, { onEnter(args) {
    try { callback(args); } catch (error) { send({ kind: 'hook-error', api, error: String(error) }); }
  } });
  send({ kind: 'installed', api });
}
const observer = Process.attachModuleObserver({ onAdded(module) {
  try {
    const name = module.name.toLowerCase();
    const hook = (api, callback) => attach(module.findExportByName(api), name + '!' + api, callback);
    if (name === 'ws2_32.dll') {
      for (const api of ['connect', 'WSAConnect']) hook(api, args => record('connect', api, endpoint(args[1])));
      hook('sendto', args => record('datagram', 'sendto', endpoint(args[4])));
      hook('WSASendTo', args => record('datagram', 'WSASendTo', endpoint(args[5])));
      for (const api of ['getaddrinfo', 'GetAddrInfoW', 'GetAddrInfoExA', 'GetAddrInfoExW', 'gethostbyname'])
        hook(api, args => record('resolve', api, textAt(args[0], api.endsWith('W'))));
      for (const api of ['WSAConnectByNameA', 'WSAConnectByNameW'])
        hook(api, args => record('connect-name', api, textAt(args[1], api.endsWith('W'))));
      // .NET obtains ConnectEx/AcceptEx as extension-function pointers, not exports.
      const ioctl = module.findExportByName('WSAIoctl');
      if (ioctl && !installed.has(ioctl.toString())) {
        installed.add(ioctl.toString());
        Interceptor.attach(ioctl, {
          onEnter(args) {
            if (args[1].toUInt32() === 0xc8000006 && args[3].toUInt32() === 16 && !args[2].isNull()) {
              this.guid = args[2].readU32(); this.output = args[4];
            }
          },
          onLeave(result) {
            if (result.toInt32() !== 0 || !this.output) return;
            try {
              const target = this.output.readPointer();
              if (this.guid === 0x25a207b9) attach(target, 'ConnectEx', args => record('connect', 'ConnectEx', endpoint(args[1])));
              if (this.guid === 0xb5367df1) attach(target, 'AcceptEx', () => record('incoming-accept', 'AcceptEx', null));
            } catch (error) { send({ kind: 'hook-error', api: 'WSAIoctl', error: String(error) }); }
          }
        });
        send({ kind: 'installed', api: 'ws2_32.dll!WSAIoctl' });
      }
    }
    if (name === 'dnsapi.dll') {
      for (const api of ['DnsQuery_A', 'DnsQuery_W', 'DnsQuery_UTF8']) hook(api, args => record('resolve', api, textAt(args[0], api.endsWith('_W'))));
      hook('DnsQueryEx', args => record('resolve', 'DnsQueryEx', textAt(args[0].add(Process.pointerSize).readPointer(), true)));
    }
    if (name === 'winhttp.dll') hook('WinHttpSendRequest', () => record('http', 'WinHttpSendRequest', null));
    if (name === 'wininet.dll') {
      for (const api of ['HttpSendRequestA', 'HttpSendRequestW', 'InternetOpenUrlA', 'InternetOpenUrlW']) hook(api, () => record('http', api, null));
    }
    if (name === 'kernelbase.dll') {
      hook('CreateFileW', args => {
        const name = textAt(args[0], true);
        if (name && (/^\\\\\?\\UNC\\/i.test(name) || (/^\\\\/.test(name) && !/^\\\\[?.]\\/.test(name))))
          record('unc-file', 'CreateFileW', name);
      });
    }
  } catch (error) { send({ kind: 'hook-error', api: module.name, error: String(error) }); }
} });
send({ kind: 'ready', architecture: Process.arch, executable: Process.mainModule.path });
