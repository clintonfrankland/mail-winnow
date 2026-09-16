using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Outgoing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MailWinnow.Web.Components.Pages;

public partial class SendingAccounts
{
    [CascadingParameter] public Task<AuthenticationState> AuthenticationStateTask { get; set; } = null!;
    [Inject] private IOutgoingMailService Outgoing { get; set; } = null!;
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private ClaimsPrincipal _user = null!;
    private IReadOnlyList<SendingAccountView> _accounts = [];
    private IReadOnlyList<SourceMailboxSummary> _sources = [];
    private Guid? _id, _sourceMailboxId;
    private string _displayName = "", _fromAddress = "", _host = "", _username = "", _password = "", _sentFolder = "Sent";
    private int _port = 587;
    private bool _useStartTls = true, _useAuthentication = true, _enabled = false, _hasPassword, _saving, _loading = true;
    private SentCopyPolicy _sentCopyPolicy = SentCopyPolicy.ProviderSaves;
    private string? _error, _notice;

    protected override async Task OnInitializedAsync()
    {
        _user = (await AuthenticationStateTask).User;
        try
        {
            _accounts = await Outgoing.ListSendingAccountsAsync(_user, _lifetime.Token);
            await using var scope = ScopeFactory.CreateAsyncScope();
            _sources = await scope.ServiceProvider.GetRequiredService<IMailboxConfigurationService>().ListSourcesAsync(_user, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _error = "Sending settings could not be loaded. Please reload before editing."; }
        finally { _loading = false; }
    }

    private void EditAccount(SendingAccountView account)
    {
        _id = account.Id; _sourceMailboxId = account.SourceMailboxId; _displayName = account.DisplayName;
        _fromAddress = account.FromAddress; _host = account.Host; _port = account.Port;
        _useStartTls = account.UseStartTls; _useAuthentication = account.UseAuthentication;
        _username = account.Username; _hasPassword = account.HasPassword; _password = "";
        _enabled = account.Enabled; _sentCopyPolicy = account.SentCopyPolicy; _sentFolder = account.SentFolder;
        _error = null; _notice = null;
    }

    private void NewAccount()
    {
        _id = null; _sourceMailboxId = null;
        _displayName = ""; _fromAddress = ""; _host = ""; _username = ""; _password = ""; _sentFolder = "Sent";
        _port = 587; _useStartTls = true; _useAuthentication = true; _enabled = false; _hasPassword = false;
        _sentCopyPolicy = SentCopyPolicy.ProviderSaves; _error = null; _notice = null;
    }

    private async Task SaveAsync()
    {
        if (_saving || _loading) return;
        _saving = true; _error = null; _notice = null;
        try
        {
            var account = await Outgoing.SaveSendingAccountAsync(_user, new(_id, _sourceMailboxId, _displayName, _fromAddress,
                _host, _port, _useStartTls, _useAuthentication, _username, string.IsNullOrWhiteSpace(_password) ? null : _password,
                _enabled, _sentCopyPolicy, _sentFolder), _lifetime.Token);
            EditAccount(account);
            _accounts = _accounts.Where(item => item.Id != account.Id).Append(account).OrderBy(item => item.DisplayName).ToArray();
            _notice = "Sending account saved. No test email was sent.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OutgoingMailException exception) { _error = exception.Message; }
        catch (Exception) { _error = "The sending account could not be saved. Please try again."; }
        finally { _password = ""; _saving = false; }
    }

    public async ValueTask DisposeAsync() { _password = ""; await _lifetime.CancelAsync(); }
}
