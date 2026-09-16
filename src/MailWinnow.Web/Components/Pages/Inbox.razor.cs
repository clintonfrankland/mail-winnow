using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MailWinnow.Web.Components.Pages;

public partial class Inbox
{
    [CascadingParameter] public Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = null!;
    [Inject] private IInboxRefreshService RefreshService { get; set; } = null!;
    [Inject] private IInboxDeletionQueue DeletionQueue { get; set; } = null!;
    [Inject] private NavigationCountState CountState { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _listLoadGate = new(1, 1);
    private readonly HashSet<(uint Uid, uint UidValidity)> _locallyRemoved = [];
    private ClaimsPrincipal _user = null!;
    private IReadOnlyList<InboxMessageSummary> _messages = [];
    private InboxMessageContent? _selected;
    private uint? _selectedUidValidity;
    private string? _error;
    private string? _refreshMessage;
    private bool _busy;
    private bool _loadingList;
    private bool _showImages;
    private bool _requestingRefresh;
    private bool _disposed;
    private long _selectionVersion;
    private Task? _refreshTask;
    private string ViewerKey => $"{_selectedUidValidity}:{_selected?.Uid}:{_showImages}";

    private string AttachmentDownloadUrl(InboxAttachment attachment) =>
        $"inbox/attachments/{_selected!.DestinationMailboxId}/{_selected.Uid}/{_selectedUidValidity}/{attachment.Index}?folder={Uri.EscapeDataString(_selected.DestinationFolder!)}&identity={Uri.EscapeDataString(_selected.AttachmentMailboxIdentity!)}";

    protected override async Task OnInitializedAsync()
    {
        _user = (await AuthenticationStateTask).User;
        await LoadLocalInboxAsync();
    }

    private async Task RequestRefreshAsync()
    {
        if (_requestingRefresh || _disposed) return;
        _requestingRefresh = true;
        _error = null;
        _refreshMessage = "Requesting a mail check…";
        try
        {
            // Only a durable database request is awaited here, never source IMAP.
            var request = await RefreshService.RequestAsync(_user, _lifetime.Token);
            if (_disposed) return;
            if (!request.Succeeded)
            {
                _refreshMessage = request.Message;
                _requestingRefresh = false;
                return;
            }
            if (request.MailboxCount == 0)
            {
                _refreshMessage = request.Message;
                _refreshTask = RefreshLocalOnlyAsync();
                return;
            }
            _refreshMessage = "Mail check requested. You can keep reading while your accounts sync in the background.";
            _refreshTask = ObserveRefreshAsync(request.RequestedUtc);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            _refreshMessage = "The mail check could not be requested. Please try again.";
            _requestingRefresh = false;
        }
    }

    private async Task RefreshLocalOnlyAsync()
    {
        await Task.Yield();
        try { await LoadLocalInboxAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _requestingRefresh = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ObserveRefreshAsync(DateTimeOffset requestedUtc)
    {
        // Yield to finish the button event before local IMAP or progress observation.
        await Task.Yield();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        DateTimeOffset? lastInboxChange = null;
        try
        {
            await LoadLocalInboxAsync();
            while (!_lifetime.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                var status = await RefreshService.GetStatusAsync(_user, requestedUtc, _lifetime.Token);
                if (_disposed) return;
                var inboxReloaded = true;
                if (status.LatestInboxChangeUtc != lastInboxChange || status.IsComplete)
                {
                    inboxReloaded = await LoadLocalInboxAsync();
                    lastInboxChange = status.LatestInboxChangeUtc;
                }
                if (status.IsComplete)
                {
                    _refreshMessage = !inboxReloaded
                        ? "Mail check finished, but the displayed inbox could not be updated. Please refresh again."
                        : status.FailedSources > 0 || status.FailedDeliveries > 0
                        ? "Mail check finished, but some accounts or messages could not be processed. Check mailbox status; your other mail is available."
                        : "Mail check finished. New deliveries are shown; mail needing a decision is in Review. Large backlogs continue in later batches.";
                    return;
                }
                _refreshMessage = status.WaitingSources > 0
                    ? "Waiting for the background worker to check your accounts. You can keep using the inbox."
                    : status.SyncingSources > 0
                        ? "Checking your source accounts in the background…"
                        : "Processing new mail in the background…";
                await InvokeAsync(StateHasChanged);
                await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
            }
            if (!_disposed)
                _refreshMessage = "The mail check is still running in the background. You can refresh again later to check progress.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
                _refreshMessage = "The request was saved, but progress could not be checked. Background processing will continue.";
        }
        finally
        {
            _requestingRefresh = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<bool> LoadLocalInboxAsync()
    {
        await _listLoadGate.WaitAsync(_lifetime.Token);
        _loadingList = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using var scope = ScopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IInboxReaderService>().ListAsync(_user, timeout.Token);
            if (_disposed) return false;
            if (!result.Succeeded) { _error = result.Error; return false; }
            // A refresh started before an optimistic deletion must not resurrect it.
            _messages = (result.Value ?? []).Where(message => !_locallyRemoved.Contains((message.Uid, message.UidValidity))).ToArray();
            CountState.SetInboxCount(_messages.Count);
            if (_selected is not null && !_messages.Any(message => message.Uid == _selected.Uid && message.UidValidity == _selectedUidValidity))
            {
                _selected = null;
                _selectionVersion++;
                _busy = false;
                _showImages = false;
            }
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception)
        {
            if (!_disposed) _error = "The local inbox could not be refreshed. Your existing list is still available.";
            return false;
        }
        finally
        {
            _loadingList = false;
            _listLoadGate.Release();
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private void ShowImages() => _showImages = true;

    private async Task SelectAsync(InboxMessageSummary summary)
    {
        if (_disposed) return;
        var selectionVersion = ++_selectionVersion;
        _busy = true;
        _error = null;
        _showImages = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var scope = ScopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IInboxReaderService>().ReadAsync(_user, summary.Uid, summary.UidValidity, timeout.Token);
            if (_disposed || selectionVersion != _selectionVersion) return;
            if (result.Succeeded && _messages.Any(message => message.Uid == summary.Uid && message.UidValidity == summary.UidValidity))
            {
                _selected = result.Value;
                _selectedUidValidity = summary.UidValidity;
            }
            else if (!result.Succeeded) _error = result.Error;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && selectionVersion == _selectionVersion) _error = "The message could not be opened. Please try again."; }
        finally { if (selectionVersion == _selectionVersion) _busy = false; }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected is null || _busy || _disposed) return;
        var deletedIndex = _messages.ToList().FindIndex(message => message.Uid == _selected.Uid && message.UidValidity == _selectedUidValidity);
        if (deletedIndex < 0) return;
        var deleted = _messages[deletedIndex];
        _locallyRemoved.Add((deleted.Uid, deleted.UidValidity));
        _messages = _messages.Where(message => message.Uid != deleted.Uid || message.UidValidity != deleted.UidValidity).ToArray();
        CountState.SetInboxCount(_messages.Count);
        _selected = null;
        var deletionVersion = ++_selectionVersion;
        _showImages = false;
        StateHasChanged();
        MailboxOperationResult queued;
        try { queued = await DeletionQueue.QueueAsync(_user, deleted.Uid, deleted.UidValidity, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception) { queued = new(false, "The deletion could not be queued. Please try again."); }
        if (_disposed) return;
        if (!queued.Succeeded)
        {
            _locallyRemoved.Remove((deleted.Uid, deleted.UidValidity));
            var restored = _messages.ToList();
            restored.Insert(Math.Min(deletedIndex, restored.Count), deleted);
            _messages = restored;
            CountState.SetInboxCount(_messages.Count);
            if (deletionVersion == _selectionVersion && _selected is null) await SelectAsync(deleted);
            _error = queued.Message;
            return;
        }
        if (_messages.Count > 0 && _selected is null && deletionVersion == _selectionVersion)
            await SelectAsync(_messages[Math.Min(deletedIndex, _messages.Count - 1)]);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_refreshTask is not null) await _refreshTask;
        // In-flight user events can still be unwinding; don't dispose their token/gate here.
    }
}
