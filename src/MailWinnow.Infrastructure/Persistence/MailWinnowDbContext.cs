using MailWinnow.Infrastructure.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MailWinnow.Infrastructure.Persistence;

/// <summary>
/// EF Core context for MailWinnow's application data.
/// </summary>
public sealed class MailWinnowDbContext(DbContextOptions<MailWinnowDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
}
