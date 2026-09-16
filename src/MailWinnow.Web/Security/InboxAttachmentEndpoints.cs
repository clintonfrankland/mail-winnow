using MailWinnow.Infrastructure.Mailboxes;

namespace MailWinnow.Web.Security;

public static class InboxAttachmentEndpoints
{
    public static IEndpointRouteBuilder MapInboxAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inbox/attachments/{destinationMailboxId:guid}/{uid:long}/{uidValidity:long}/{attachmentIndex:int}", DownloadAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(HttpContext context, IInboxAttachmentService attachments,
        Guid destinationMailboxId, long uid, long uidValidity, int attachmentIndex, string? folder, string? identity,
        ILoggerFactory loggerFactory)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        if (uid is <= 0 or > uint.MaxValue || uidValidity is <= 0 or > uint.MaxValue ||
            attachmentIndex < 0 || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(identity))
            return Results.NotFound();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await attachments.DownloadAsync(context.User, destinationMailboxId, folder, identity,
                (uint)uid, (uint)uidValidity, attachmentIndex, timeout.Token);
            return result.Status switch
            {
                InboxAttachmentDownloadStatus.Success when result.Content is not null && result.FileName is not null =>
                    Results.File(result.Content, "application/octet-stream", result.FileName, enableRangeProcessing: false),
                InboxAttachmentDownloadStatus.NotFound => Results.Text("This attachment is no longer available. Refresh the inbox and reopen the message.", statusCode: 404),
                InboxAttachmentDownloadStatus.TooLarge => Results.Text("This attachment exceeds the 25 MiB download limit. Use a mail client to save it.", statusCode: 413),
                _ => Results.Text("The attachment could not be downloaded. Please try again.", statusCode: 503)
            };
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Text("The attachment download timed out. Please try again.", statusCode: 504);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Do not include mail content, credentials, or provider responses in logs.
            loggerFactory.CreateLogger(nameof(InboxAttachmentEndpoints)).LogWarning("Inbox attachment download failed");
            return Results.Text("The attachment could not be downloaded. Please try again.", statusCode: 503);
        }
    }
}
