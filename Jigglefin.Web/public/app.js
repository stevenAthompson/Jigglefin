'use strict';

// No discovery of other servers, external URLs, analytics, CDN assets or service
// workers. This small client speaks the same playback APIs as Jellyfin clients.
const $ = id => document.getElementById(id);
const serverBase = new URL('../', location.href);
const storageKey = `jigglefin:${serverBase.pathname}:`;
const ticksPerSecond = 10000000;
const randomId = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
const deviceId = localStorage.getItem(storageKey + 'device') || randomId();
localStorage.setItem(storageKey + 'device', deviceId);
let token = localStorage.getItem(storageKey + 'token');
let me, setup = false, roots = [], configuredRoots = [], accounts = [], currentItems = [], selected;
let routeVersion = 0, selectionVersion = 0, playVersion = 0, playback = null;
let reportQueue = Promise.resolve();
const player = $('player');
player.disableRemotePlayback = true;

function localUrl(value) {
  const url = new URL(value, serverBase);
  if (url.origin !== serverBase.origin || !url.pathname.startsWith(serverBase.pathname)) throw new Error('An external resource was blocked. Jigglefin uses this server only.');
  return url;
}
function authHeader(accessToken = token) {
  return `MediaBrowser Client="Jigglefin Web", Device="Browser", DeviceId="${deviceId}", Version="0.1.0"${accessToken ? `, Token="${accessToken}"` : ''}`;
}
async function api(path, { method = 'GET', body, accessToken = token, keepalive = false } = {}) {
  const headers = { Authorization: authHeader(accessToken), Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(localUrl(path), { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', credentials: 'omit', keepalive });
  if (!response.ok) {
    let message = response.status === 401 ? 'Your session expired. Please sign in again.' : response.status === 403 ? 'This account does not have permission for that action.' : response.status === 404 ? 'This item is no longer available or this account cannot open it.' : `The server could not complete the request (${response.status}).`;
    const contentType = response.headers.get('content-type') || '';
    if (contentType.includes('json')) {
      const error = await response.json().catch(() => null);
      if (typeof error === 'string') message = error;
      else if (error?.detail) message += ` ${error.detail}`;
    }
    const error = new Error(message); error.status = response.status; throw error;
  }
  return response.status === 204 ? null : response.json();
}
function assetUrl(path, accessToken = token) {
  const url = localUrl(path); url.searchParams.set('ApiKey', accessToken); return url.href;
}
function notice(message, error = false) {
  $('notice').textContent = message; $('notice').hidden = !message; $('notice').classList.toggle('error', error);
}
function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}
function link(label, hash) { const anchor = node('a', label); anchor.href = hash; return anchor; }
function on(id, event, handler) {
  $(id).addEventListener(event, async e => {
    if (event === 'submit') e.preventDefault();
    const button = event === 'submit' ? e.target.querySelector('button[type=submit]') : e.currentTarget.tagName === 'BUTTON' ? e.currentTarget : null;
    if (button) button.disabled = true;
    try { await handler(e); } catch (error) { notice(error.message, true); }
    finally { if (button) button.disabled = false; }
  });
}
function time(seconds) {
  seconds = Math.max(0, Math.floor(seconds || 0));
  const hours = Math.floor(seconds / 3600), minutes = Math.floor(seconds / 60) % 60;
  return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}` : `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
}
function kind(item) { return item.LocationType === 'Offline' ? 'Unavailable folder' : item.IsFolder ? 'Folder' : item.MediaType === 'Audio' ? (item.Type === 'AudioBook' ? 'Audiobook' : 'Audio') : item.MediaType === 'Video' ? 'Video' : item.MediaType === 'Photo' ? 'Image' : 'File'; }
function playable(item) { return !item.IsFolder && ['Audio', 'Video'].includes(item.MediaType); }
function setToken(value) { token = value; if (value) localStorage.setItem(storageKey + 'token', value); else localStorage.removeItem(storageKey + 'token'); }
function showAuth(firstTime) {
  setup = firstTime; $('auth').hidden = false; $('workspace').hidden = true; $('account-nav').hidden = true;
  $('auth-title').textContent = firstTime ? 'Make yourself at home.' : 'Sign in';
  $('auth-description').textContent = firstTime ? 'Create the administrator account. You can add your media folders next.' : 'Your files and your place, on this server.';
  $('auth-submit').textContent = firstTime ? 'Create local server' : 'Sign in';
  $('server-name-label').hidden = !firstTime;
  $('password').autocomplete = firstTime ? 'new-password' : 'current-password';
  $('password').minLength = firstTime ? 8 : 0;
}
async function enter() {
  me = await api('Users/Me');
  $('auth').hidden = true; $('workspace').hidden = false; $('account-nav').hidden = false;
  $('password').value = ''; $('account-name').textContent = `Signed in as ${me.Name}`;
  await loadRoots(); await route();
}
async function loadRoots() {
  roots = (await api('UserViews')).Items || [];
  $('root-links').replaceChildren(...roots.map(root => link(root.Name, `#/folder/${root.Id}`)));
}
function currentRoute() { return location.hash.slice(1).split('/').filter(Boolean); }
async function route() {
  if (!me) return;
  const version = ++routeVersion;
  ++selectionVersion; selected = null; $('details').hidden = true;
  const [page, id] = currentRoute();
  $('settings-view').hidden = page !== 'settings'; $('browser-view').hidden = page === 'settings';
  for (const anchor of document.querySelectorAll('.sidebar a')) { if (anchor.hash === (location.hash || '#/')) anchor.setAttribute('aria-current', 'page'); else anchor.removeAttribute('aria-current'); }
  if (page === 'settings') { await showSettings(version); return; }
  $('file-search').value = ''; $('folder-description').hidden = true;
  $('file-list').replaceChildren(); $('empty-message').hidden = true; $('file-count').textContent = 'Reading…';
  let items, title, ancestors = [];
  if (page === 'folder' && id) {
    // Only the selected folder's details plus one immediate directory listing.
    const [folder, listing, parents] = await Promise.all([api(`Items/${encodeURIComponent(id)}`), api(`Items?parentId=${encodeURIComponent(id)}`), api(`Items/${encodeURIComponent(id)}/Ancestors`)]);
    if (version !== routeVersion) return;
    title = folder.Name; items = listing.Items || []; ancestors = [...parents].reverse().filter(parent => parent.Id.replaceAll('-', '') !== '530b1635c4cd4a01a68bcdd43c1c0f92');
    $('folder-description').textContent = folder.Overview || ''; $('folder-description').hidden = !folder.Overview;
  } else if (page === 'resume') {
    const result = await api('UserItems/Resume'); if (version !== routeVersion) return;
    title = 'Pick up where you left off'; items = result.Items || [];
  } else {
    await loadRoots(); if (version !== routeVersion) return;
    title = 'Your folders'; items = roots;
  }
  if (version !== routeVersion) return;
  currentItems = items; $('folder-title').textContent = title;
  $('location-kind').textContent = page === 'resume' ? 'SAVED PLACES' : 'LIVE FILESYSTEM';
  const crumbs = [link('Folders', '#/')];
  for (const parent of ancestors) crumbs.push(node('span', '/'), link(parent.Name, `#/folder/${parent.Id}`));
  if (page) crumbs.push(node('span', '/'), node('span', title));
  $('breadcrumbs').replaceChildren(...crumbs); renderList();
}
function renderList() {
  const search = $('file-search').value.toLocaleLowerCase();
  const descending = $('file-sort').value === 'desc' ? -1 : 1;
  const items = currentItems.filter(item => item.Name.toLocaleLowerCase().includes(search)).toSorted((a, b) => Number(Boolean(b.IsFolder)) - Number(Boolean(a.IsFolder)) || descending * a.Name.localeCompare(b.Name, undefined, { numeric: true, sensitivity: 'base' }));
  $('file-list').replaceChildren(...items.map(item => {
    const button = node('button', undefined, `file-row${item.IsFolder ? ' folder' : ''}`);
    button.setAttribute('aria-label', `${item.IsFolder ? 'Open folder' : 'Select file'} ${item.Name}`);
    button.setAttribute('aria-pressed', String(selected?.Id === item.Id));
    const icon = node('span', item.IsFolder ? 'DIR' : kind(item).slice(0, 3).toUpperCase(), 'file-icon'); icon.setAttribute('aria-hidden', 'true');
    const label = node('span', undefined, 'file-label'); label.append(node('span', item.Name, 'file-name'));
    const position = item.UserData?.PlaybackPositionTicks || 0;
    label.append(node('span', `${kind(item)}${position ? ` · Resume at ${time(position / ticksPerSecond)}` : ''}${item.UserData?.IsFavorite ? ' · Favorite' : ''}`, 'file-extra'));
    button.append(icon, label, node('span', item.IsFolder ? '›' : '···', 'file-arrow'));
    button.addEventListener('click', () => item.IsFolder ? (location.hash = `#/folder/${item.Id}`) : select(item).catch(error => notice(error.message, true)));
    const row = document.createElement('div'); row.setAttribute('role', 'listitem'); row.append(button); return row;
  }));
  $('file-count').textContent = `${items.length} ${items.length === 1 ? 'entry' : 'entries'}`;
  $('empty-message').hidden = items.length !== 0;
  $('empty-message').textContent = search ? 'No matching filenames in this folder.' : currentRoute()[0] === 'resume' ? 'Nothing unfinished yet. Your place will appear here after playback.' : currentRoute()[0] !== 'folder' ? (me.Policy.IsAdministrator ? 'No folders yet. Add a folder group in Settings.' : 'No folders are available to this account. Ask the administrator for folder access.') : 'This folder is empty.';
}
async function select(file) {
  const version = ++selectionVersion;
  const details = await api(`Items/${encodeURIComponent(file.Id)}`); if (version !== selectionVersion) return;
  selected = { ...details, FileName: file.FileName || file.Name };
  $('details').hidden = false; $('detail-kind').textContent = kind(selected); $('detail-title').textContent = selected.Name;
  $('detail-filename').textContent = selected.Name !== selected.FileName ? selected.FileName : '';
  $('detail-overview').textContent = selected.Overview || '';
  const position = selected.UserData?.PlaybackPositionTicks || 0;
  $('detail-progress').textContent = position ? `Saved place: ${time(position / ticksPerSecond)}${selected.RunTimeTicks ? ` of ${time(selected.RunTimeTicks / ticksPerSecond)}` : ''}` : selected.UserData?.Played ? 'Finished' : 'Not started';
  $('play-button').hidden = !playable(selected); $('play-button').textContent = position ? `Resume at ${time(position / ticksPerSecond)}` : 'Play';
  $('restart-button').hidden = !playable(selected) || !position;
  $('unsupported-note').hidden = playable(selected) || selected.MediaType === 'Photo';
  $('favorite-button').textContent = selected.UserData?.IsFavorite ? 'Remove favorite' : 'Favorite';
  const art = $('detail-art'); art.hidden = !selected.ImageTags?.Primary;
  if (!art.hidden) art.src = assetUrl(`Items/${selected.Id}/Images/Primary?maxWidth=500&tag=${encodeURIComponent(selected.ImageTags.Primary)}`); else art.removeAttribute('src');
  renderList();
}
function browserProfile(compatible = false) {
  const audio = document.createElement('audio'), video = document.createElement('video');
  const direct = [];
  if (!compatible) {
    if (video.canPlayType('video/mp4; codecs="avc1.42E01E, mp4a.40.2"')) direct.push({ Type: 'Video', Container: 'mp4,mov,m4v', VideoCodec: 'h264', AudioCodec: 'aac,mp3' });
    if (video.canPlayType('video/webm; codecs="vp9, opus"')) direct.push({ Type: 'Video', Container: 'webm', VideoCodec: 'vp8,vp9', AudioCodec: 'vorbis,opus' });
    for (const [mime, container, codec] of [['audio/mpeg', 'mp3', 'mp3'], ['audio/mp4; codecs="mp4a.40.2"', 'mp4,m4a,m4b,mov', 'aac'], ['audio/flac', 'flac', 'flac'], ['audio/ogg; codecs="opus"', 'ogg,opus', 'opus,vorbis'], ['audio/wav', 'wav', 'pcm_s16le,pcm_s24le,pcm_f32le']]) {
      if (audio.canPlayType(mime)) direct.push({ Type: 'Audio', Container: container, AudioCodec: codec });
    }
  }
  return { Name: 'Jigglefin Browser', MaxStreamingBitrate: 100000000, MaxStaticBitrate: 1000000000, DirectPlayProfiles: direct,
    TranscodingProfiles: [{ Type: 'Video', Container: 'ts', Protocol: 'hls', VideoCodec: 'h264', AudioCodec: 'aac', Context: 'Streaming', MaxAudioChannels: '2', MinSegments: 1, SegmentLength: 4, CopyTimestamps: true }, { Type: 'Audio', Container: 'ts', Protocol: 'hls', AudioCodec: 'aac', Context: 'Streaming', MaxAudioChannels: '2', MinSegments: 1, SegmentLength: 4, CopyTimestamps: true }],
    SubtitleProfiles: [{ Format: 'vtt', Method: 'External' }], CodecProfiles: [], ContainerProfiles: [] };
}
function reportBody(context, ended = false) {
  return { ItemId: context.item.Id, MediaSourceId: context.source.Id, PlaySessionId: context.session, PositionTicks: ended ? context.source.RunTimeTicks : Math.max(0, Math.round(context.position * ticksPerSecond)), CanSeek: true, IsPaused: player.paused, IsMuted: player.muted, VolumeLevel: Math.round(player.volume * 100), AudioStreamIndex: context.audioIndex, SubtitleStreamIndex: context.subtitleIndex, PlayMethod: context.method, PlaybackRate: player.playbackRate };
}
function report(context, type, { ended = false, keepalive = false } = {}) {
  const body = reportBody(context, ended);
  reportQueue = reportQueue.catch(() => {}).then(() => api(`Sessions/Playing${type ? '/' + type : ''}`, { method: 'POST', body, accessToken: context.token, keepalive }));
  return reportQueue.then(() => { if (playback === context || !playback) $('save-status').textContent = `Place saved at ${time(body.PositionTicks / ticksPerSecond)}`; }, error => { $('save-status').textContent = 'Place not saved'; notice(`Could not save your place: ${error.message}`, true); throw error; });
}
async function stop({ ended = false, refresh = false } = {}) {
  const context = playback; if (!context) return;
  if (context.ready && Number.isFinite(player.currentTime)) context.position = player.currentTime;
  context.ready = false; playback = null; clearInterval(context.interval);
  if (context.loaded) player.removeEventListener('loadedmetadata', context.loaded);
  player.pause(); context.hls?.destroy(); player.removeAttribute('src'); player.replaceChildren(); player.load(); $('player-panel').hidden = true;
  await report(context, 'Stopped', { ended });
  if (refresh && selected?.Id === context.item.Id) await select(selected);
}
async function play(file, { fromBeginning = false, compatible = false, position, audioIndex } = {}) {
  const version = ++playVersion; await stop(); notice('');
  const item = await api(`Items/${encodeURIComponent(file.Id)}`);
  const start = position ?? (fromBeginning ? 0 : (item.UserData?.PlaybackPositionTicks || 0) / ticksPerSecond);
  // A full VOD timeline lets seeking work without misreporting segment-relative time.
  const info = await api(`Items/${item.Id}/PlaybackInfo`, { method: 'POST', body: { UserId: me.Id, DeviceProfile: browserProfile(compatible), StartTimeTicks: 0, AudioStreamIndex: audioIndex, SubtitleStreamIndex: -1, EnableDirectPlay: !compatible, EnableDirectStream: true, EnableTranscoding: true, AutoOpenLiveStream: false } });
  if (version !== playVersion) return;
  if (info.ErrorCode || !info.MediaSources?.length) throw new Error('This file has no compatible playable stream. It may be unsupported or damaged.');
  const source = info.MediaSources[0];
  const context = { item, source, session: info.PlaySessionId, token, position: start, ready: false, compatible, audioIndex: audioIndex ?? source.DefaultAudioStreamIndex, subtitleIndex: -1 };
  const direct = !compatible && source.SupportsDirectPlay && audioIndex === undefined;
  context.method = direct ? 'DirectPlay' : 'Transcode';
  let url;
  if (direct) url = assetUrl(`${item.MediaType === 'Audio' ? 'Audio' : 'Videos'}/${item.Id}/stream?static=true&mediaSourceId=${encodeURIComponent(source.Id)}`);
  else {
    if (!source.TranscodingUrl) throw new Error('Compatible playback is unavailable for this account or browser.');
    url = assetUrl(source.TranscodingUrl);
    if (!url.includes('.m3u8')) throw new Error('This browser requires a local HLS playback stream.');
  }
  playback = context; $('playing-title').textContent = item.Name; $('play-method').textContent = direct ? 'Direct playback' : 'Compatible playback · local conversion';
  $('player-panel').hidden = false; $('player-panel').classList.toggle('audio', item.MediaType === 'Audio');
  $('save-status').textContent = 'Loading your saved place…';
  player.playbackRate = Number($('playback-rate').value); player.replaceChildren();
  const audios = source.MediaStreams.filter(stream => stream.Type === 'Audio');
  $('audio-select-label').hidden = audios.length < 2;
  $('audio-select').replaceChildren(...audios.map(stream => { const option = node('option', stream.DisplayTitle || stream.Language || `Track ${stream.Index + 1}`); option.value = stream.Index; option.selected = stream.Index === context.audioIndex; return option; }));
  const subtitles = source.MediaStreams.filter(stream => stream.Type === 'Subtitle' && stream.IsTextSubtitleStream);
  const off = node('option', 'Off'); off.value = '-1'; $('subtitle-select').replaceChildren(off);
  for (const stream of subtitles) {
    const option = node('option', stream.DisplayTitle || stream.Title || stream.Language || `Track ${stream.Index + 1}`); option.value = stream.Index; $('subtitle-select').append(option);
    const track = document.createElement('track'); track.kind = 'subtitles'; track.label = option.textContent; track.srclang = stream.Language || 'und';
    track.src = assetUrl(`Videos/${item.Id}/${source.Id}/Subtitles/${stream.Index}/Stream.vtt`); track.dataset.index = stream.Index; player.append(track);
  }
  $('subtitle-select-label').hidden = !subtitles.length;
  // Seed start reporting with the existing bookmark, never a transient media element's zero.
  await report(context, ''); if (playback !== context) return;
  const loaded = () => {
    if (playback !== context || context.ready) return;
    const maximum = Number.isFinite(player.duration) ? Math.max(0, player.duration - .05) : start;
    const target = Math.min(start, maximum);
    if (target > 0 && Math.abs(player.currentTime - target) > .1) player.currentTime = target;
    context.position = target; context.ready = true;
    player.playbackRate = Number($('playback-rate').value);
    $('save-status').textContent = `Starting at ${time(target)}`;
    player.play().catch(() => { if (playback === context) notice('Press Play in the player to begin. Your saved place is ready.'); });
  };
  context.loaded = loaded; player.addEventListener('loadedmetadata', loaded, { once: true });
  if (!direct && Hls.isSupported()) {
    context.hls = new Hls({ enableWorker: false, startPosition: start, xhrSetup: xhr => { xhr.withCredentials = false; } });
    context.hls.on(Hls.Events.ERROR, (_event, data) => { if (data.fatal && playback === context) notice('Compatible playback stopped. Your saved place is kept. Try reopening the file.', true); });
    context.hls.loadSource(url); context.hls.attachMedia(player);
  } else if (!direct && !player.canPlayType('application/vnd.apple.mpegurl')) {
    await stop(); throw new Error('This browser cannot play the local HLS stream. Try a standard Jellyfin client.');
  } else player.src = url;
  context.interval = setInterval(() => { if (playback === context && context.ready && !player.paused && !player.seeking) report(context, 'Progress').catch(() => {}); }, 5000);
}
player.addEventListener('timeupdate', () => { if (playback?.ready && !player.seeking && Number.isFinite(player.currentTime)) playback.position = player.currentTime; });
player.addEventListener('pause', () => { if (playback?.ready && !player.ended) report(playback, 'Progress').catch(() => {}); });
player.addEventListener('seeked', () => { if (playback?.ready) { playback.position = player.currentTime; report(playback, 'Progress').catch(() => {}); } });
player.addEventListener('error', () => { if (playback) notice('The browser could not play this stream. Try “Use compatible playback”; your saved place is kept.', true); });
player.addEventListener('ended', async () => { try { await stop({ ended: true, refresh: true }); } catch (error) { notice(error.message, true); } });
document.addEventListener('visibilitychange', () => { if (document.hidden && playback?.ready) report(playback, 'Progress', { keepalive: true }).catch(() => {}); });
window.addEventListener('pagehide', () => { if (playback?.ready) report(playback, 'Progress', { keepalive: true }).catch(() => {}); });

async function showSettings(version = routeVersion) {
  $('admin-settings').hidden = !me.Policy.IsAdministrator;
  if (!me.Policy.IsAdministrator) return;
  [configuredRoots, accounts] = await Promise.all([api('Library/VirtualFolders'), api('Users')]); if (version !== routeVersion) return;
  $('configured-roots').replaceChildren(...configuredRoots.map(root => {
    const row = node('div', undefined, 'root-setting'), description = node('div'); description.append(node('strong', root.Name), node('p', (root.Locations || []).join('\n'), 'muted'));
    const enabled = root.LibraryOptions?.Enabled !== false;
    if (!enabled) description.append(node('p', 'Disabled · files and saved places are kept.', 'muted'));
    const toggle = node('button', enabled ? 'Disable' : 'Enable'); toggle.setAttribute('aria-label', `${enabled ? 'Disable' : 'Enable'} folder group ${root.Name}`);
    toggle.addEventListener('click', async () => { try { await api(`Jigglefin/Folders/${root.ItemId}/Enabled?enabled=${!enabled}`, { method: 'POST' }); await loadRoots(); await showSettings(); notice(`Folder group ${enabled ? 'disabled' : 'enabled'}. Files and saved places are unchanged.`); } catch (error) { notice(error.message, true); } });
    const remove = node('button', 'Remove'); remove.setAttribute('aria-label', `Remove folder group ${root.Name}`);
    remove.addEventListener('click', async () => { if (!confirm(`Remove “${root.Name}” from Jigglefin? Your files will not be deleted.`)) return; try { await api(`Library/VirtualFolders?name=${encodeURIComponent(root.Name)}`, { method: 'DELETE' }); await loadRoots(); await showSettings(); notice('Folder group removed. Its files were not changed.'); } catch (error) { notice(error.message, true); } });
    const controls = node('div', undefined, 'action-row'); controls.append(toggle, remove); row.append(description, controls); return row;
  }));
  const active = $('user-select').value;
  $('user-select').replaceChildren(...accounts.map(user => { const option = node('option', user.Name + (user.Policy.IsAdministrator ? ' · administrator' : '')); option.value = user.Id; return option; }));
  $('user-select').value = accounts.some(user => user.Id === active) ? active : me.Id;
  renderAccess();
}
function renderAccess() {
  const account = accounts.find(user => user.Id === $('user-select').value); if (!account) return;
  $('access-all').checked = account.Policy.EnableAllFolders;
  $('access-roots').replaceChildren(...configuredRoots.map(root => {
    const label = node('label', undefined, 'check-label'), input = document.createElement('input'); input.type = 'checkbox'; input.value = root.ItemId;
    input.checked = (account.Policy.EnabledFolders || []).some(id => id.replaceAll('-', '') === root.ItemId.replaceAll('-', ''));
    input.disabled = $('access-all').checked; label.append(input, document.createTextNode(root.Name)); return label;
  }));
}
function folderPolicy(policy, all, enabled) {
  return { ...policy, EnableAllFolders: all, EnabledFolders: enabled, BlockedMediaFolders: [], MaxParentalRating: null, MaxParentalSubRating: null, BlockUnratedItems: [], BlockedTags: [], AllowedTags: [], EnableContentDeletion: false, EnableContentDeletionFromFolders: [], EnableSubtitleManagement: false, EnableCollectionManagement: false };
}
on('auth-form', 'submit', async () => {
  notice(''); const name = $('username').value.trim(), password = $('password').value;
  if (setup) {
    await api('Startup/User');
    await api('Startup/User', { method: 'POST', body: { Name: name, Password: password } });
    await api('Startup/Configuration', { method: 'POST', body: { ServerName: $('server-name').value.trim() || 'Jigglefin', UICulture: 'en-US' } });
    await api('Startup/Complete', { method: 'POST' }); setup = false;
  }
  const login = await api('Users/AuthenticateByName', { method: 'POST', body: { Username: name, Pw: password }, accessToken: null });
  setToken(login.AccessToken); await enter();
});
on('signout-button', 'click', async () => {
  ++playVersion; await stop(); await api('Sessions/Logout', { method: 'POST' }); setToken(null); me = null; selected = null; currentItems = [];
  $('username').value = ''; notice(''); showAuth(false);
});
on('settings-button', 'click', () => { location.hash = '#/settings'; });
on('reload-button', 'click', () => route());
on('file-search', 'input', () => renderList()); on('file-sort', 'change', () => renderList());
on('close-details', 'click', () => { ++selectionVersion; selected = null; $('details').hidden = true; renderList(); });
on('play-button', 'click', () => play(selected)); on('restart-button', 'click', () => play(selected, { fromBeginning: true }));
on('favorite-button', 'click', async () => { const item = selected; await api(`UserFavoriteItems/${item.Id}`, { method: item.UserData?.IsFavorite ? 'DELETE' : 'POST' }); await select(item); const listing = currentItems.find(entry => entry.Id === item.Id); if (listing) listing.UserData = selected.UserData; renderList(); });
on('stop-button', 'click', async () => { ++playVersion; await stop({ refresh: true }); });
on('back-button', 'click', () => { if (playback?.ready) player.currentTime = Math.max(0, player.currentTime - 30); });
on('forward-button', 'click', () => { if (playback?.ready) player.currentTime = Math.min(Number.isFinite(player.duration) ? player.duration : Number.MAX_SAFE_INTEGER, player.currentTime + 30); });
on('playback-rate', 'change', () => { player.defaultPlaybackRate = player.playbackRate = Number($('playback-rate').value); });
on('compatible-play-button', 'click', () => { if (playback) return play(playback.item, { compatible: true, position: playback.position, audioIndex: playback.audioIndex }); });
on('audio-select', 'change', () => { if (playback) return play(playback.item, { compatible: true, position: playback.position, audioIndex: Number($('audio-select').value) }); });
on('subtitle-select', 'change', () => {
  if (!playback) return; playback.subtitleIndex = Number($('subtitle-select').value);
  for (const track of player.querySelectorAll('track')) track.track.mode = Number(track.dataset.index) === playback.subtitleIndex ? 'showing' : 'disabled';
});
on('root-form', 'submit', async () => {
  const paths = $('root-paths').value.split(/\r?\n/).map(path => path.trim()).filter(Boolean);
  if (!paths.length) throw new Error('Enter at least one folder location.');
  await api(`Library/VirtualFolders?name=${encodeURIComponent($('root-name').value.trim())}`, { method: 'POST', body: { LibraryOptions: { PathInfos: paths.map(Path => ({ Path })) } } });
  $('root-form').reset(); await loadRoots(); await showSettings(); notice('Folder group added. Nothing was scanned or written to the media folders.');
});
on('password-form', 'submit', async () => { await api(`Users/Password?userId=${me.Id}`, { method: 'POST', body: { CurrentPw: $('current-password').value, NewPw: $('new-password').value } }); $('password-form').reset(); notice('Password changed.'); });
on('user-select', 'change', () => renderAccess());
on('access-all', 'change', () => { for (const input of $('access-roots').querySelectorAll('input')) input.disabled = $('access-all').checked; });
on('access-form', 'submit', async () => {
  const account = accounts.find(user => user.Id === $('user-select').value);
  const enabled = [...$('access-roots').querySelectorAll('input:checked')].map(input => input.value);
  await api(`Users/${account.Id}/Policy`, { method: 'POST', body: folderPolicy(account.Policy, $('access-all').checked, enabled) });
  if (account.Id === me.Id) me = await api('Users/Me');
  await loadRoots(); await showSettings(); notice('Folder access saved.');
});
on('user-form', 'submit', async () => {
  // A new account uses an undisclosed random password until its deny-all folder
  // policy is saved. A failed access update never publishes broad credentials.
  const created = await api('Users/New', { method: 'POST', body: { Name: $('new-username').value.trim(), Password: randomId() + randomId() } });
  await api(`Users/${created.Id}/Policy`, { method: 'POST', body: folderPolicy(created.Policy, false, []) });
  await api(`Users/Password?userId=${created.Id}`, { method: 'POST', body: { NewPw: $('user-password').value } });
  $('user-form').reset(); await showSettings(); $('user-select').value = created.Id; renderAccess(); notice('Account created with no folder access. Choose the folders it may open.');
});
on('clear-cache-button', 'click', async () => { await api('Jigglefin/Cache/Clear', { method: 'POST' }); notice('Selection cache cleared. Saved places and favorites are kept.'); });
window.addEventListener('hashchange', () => route().catch(error => notice(error.message, true)));
$('detail-art').addEventListener('error', () => { $('detail-art').hidden = true; });
(async () => {
  try {
    const info = await api('System/Info/Public', { accessToken: null });
    if (!info.StartupWizardCompleted) { setToken(null); showAuth(true); return; }
    if (token) { try { await enter(); return; } catch (error) { if (error.status !== 401) throw error; setToken(null); } }
    showAuth(false);
  } catch (error) { notice(`Cannot open Jigglefin: ${error.message}`, true); showAuth(false); }
})();
