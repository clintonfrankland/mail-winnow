using System.Security.Claims;
using MailWinnow.Infrastructure.Mailboxes;
using MailWinnow.Infrastructure.Persistence;
using MailWinnow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailWinnow.Tests.Mailboxes;

public sealed class InboxDeletionQueueTests
{
    [Fact]
    public async Task RapidDeletesAreQueuedAndProcessedSerially()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mailwinnow-delete-{Guid.NewGuid():N}.db");
        try
        {
            var imap = new RecordingImap();
            var services = new ServiceCollection();
            services.AddDbContext<MailWinnowDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
            services.AddScoped<IOwnershipAuthorizer, OwnershipAuthorizer>();
            services.AddScoped<ICredentialProtectionService, PlainCredentials>();
            services.AddScoped<IImapConnectionService>(_ => imap);
            services.AddSingleton(Options.Create(new LocalImapOptions { Host = "imap.test", Port = 993, UseSsl = true }));
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.DestinationMailboxes.Add(new DestinationMailbox { OwnerUserId = "owner", Username = "local", ProtectedCredential = "password", Folder = "INBOX" });
                await db.SaveChangesAsync();
            }

            var queue = new InboxDeletionQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InboxDeletionQueue>.Instance);
            await queue.StartAsync(CancellationToken.None);
            var requests = Enumerable.Range(1, 5).Select(uid => queue.QueueAsync(Principal("owner"), (uint)uid, 42));

            Assert.All(await Task.WhenAll(requests), result => Assert.True(result.Succeeded));
            await imap.AllDeleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.StopAsync(CancellationToken.None);

            Assert.Equal([1u, 2u, 3u, 4u, 5u], imap.DeletedUids);
            Assert.Equal(1, imap.MaximumConcurrency);
            Assert.All(imap.Moves, move => Assert.Equal(("INBOX", "Trash"), move));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
    private sealed class PlainCredentials : ICredentialProtectionService { public string Protect(string value, CredentialKind kind) => value; public string Unprotect(string value, CredentialKind kind) => value; }

    private sealed class RecordingImap : IImapConnectionService
    {
        private int _active;
        public List<uint> DeletedUids { get; } = [];
        public List<(string Source, string Destination)> Moves { get; } = [];
        public int MaximumConcurrency { get; private set; }
        public TaskCompletionSource AllDeleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImapOperationResult<int>> MoveToFolderAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint expectedUidValidity, string destinationFolderName, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            await Task.Delay(20, cancellationToken);
            DeletedUids.Add(Assert.Single(uids));
            Moves.Add((folderName, destinationFolderName));
            Interlocked.Decrement(ref _active);
            if (DeletedUids.Count == 5) AllDeleted.TrySetResult();
            return ImapOperationResult<int>.Success(1);
        }

        public Task<ImapOperationResult<int>> DeleteAndExpungeAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint expectedUidValidity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImapOperationResult<IReadOnlyList<string>>> ListFoldersAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<IReadOnlyList<ImapMessageHeader>>> FetchHeadersAsync(ImapConnectionSettings connection, string folderName, IReadOnlyList<uint> uids, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<MimeMessage>> FetchMessageAsync(ImapConnectionSettings connection, string folderName, uint uid, uint? expectedUidValidity = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<uint?>> AppendMessageAsync(ImapConnectionSettings connection, string folderName, MimeMessage message, CancellationToken cancellationToken = default, DateTimeOffset? receivedUtc = null) => throw new NotSupportedException();
        public Task<ImapOperationResult<bool>> TestConnectionAsync(ImapConnectionSettings connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImapOperationResult<ImapFolderSnapshot>> GetFolderSnapshotAsync(ImapConnectionSettings connection, string folderName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
