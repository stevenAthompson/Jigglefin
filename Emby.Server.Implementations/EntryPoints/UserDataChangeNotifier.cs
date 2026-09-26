using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EntryPoints
{
    /// <summary>
    /// <see cref="IHostedService"/> responsible for notifying users when associated item data is updated.
    /// </summary>
    public sealed class UserDataChangeNotifier : IHostedService, IDisposable
    {
        private const int UpdateDuration = 500;
        internal const int MaxBatchSize = 2000;

        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILogger<UserDataChangeNotifier> _logger;

        private readonly Dictionary<Guid, Dictionary<Guid, BaseItem>> _changedItems = [];
        private readonly Lock _syncLock = new();

        private Timer? _updateTimer;
        private int _changedItemCount;
        private bool _stopping;
        private Task _pendingUpdates = Task.CompletedTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserDataChangeNotifier"/> class.
        /// </summary>
        /// <param name="userDataManager">The <see cref="IUserDataManager"/>.</param>
        /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
        /// <param name="userManager">The <see cref="IUserManager"/>.</param>
        /// <param name="logger">The logger.</param>
        public UserDataChangeNotifier(
            IUserDataManager userDataManager,
            ISessionManager sessionManager,
            IUserManager userManager,
            ILogger<UserDataChangeNotifier> logger)
        {
            _userDataManager = userDataManager;
            _sessionManager = sessionManager;
            _userManager = userManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _userDataManager.UserDataSaved += OnUserDataManagerUserDataSaved;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _userDataManager.UserDataSaved -= OnUserDataManagerUserDataSaved;
            Timer? timer;
            Task pending;
            lock (_syncLock)
            {
                _stopping = true;
                timer = _updateTimer;
                _updateTimer = null;
                _changedItems.Clear();
                _changedItemCount = 0;
                // Hosted services stop before their dependencies are disposed.
                // Timer.Dispose alone does not wait for async notification work.
                pending = _pendingUpdates;
            }

            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            await pending.ConfigureAwait(false);
        }

        private void OnUserDataManagerUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            lock (_syncLock)
            {
                if (_stopping)
                {
                    return;
                }

                // The window runs from the first change of a batch and is never extended, so a stream
                // of changes that never pauses - a library scan - still closes its batches instead of
                // holding every item it touched alive until the stream stops.
                _updateTimer ??= new Timer(
                    UpdateTimerCallback,
                    null,
                    UpdateDuration,
                    Timeout.Infinite);

                if (!_changedItems.TryGetValue(e.UserId, out Dictionary<Guid, BaseItem>? keys))
                {
                    keys = [];
                    _changedItems[e.UserId] = keys;
                }

                var baseItem = e.Item;

                // Go up one level for indicators
                if (baseItem is not null)
                {
                    Track(keys, baseItem);

                    var parent = baseItem.GetOwner() ?? baseItem.GetParent();

                    if (parent is not null)
                    {
                        Track(keys, parent);
                    }
                }

                // A window long enough to cover a burst still has to give way once the batch is
                // large enough to be worth sending on its own.
                if (_changedItemCount >= MaxBatchSize)
                {
                    _updateTimer.Change(0, Timeout.Infinite);
                }
            }
        }

        private void Track(Dictionary<Guid, BaseItem> keys, BaseItem item)
        {
            var before = keys.Count;
            keys[item.Id] = item;

            if (keys.Count != before)
            {
                _changedItemCount++;
            }
        }

        private void UpdateTimerCallback(object? state)
        {
            lock (_syncLock)
            {
                if (_stopping)
                {
                    return;
                }

                var changes = _changedItems.ToList();
                _changedItems.Clear();
                _changedItemCount = 0;

                if (_updateTimer is not null)
                {
                    _updateTimer.Dispose();
                    _updateTimer = null;
                }

                // Publish the task while holding the same lock as StopAsync.
                var update = SendChangesAsync(changes);
                _pendingUpdates = _pendingUpdates.IsCompleted ? update : Task.WhenAll(_pendingUpdates, update);
            }
        }

        private async Task SendChangesAsync(List<KeyValuePair<Guid, Dictionary<Guid, BaseItem>>> changes)
        {
            try
            {
                foreach (var (userId, changedItems) in changes)
                {
                    await _sessionManager.SendMessageToUserSessions(
                        [userId],
                        SessionMessageType.UserDataChanged,
                        () => GetUserDataChangeInfo(userId, changedItems.Values),
                        default).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unable to send a user-data notification batch");
            }
        }

        private UserDataChangeInfo GetUserDataChangeInfo(Guid userId, IEnumerable<BaseItem> changedItems)
        {
            var user = _userManager.GetUserById(userId)
                ?? throw new ArgumentException("Invalid user ID", nameof(userId));

            return new UserDataChangeInfo
            {
                UserId = userId,
                UserDataList = changedItems
                    .Select(i =>
                    {
                        var dto = _userDataManager.GetUserDataDto(i, user);
                        if (dto is null)
                        {
                            return null!;
                        }

                        dto.ItemId = i.Id;
                        return dto;
                    })
                    .Where(e => e is not null)
                    .ToArray()
            };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
