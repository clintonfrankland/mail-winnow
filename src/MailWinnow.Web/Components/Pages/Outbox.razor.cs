using System.Security.Claims;
using MailWinnow.Infrastructure.Outgoing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MailWinnow.Web.Components.Pages;

public partial class Outbox
{
    [CascadingParameter] public Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;
    [Inject] private IOutgoingMailService Outgoing { get; set; } = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private ClaimsPrincipal _user = null!;
    private IReadOnlyList<MessageDraftView> _drafts = [];
    private IReadOnlyList<OutboxMessageView> _messages = [];
    private MessageDraftView? _editing;
    private string? _error;
    private bool _loading, _opening, _retrying, _disposed;
    private Task? _polling;

    protected override async Task OnInitializedAsync()
    {
        _user = (await AuthenticationStateTask).User;
        await LoadAsync();
        _polling = PollAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading || _disposed) return;
        _loading = true;
        try
        {
            _drafts = await Outgoing.ListDraftsAsync(_user, _lifetime.Token);
            _messages = await Outgoing.ListOutboxAsync(_user, _lifetime.Token);
            _error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _error = "Status could not be refreshed. The last known values are shown; background sending is unaffected."; }
        finally { _loading = false; }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.Token);
                await InvokeAsync(async () => { await LoadAsync(); if (!_disposed) StateHasChanged(); });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task OpenDraftAsync(Guid id)
    {
        if (_editing is not null || _opening) return;
        _opening = true; _error = null;
        try { _editing = await Outgoing.GetDraftAsync(_user, id, _lifetime.Token); }
        catch (OutgoingMailException exception) { _error = exception.Message; }
        catch (Exception) { _error = "The draft could not be opened. Please refresh and try again."; }
        finally { _opening = false; }
    }

    private async Task CloseDraftAsync() { _editing = null; await LoadAsync(); }

    private async Task RetryAsync(Guid id)
    {
        if (_retrying) return;
        _retrying = true; _error = null;
        try { await Outgoing.RetryAsync(_user, id, _lifetime.Token); await LoadAsync(); }
        catch (OutgoingMailException exception) { _error = exception.Message; }
        catch (Exception) { _error = "The retry could not be confirmed. Refresh status before trying again."; }
        finally { _retrying = false; }
    }

    private static string SentCopyHelp(string status) => status switch
    {
        "ProviderSaves" => "Your provider is responsible for saving the Sent copy.",
        "Pending" => "Waiting to save a local Sent copy. The message has already been sent.",
        "Copying" => "Saving a local Sent copy. The message has already been sent.",
        "Saved" => "A copy was saved in your local Sent folder.",
        "Failed" => "The local Sent copy could not be saved. Retry Sent copy does not send the email again.",
        "OutcomeUnknown" => "The local Sent copy could not be confirmed. Check the Sent folder; no copy will be retried automatically. The email itself has already been sent.",
        _ => "Sent-copy status is not available. This does not change sending status."
    };

    private static string StateLabel(OutgoingState state) => state == OutgoingState.OutcomeUnknown ? "Outcome unknown" : state.ToString();
    private static string StateHelp(OutboxMessageView message) => message.State switch
    {
        OutgoingState.Queued => "Saved safely. Waiting for the background worker.",
        OutgoingState.Sending => "The worker is contacting the sending server.",
        OutgoingState.Sent => "Accepted by the sending server. A missing Sent copy will not cause a resend.",
        OutgoingState.OutcomeUnknown => "The server may have accepted this message. Check your provider's Sent folder and recipients before composing another message. It will not be resent automatically.",
        _ => message.CanRetry ? "The message was not accepted for sending. Check your sending account settings before retrying. Retry keeps the saved message and From address." : "Sending failed. Check your account settings and the message before taking further action."
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_polling is not null) await _polling;
    }
}
