using System.Text.RegularExpressions;
using MimeKit;

namespace MailWinnow.Infrastructure.Outgoing;

public static class OutgoingMessagePolicies
{
    public const int MaxAttachmentCount = 20;
    public const int MaxAttachmentBytes = 25 * 1024 * 1024;
    public const int MaxTotalAttachmentBytes = 30 * 1024 * 1024;
    public const int MaxMimeBytes = 45 * 1024 * 1024;
    public const int MaxBodyCharacters = 500000;
    public static InternetAddressList ParseRecipients(string value)
    {
        if (value.Length > 16000 || value.Contains('\r') || value.Contains('\n')) throw new OutgoingMailException("Enter valid email addresses without line breaks.");
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (!InternetAddressList.TryParse(value, out var addresses) || addresses.Count == 0 || addresses.Any(address => address is not MailboxAddress) || addresses.Mailboxes.Any(address => !ValidMailbox(address)))
            throw new OutgoingMailException("One or more email addresses are invalid.");
        if (addresses.Count > 100) throw new OutgoingMailException("A message can have at most 100 recipients.");
        return addresses;
    }
    private static bool ValidMailbox(MailboxAddress address) => address.Address.Length <= 320 && address.Address.Contains('@') && !address.Address.Any(char.IsControl);
    public static MailboxAddress ParseSender(string address, string displayName)
    {
        var addresses = ParseRecipients(address);
        if (addresses.Count != 1 || displayName.Length > 200 || displayName.Any(char.IsControl)) throw new OutgoingMailException("Enter one valid From address and display name.");
        return new MailboxAddress(displayName, addresses.Mailboxes.Single().Address);
    }
    public static (string To, string Cc) ReplyRecipients(MimeMessage original, bool replyAll, IEnumerable<string> selfAddresses)
    {
        var self = selfAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preferred = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        if (!preferred.Any() && original.Sender is not null) preferred = [original.Sender];
        InternetAddressList Filter(IEnumerable<MailboxAddress> addresses) => new(addresses.Where(address => ValidMailbox(address) && !self.Contains(address.Address) && seen.Add(address.Address)).Take(100));
        var to = Filter(replyAll ? preferred.Concat(original.To.Mailboxes) : preferred);
        var cc = replyAll ? Filter(original.Cc.Mailboxes) : new InternetAddressList();
        return (to.ToString(), cc.ToString());
    }
    public static string ReplySubject(string subject)
    {
        subject = new string(subject.Where(character => !char.IsControl(character)).Take(990).ToArray());
        return Regex.IsMatch(subject, @"^\s*Re\s*:", RegexOptions.IgnoreCase) ? subject : "Re: " + subject;
    }
    public static string Quote(MimeMessage original)
    {
        var text = original.TextBody;
        if (text is null && original.HtmlBody is not null)
        {
            // Plain text only: never preserve HTML, tracking images or remote resources.
            var boundedHtml = original.HtmlBody[..Math.Min(original.HtmlBody.Length, 150000)];
            text = System.Net.WebUtility.HtmlDecode(Regex.Replace(boundedHtml, "<[^>]*>", " ", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)));
        }
        text ??= "[Original message has no text body.]";
        if (text.Length > 100000) text = text[..100000] + "\n[Quoted message truncated]";
        return "\n\n" + string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => "> " + line));
    }
    public static string SafeFileName(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(character => !char.IsControl(character) && character is not '"' and not ':' and not '<' and not '>' and not '|' and not '?' and not '*').Take(180).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }
}
