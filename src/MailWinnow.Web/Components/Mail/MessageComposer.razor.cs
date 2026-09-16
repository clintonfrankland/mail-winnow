using System.Security.Claims;
using MailWinnow.Infrastructure.Outgoing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;

namespace MailWinnow.Web.Components.Mail;

public partial class MessageComposer
{
    [Parameter, EditorRequired] public MessageDraftView Draft { get; set; } = null!;
    [Parameter] public EventCallback Closed { get; set; }
    [CascadingParameter] public Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;
    [Inject] private IOutgoingMailService Outgoing { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _debounce;
    private ElementReference _composerPanel;
    private ClaimsPrincipal _user = null!;
    private MessageDraftView? _draft;
    private IReadOnlyList<SendingAccountView> _accounts = [];
    private OutboxMessageView? _queued;
    private Guid? _sendingAccountId;
    private string _to = "", _cc = "", _bcc = "", _subject = "", _body = "";
    private string? _error, _status;
    private long _editRevision, _savedEditRevision;
    private bool _busy, _confirmDiscard, _disposed;
    private bool IsDirty => _queued is null && _editRevision != _savedEditRevision;

    protected override async Task OnInitializedAsync()
    {
        _user = (await AuthenticationStateTask).User;
        _draft = Draft;
        _sendingAccountId = Draft.SendingAccountId;
        _to = Draft.To; _cc = Draft.Cc; _bcc = Draft.Bcc; _subject = Draft.Subject; _body = Draft.Body;
        try { _accounts = await Outgoing.ListSendingAccountsAsync(_user, _lifetime.Token); }
        catch (Exception) { _error = "Sending accounts could not be loaded. Save your draft and reopen it to try again."; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await _composerPanel.FocusAsync();
    }

    private static string AcceptanceTitle(OutgoingState state) => state switch
    {
        OutgoingState.Sent => "Sent.",
        OutgoingState.Sending => "Sending in the background.",
        OutgoingState.Failed => "Sending failed.",
        OutgoingState.OutcomeUnknown => "Sending outcome unknown.",
        _ => "Queued for sending."
    };
    private static string AcceptanceDetail(OutgoingState state) => state switch
    {
        OutgoingState.Sent => "The sending server accepted your message. This is not a recipient delivery receipt.",
        OutgoingState.Sending => "The worker is processing your saved message. You can continue using the inbox.",
        OutgoingState.Failed => "Your message remains in Outbox with its failure status. It has not been silently resent.",
        OutgoingState.OutcomeUnknown => "The server may have accepted your message. Check Outbox and your provider before trying another send.",
        _ => "The background worker will send your message. This does not yet mean it has been sent."
    };

    private static string Text(ChangeEventArgs args) => args.Value?.ToString() ?? "";
    private static string FormatLength(long length) => length >= 1024 * 1024 ? $"{length / (1024d * 1024):0.0} MiB" : $"{Math.Max(1, length / 1024):0} KiB";
    private void SenderChanged(ChangeEventArgs args) => Edit(() => _sendingAccountId = Guid.TryParse(Text(args), out var id) ? id : null);

    private void Edit(Action change)
    {
        if (_busy || _queued is not null || _disposed) return;
        change();
        _editRevision++;
        _status = null;
        _debounce?.Cancel();
        _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = AutosaveAsync(_debounce.Token);
    }

    private async Task AutosaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            // Debouncing cancels only waiting, never an already-started database write.
            await _saveGate.WaitAsync(token);
            try { if (!_busy && !_disposed && _queued is null) await PersistAsync(); }
            finally { _saveGate.Release(); }
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SetError(exception);
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PersistAsync()
    {
        if (_draft is null || !IsDirty) return;
        var editRevision = _editRevision;
        var request = new SaveDraftRequest(_draft.Id, _draft.Revision, _sendingAccountId, _to, _cc, _bcc, _subject, _body);
        var saved = await Outgoing.SaveDraftAsync(_user, request, _lifetime.Token);
        // Preserve edits made while saving; only acknowledge the captured snapshot.
        _draft = saved;
        _savedEditRevision = editRevision;
        _status = "Draft saved";
        _error = null;
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy || _disposed || _queued is not null) return;
        _busy = true;
        _error = null;
        _debounce?.Cancel();
        await _saveGate.WaitAsync();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { SetError(exception); }
        finally { _busy = false; _saveGate.Release(); }
    }

    private Task SaveAsync(bool close) => RunExclusiveAsync(async () =>
    {
        await PersistAsync();
        _status = "Draft saved";
        if (close) await Closed.InvokeAsync();
    });

    private Task SendAsync() => RunExclusiveAsync(async () =>
    {
        await PersistAsync();
        _queued = await Outgoing.QueueAsync(_user, _draft!.Id, _draft.Revision, _lifetime.Token);
        _savedEditRevision = _editRevision;
    });

    private Task AddFilesAsync(InputFileChangeEventArgs args) => RunExclusiveAsync(async () =>
    {
        await PersistAsync();
        foreach (var file in args.GetMultipleFiles(20))
        {
            await using var content = file.OpenReadStream(OutgoingMessagePolicies.MaxAttachmentBytes, _lifetime.Token);
            _draft = await Outgoing.AddAttachmentAsync(_user, _draft!.Id, _draft.Revision, file.Name, file.ContentType, content, _lifetime.Token);
        }
        _status = "Attachments saved with draft";
    });

    private Task RemoveFileAsync(Guid attachmentId) => RunExclusiveAsync(async () =>
    {
        await PersistAsync();
        _draft = await Outgoing.RemoveAttachmentAsync(_user, _draft!.Id, _draft.Revision, attachmentId, _lifetime.Token);
    });

    private Task DiscardAsync() => RunExclusiveAsync(async () =>
    {
        await Outgoing.DiscardDraftAsync(_user, _draft!.Id, _draft.Revision, _lifetime.Token);
        _savedEditRevision = _editRevision;
        await Closed.InvokeAsync();
    });

    private Task CloseAsync() => Closed.InvokeAsync();

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_busy) { context.PreventNavigation(); return; }
        if (!IsDirty) return;
        await SaveAsync(false);
        if (IsDirty) context.PreventNavigation();
    }

    private void SetError(Exception exception) => _error = exception is OutgoingMailException
        ? exception.Message
        : "The operation could not be completed. Your unsaved text is still here. Check Drafts & Outbox before repeating Send.";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _debounce?.Cancel();
        await _lifetime.CancelAsync();
        // Pending events own the gate/token until they unwind. Never discard or send on disposal.
    }
}
