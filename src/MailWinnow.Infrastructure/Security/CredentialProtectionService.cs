using Microsoft.AspNetCore.DataProtection;

namespace MailWinnow.Infrastructure.Security;

public enum CredentialKind
{
    SourceImapPassword,
    SourceImapAppPassword,
    SourceOAuthRefreshToken,
    DestinationImapPassword
}

public interface ICredentialProtectionService
{
    string Protect(string credential, CredentialKind kind);

    string Unprotect(string protectedCredential, CredentialKind kind);
}

public sealed class CredentialProtectionException : Exception
{
    public CredentialProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class CredentialProtectionService(IDataProtectionProvider provider)
    : ICredentialProtectionService
{
    private const string PurposePrefix = "MailWinnow.Credentials.v1";

    public string Protect(string credential, CredentialKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        try
        {
            return CreateProtector(kind).Protect(credential);
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            throw new CredentialProtectionException(
                "The credential could not be encrypted. Verify that the Data Protection key ring is available and writable.",
                exception);
        }
    }

    public string Unprotect(string protectedCredential, CredentialKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCredential);

        try
        {
            return CreateProtector(kind).Unprotect(protectedCredential);
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            throw new CredentialProtectionException(
                "The stored credential could not be decrypted. It may be corrupt or its Data Protection key is unavailable.",
                exception);
        }
    }

    private IDataProtector CreateProtector(CredentialKind kind) =>
        provider.CreateProtector(PurposePrefix, kind.ToString());
}
