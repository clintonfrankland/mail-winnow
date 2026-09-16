using System.Security.Claims;
using Bunit;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Web.Components.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MailWinnow.Tests.Mailboxes;

public sealed class MailboxDialogRenderedTests : BunitContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditorsAreUniqueLabelledDialogsOutsideClippedTableWithOriginalPostContracts(bool hasDestination)
    {
        var mailboxes = new MailboxesStub(hasDestination);
        Services.AddSingleton<IMailboxConfigurationService>(mailboxes);
        Services.AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>();
        Services.AddSingleton(Options.Create(new LocalImapOptions { Host = "local.test", Port = 993, UseSsl = true }));
        var page = Render<MailWinnow.Web.Components.Pages.Mailboxes>(parameters => parameters.AddCascadingValue(new DefaultHttpContext()));

        var dialogs = page.FindAll("dialog");
        Assert.Equal(4, dialogs.Count);
        Assert.Empty(page.FindAll(".table-responsive dialog"));
        var identifiers = page.FindAll("[id]").Select(element => element.Id).ToArray();
        Assert.Equal(identifiers.Length, identifiers.Distinct().Count());
        foreach (var dialog in dialogs)
        {
            Assert.False(dialog.HasAttribute("open"));
            Assert.NotEmpty(page.Find($"#{dialog.GetAttribute("aria-labelledby")}").TextContent);
            Assert.Single(page.FindAll($"button[data-dialog-open='{dialog.Id}'][aria-controls='{dialog.Id}'][aria-haspopup='dialog']"));
            var form = dialog.QuerySelector("form")!;
            Assert.Equal("post", form.GetAttribute("method"));
            Assert.Equal("fixture-token", form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value"));
            Assert.NotNull(dialog.QuerySelector("button[type=button][data-dialog-close]"));
        }

        var addForm = page.Find("#add-source-dialog form");
        Assert.Equal("/mailboxes/source/save", addForm.GetAttribute("action"));
        Assert.Null(addForm.QuerySelector("[name=Id]"));
        Assert.True(addForm.QuerySelector("[name=Password]")!.HasAttribute("required"));
        foreach (var source in mailboxes.Sources)
        {
            var form = page.Find($"#source-dialog-{source.Id:N} form");
            Assert.Equal(source.Id.ToString(), form.QuerySelector("[name=Id]")!.GetAttribute("value"));
            Assert.Equal(source.Username, form.QuerySelector("[name=Username]")!.GetAttribute("value"));
            Assert.False(form.QuerySelector("[name=Password]")!.HasAttribute("required"));
            Assert.Null(form.QuerySelector("[name=Password]")!.GetAttribute("value"));
            Assert.Equal(source.UseSsl, form.QuerySelector("[name=UseSsl]")!.HasAttribute("checked"));
        }
        var destinationForm = page.Find("#destination-dialog form");
        Assert.Equal("/mailboxes/destination/save", destinationForm.GetAttribute("action"));
        Assert.Equal(!hasDestination, destinationForm.QuerySelector("[name=Password]")!.HasAttribute("required"));
        Assert.DoesNotContain("ProtectedCredential", page.Markup);
        // Optional artifact feeds the real-browser regression with actual rendered markup.
        if (hasDestination && Environment.GetEnvironmentVariable("MAILBOX_DIALOG_FIXTURE_PATH") is { Length: > 0 } fixturePath)
            File.WriteAllText(fixturePath, page.Markup);
    }

    private sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("fixture-token", "__RequestVerificationToken");
    }

    private sealed class MailboxesStub(bool hasDestination) : IMailboxConfigurationService
    {
        public SourceMailboxSummary[] Sources { get; } = [
            new(Guid.NewGuid(), "First", "imap.test", 993, true, "first", true, ["INBOX"], null, null, null),
            new(Guid.NewGuid(), "Second", "imap.test", 143, false, "second", false, ["Archive"], null, null, null)];
        public Task<IReadOnlyList<SourceMailboxSummary>> ListSourcesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SourceMailboxSummary>>(Sources);
        public Task<DestinationMailboxSummary?> GetDestinationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default) => Task.FromResult<DestinationMailboxSummary?>(hasDestination ? new("local-user", "INBOX", true) : null);
        public Task<MailboxOperationResult> SaveSourceAsync(ClaimsPrincipal actor, Guid? id, SourceMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveSourceFoldersAsync(ClaimsPrincipal actor, Guid id, IReadOnlyList<string>? selectedFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SetSourceEnabledAsync(ClaimsPrincipal actor, Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> TestSourceAsync(ClaimsPrincipal actor, Guid id, bool discoverFolders, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailboxOperationResult> SaveDestinationAsync(ClaimsPrincipal actor, DestinationMailboxInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
