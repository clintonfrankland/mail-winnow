# Source and dependency reference

## Project references

| Project | Reference |
|---|---|
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | `..\MailWinnow.Core\MailWinnow.Core.csproj` |
| `src/MailWinnow.Web/MailWinnow.Web.csproj` | `..\MailWinnow.Core\MailWinnow.Core.csproj` |
| `src/MailWinnow.Web/MailWinnow.Web.csproj` | `..\MailWinnow.Infrastructure\MailWinnow.Infrastructure.csproj` |
| `src/MailWinnow.Worker/MailWinnow.Worker.csproj` | `..\MailWinnow.Core\MailWinnow.Core.csproj` |
| `src/MailWinnow.Worker/MailWinnow.Worker.csproj` | `..\MailWinnow.Infrastructure\MailWinnow.Infrastructure.csproj` |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | `..\..\src\MailWinnow.Core\MailWinnow.Core.csproj` |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | `..\..\src\MailWinnow.Infrastructure\MailWinnow.Infrastructure.csproj` |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | `..\..\src\MailWinnow.Web\MailWinnow.Web.csproj` |
| `tools/MailWinnow.DbMigrator/MailWinnow.DbMigrator.csproj` | `..\..\src\MailWinnow.Infrastructure\MailWinnow.Infrastructure.csproj` |

## Package references

| Project | Package | Version |
|---|---|---|
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | Microsoft.EntityFrameworkCore.Design | 10.0.10 |
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | Microsoft.EntityFrameworkCore.SqlServer | 10.0.10 |
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.10 |
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | MailKit | 4.17.0 |
| `src/MailWinnow.Infrastructure/MailWinnow.Infrastructure.csproj` | HtmlSanitizer | 9.1.982 |
| `src/MailWinnow.Worker/MailWinnow.Worker.csproj` | Microsoft.Extensions.Hosting | 10.0.10 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | bunit | 2.9.0 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | coverlet.collector | 6.0.4 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | Microsoft.NET.Test.Sdk | 17.14.1 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | Microsoft.AspNetCore.TestHost | 10.0.10 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | Microsoft.EntityFrameworkCore.Sqlite | 10.0.10 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | xunit | 2.9.3 |
| `tests/MailWinnow.Tests/MailWinnow.Tests.csproj` | xunit.runner.visualstudio | 3.1.4 |
| `tools/MailWinnow.DbMigrator/MailWinnow.DbMigrator.csproj` | Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.10 |
| `tools/MailWinnow.DbMigrator/MailWinnow.DbMigrator.csproj` | Microsoft.Extensions.Logging.Console | 10.0.10 |
| `tools/MailWinnow.DeploymentProbe/MailWinnow.DeploymentProbe.csproj` | Microsoft.Data.SqlClient | 6.1.1 |
| `tools/MailWinnow.SqlProvisioner/MailWinnow.SqlProvisioner.csproj` | Microsoft.Data.SqlClient | 6.1.1 |

## Source file reference

Generated migration designers and model snapshot accompany the named migrations; build output is excluded.

| File | Responsibility |
|---|---|
| [`src/MailWinnow.Core/Abstractions/IMailboxProcessor.cs`](../src/MailWinnow.Core/Abstractions/IMailboxProcessor.cs) | /// Processes the configured source mailboxes once. Implementations belong to Infrastructure. /// |
| [`src/MailWinnow.Core/Rules/RuleAction.cs`](../src/MailWinnow.Core/Rules/RuleAction.cs) | The durable decisions supported by the first MailWinnow rule engine. |
| [`src/MailWinnow.Core/Rules/RuleEvaluator.cs`](../src/MailWinnow.Core/Rules/RuleEvaluator.cs) | Pure, deterministic evaluation for a single catalogued message. |
| [`src/MailWinnow.Infrastructure/Mailboxes/BlockedMessageDeletionService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/BlockedMessageDeletionService.cs) | Durably moves blocked messages from the source mailbox into the destination Blocked folder. |
| [`src/MailWinnow.Infrastructure/Mailboxes/ImapConnectionService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/ImapConnectionService.cs) | Application-owned IMAP contract. Synchronization callers never need to depend on MailKit types. |
| [`src/MailWinnow.Infrastructure/Mailboxes/InboxDeletionQueue.cs`](../src/MailWinnow.Infrastructure/Mailboxes/InboxDeletionQueue.cs) | Inbox Deletion Queue |
| [`src/MailWinnow.Infrastructure/Mailboxes/InboxReaderService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/InboxReaderService.cs) | Inbox Reader Service |
| [`src/MailWinnow.Infrastructure/Mailboxes/LocalImapHealthChecker.cs`](../src/MailWinnow.Infrastructure/Mailboxes/LocalImapHealthChecker.cs) | Performs a bounded TCP probe without using or exposing a mailbox credential. |
| [`src/MailWinnow.Infrastructure/Mailboxes/LocalImapOptions.cs`](../src/MailWinnow.Infrastructure/Mailboxes/LocalImapOptions.cs) | Local Imap Options |
| [`src/MailWinnow.Infrastructure/Mailboxes/MailSynchronization.cs`](../src/MailWinnow.Infrastructure/Mailboxes/MailSynchronization.cs) | Durable request marker: API requests never communicate directly with IMAP or bypass worker locks. |
| [`src/MailWinnow.Infrastructure/Mailboxes/MailboxConfigurationService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/MailboxConfigurationService.cs) | Mailbox Configuration Service |
| [`src/MailWinnow.Infrastructure/Mailboxes/MailboxEntities.cs`](../src/MailWinnow.Infrastructure/Mailboxes/MailboxEntities.cs) | Per-folder checkpoint. A changed UIDVALIDITY deliberately starts a new identity namespace. |
| [`src/MailWinnow.Infrastructure/Mailboxes/MailboxRetentionService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/MailboxRetentionService.cs) | Applies fixed retention to Mail Winnow's managed destination folders. |
| [`src/MailWinnow.Infrastructure/Mailboxes/MessageDeliveryService.cs`](../src/MailWinnow.Infrastructure/Mailboxes/MessageDeliveryService.cs) | Moves MIME content through process memory. The delivery row is the append and source-deletion idempotency record. |
| [`src/MailWinnow.Infrastructure/Persistence/MailWinnowDbContext.cs`](../src/MailWinnow.Infrastructure/Persistence/MailWinnowDbContext.cs) | /// EF Core context for MailWinnow's application data. /// |
| [`src/MailWinnow.Infrastructure/Persistence/MailWinnowDbContextFactory.cs`](../src/MailWinnow.Infrastructure/Persistence/MailWinnowDbContextFactory.cs) | /// Supplies the context to EF tooling without starting either long-running host. /// |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260803201111_InitialApplicationSchema.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260803201111_InitialApplicationSchema.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804120956_AddHouseholdIdentity.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804120956_AddHouseholdIdentity.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804153851_AddMailboxConfiguration.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804153851_AddMailboxConfiguration.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804192946_AddHeaderSynchronization.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804192946_AddHeaderSynchronization.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804213243_AddRuleEngine.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804213243_AddRuleEngine.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804214018_MakeRuleRetentionDefault.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804214018_MakeRuleRetentionDefault.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260804215000_CorrectLegacyHeaderEvaluationOutcome.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260804215000_CorrectLegacyHeaderEvaluationOutcome.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260805181003_AddMessageDelivery.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260805181003_AddMessageDelivery.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260805182154_HardenMessageDeliveryRecovery.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260805182154_HardenMessageDeliveryRecovery.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260805193303_AddDeliveredMessageCleanup.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260805193303_AddDeliveredMessageCleanup.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260806123813_AddAdministrationAuditAndWorkerHealth.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260806123813_AddAdministrationAuditAndWorkerHealth.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260807185730_MoveApprovedMessagesAndOptionalRetention.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260807185730_MoveApprovedMessagesAndOptionalRetention.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260807191730_AddBlockedSourceDeletionTracking.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260807191730_AddBlockedSourceDeletionTracking.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260810163238_AddBlockedDestinationReceipt.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260810163238_AddBlockedDestinationReceipt.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260818025554_AddDurableReviewDecisionWorkItems.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260818025554_AddDurableReviewDecisionWorkItems.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260818035513_ScopeReviewDecisionIdempotencyToActiveWorkItems.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260818035513_ScopeReviewDecisionIdempotencyToActiveWorkItems.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/20260914132610_AddNavigationCountSnapshots.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/20260914132610_AddNavigationCountSnapshots.cs) | EF schema migration |
| [`src/MailWinnow.Infrastructure/Persistence/Migrations/MailWinnowDbContextModelSnapshot.cs`](../src/MailWinnow.Infrastructure/Persistence/Migrations/MailWinnowDbContextModelSnapshot.cs) | Current EF schema snapshot |
| [`src/MailWinnow.Infrastructure/Persistence/ServiceCollectionExtensions.cs`](../src/MailWinnow.Infrastructure/Persistence/ServiceCollectionExtensions.cs) | /// Registers the SQL Server application context. The standard configuration provider maps /// ConnectionStrings__MailWinnow to ConnectionStrings:MailWinnow. /// |
| [`src/MailWinnow.Infrastructure/Rules/MessageReviewService.cs`](../src/MailWinnow.Infrastructure/Rules/MessageReviewService.cs) | Read model for message review. Every query is constrained to the signed-in owner's source accounts. |
| [`src/MailWinnow.Infrastructure/Rules/NavigationCountService.cs`](../src/MailWinnow.Infrastructure/Rules/NavigationCountService.cs) | Scalar SQL projections only; never runs mailbox evaluation or opens an IMAP connection. |
| [`src/MailWinnow.Infrastructure/Rules/NavigationProjectionRefreshWorker.cs`](../src/MailWinnow.Infrastructure/Rules/NavigationProjectionRefreshWorker.cs) | Refreshes remote inbox counts outside interactive circuits. Last good snapshots survive failures/restarts. |
| [`src/MailWinnow.Infrastructure/Rules/ReviewDecisionQueue.cs`](../src/MailWinnow.Infrastructure/Rules/ReviewDecisionQueue.cs) | Database-backed review command processor. A command is committed before the UI is told it was accepted. |
| [`src/MailWinnow.Infrastructure/Rules/ReviewMessagePreviewService.cs`](../src/MailWinnow.Infrastructure/Rules/ReviewMessagePreviewService.cs) | Reads a single catalogued pending source message without changing review or delivery state. |
| [`src/MailWinnow.Infrastructure/Rules/RuleEntities.cs`](../src/MailWinnow.Infrastructure/Rules/RuleEntities.cs) | Destination retention in days. Null keeps the moved message indefinitely. |
| [`src/MailWinnow.Infrastructure/Rules/RuleEvaluationService.cs`](../src/MailWinnow.Infrastructure/Rules/RuleEvaluationService.cs) | Owner-bound evaluation and safe pending-header reevaluation on rule changes. |
| [`src/MailWinnow.Infrastructure/Rules/RuleImpactPreviewService.cs`](../src/MailWinnow.Infrastructure/Rules/RuleImpactPreviewService.cs) | Read-only, owner-bound simulation over catalogued pending headers. |
| [`src/MailWinnow.Infrastructure/Rules/ScopedMessageReviewService.cs`](../src/MailWinnow.Infrastructure/Rules/ScopedMessageReviewService.cs) | Blazor circuits retain this facade, never an EF context or its change tracker. |
| [`src/MailWinnow.Infrastructure/Security/AdministrationService.cs`](../src/MailWinnow.Infrastructure/Security/AdministrationService.cs) | Sanitized operational audit data. It deliberately has no fields for mail content or credentials. |
| [`src/MailWinnow.Infrastructure/Security/ApplicationUser.cs`](../src/MailWinnow.Infrastructure/Security/ApplicationUser.cs) | Application User |
| [`src/MailWinnow.Infrastructure/Security/AuthConstants.cs`](../src/MailWinnow.Infrastructure/Security/AuthConstants.cs) | Auth Constants |
| [`src/MailWinnow.Infrastructure/Security/AuthorizationServiceCollectionExtensions.cs`](../src/MailWinnow.Infrastructure/Security/AuthorizationServiceCollectionExtensions.cs) | Authorization Service Collection Extensions |
| [`src/MailWinnow.Infrastructure/Security/CredentialProtectionService.cs`](../src/MailWinnow.Infrastructure/Security/CredentialProtectionService.cs) | Credential Protection Service |
| [`src/MailWinnow.Infrastructure/Security/CredentialProtectionServiceCollectionExtensions.cs`](../src/MailWinnow.Infrastructure/Security/CredentialProtectionServiceCollectionExtensions.cs) | Credential Protection Service Collection Extensions |
| [`src/MailWinnow.Infrastructure/Security/FirstRunSetupService.cs`](../src/MailWinnow.Infrastructure/Security/FirstRunSetupService.cs) | First Run Setup Service |
| [`src/MailWinnow.Infrastructure/Security/HouseholdAccountService.cs`](../src/MailWinnow.Infrastructure/Security/HouseholdAccountService.cs) | Household Account Service |
| [`src/MailWinnow.Infrastructure/Security/OwnershipAuthorizer.cs`](../src/MailWinnow.Infrastructure/Security/OwnershipAuthorizer.cs) | /// The mandatory service-layer ownership boundary for mail accounts, rules, message headers, /// and credentials. Administrator role membership intentionally does not bypass ownership. /// |
| [`src/MailWinnow.Infrastructure/Security/ServiceResult.cs`](../src/MailWinnow.Infrastructure/Security/ServiceResult.cs) | Service Result |
| [`src/MailWinnow.Web/Components/App.razor`](../src/MailWinnow.Web/Components/App.razor) | App |
| [`src/MailWinnow.Web/Components/Layout/MainLayout.razor`](../src/MailWinnow.Web/Components/Layout/MainLayout.razor) | Main Layout |
| [`src/MailWinnow.Web/Components/Layout/MainLayout.razor.css`](../src/MailWinnow.Web/Components/Layout/MainLayout.razor.css) | Main Layout |
| [`src/MailWinnow.Web/Components/Layout/NavMenu.razor`](../src/MailWinnow.Web/Components/Layout/NavMenu.razor) | Nav Menu |
| [`src/MailWinnow.Web/Components/Layout/NavMenu.razor.css`](../src/MailWinnow.Web/Components/Layout/NavMenu.razor.css) | Nav Menu |
| [`src/MailWinnow.Web/Components/Layout/NavigationCountLoader.cs`](../src/MailWinnow.Web/Components/Layout/NavigationCountLoader.cs) | Navigation Count Loader |
| [`src/MailWinnow.Web/Components/Layout/NavigationCountState.cs`](../src/MailWinnow.Web/Components/Layout/NavigationCountState.cs) | Navigation Count State |
| [`src/MailWinnow.Web/Components/Pages/AccessDenied.razor`](../src/MailWinnow.Web/Components/Pages/AccessDenied.razor) | Access Denied |
| [`src/MailWinnow.Web/Components/Pages/Error.razor`](../src/MailWinnow.Web/Components/Pages/Error.razor) | Error |
| [`src/MailWinnow.Web/Components/Pages/Home.razor`](../src/MailWinnow.Web/Components/Pages/Home.razor) | Home |
| [`src/MailWinnow.Web/Components/Pages/Household.razor`](../src/MailWinnow.Web/Components/Pages/Household.razor) | Household |
| [`src/MailWinnow.Web/Components/Pages/Inbox.razor`](../src/MailWinnow.Web/Components/Pages/Inbox.razor) | Inbox |
| [`src/MailWinnow.Web/Components/Pages/Inbox.razor.css`](../src/MailWinnow.Web/Components/Pages/Inbox.razor.css) | Inbox |
| [`src/MailWinnow.Web/Components/Pages/Login.razor`](../src/MailWinnow.Web/Components/Pages/Login.razor) | Login |
| [`src/MailWinnow.Web/Components/Pages/Mailboxes.razor`](../src/MailWinnow.Web/Components/Pages/Mailboxes.razor) | Mailboxes |
| [`src/MailWinnow.Web/Components/Pages/Mailboxes.razor.css`](../src/MailWinnow.Web/Components/Pages/Mailboxes.razor.css) | Mailboxes |
| [`src/MailWinnow.Web/Components/Pages/NotFound.razor`](../src/MailWinnow.Web/Components/Pages/NotFound.razor) | Not Found |
| [`src/MailWinnow.Web/Components/Pages/Operations.razor`](../src/MailWinnow.Web/Components/Pages/Operations.razor) | Operations |
| [`src/MailWinnow.Web/Components/Pages/Review.razor`](../src/MailWinnow.Web/Components/Pages/Review.razor) | Review |
| [`src/MailWinnow.Web/Components/Pages/ReviewBySender.razor`](../src/MailWinnow.Web/Components/Pages/ReviewBySender.razor) | Review By Sender |
| [`src/MailWinnow.Web/Components/Pages/ReviewBySender.razor.css`](../src/MailWinnow.Web/Components/Pages/ReviewBySender.razor.css) | Review By Sender |
| [`src/MailWinnow.Web/Components/Pages/ReviewDecisionActions.razor`](../src/MailWinnow.Web/Components/Pages/ReviewDecisionActions.razor) | Review Decision Actions |
| [`src/MailWinnow.Web/Components/Pages/ReviewDecisionPageHelper.cs`](../src/MailWinnow.Web/Components/Pages/ReviewDecisionPageHelper.cs) | Review Decision Page Helper |
| [`src/MailWinnow.Web/Components/Pages/ReviewDecisionRequest.cs`](../src/MailWinnow.Web/Components/Pages/ReviewDecisionRequest.cs) | Review Decision Request |
| [`src/MailWinnow.Web/Components/Pages/ReviewMessagePreview.razor`](../src/MailWinnow.Web/Components/Pages/ReviewMessagePreview.razor) | Review Message Preview |
| [`src/MailWinnow.Web/Components/Pages/ReviewMessagePreview.razor.css`](../src/MailWinnow.Web/Components/Pages/ReviewMessagePreview.razor.css) | Review Message Preview |
| [`src/MailWinnow.Web/Components/Pages/Rules.razor`](../src/MailWinnow.Web/Components/Pages/Rules.razor) | Rules |
| [`src/MailWinnow.Web/Components/Pages/Setup.razor`](../src/MailWinnow.Web/Components/Pages/Setup.razor) | Setup |
| [`src/MailWinnow.Web/Components/Routes.razor`](../src/MailWinnow.Web/Components/Routes.razor) | Routes |
| [`src/MailWinnow.Web/Components/_Imports.razor`](../src/MailWinnow.Web/Components/_Imports.razor) | _Imports |
| [`src/MailWinnow.Web/Program.cs`](../src/MailWinnow.Web/Program.cs) | Program |
| [`src/MailWinnow.Web/Security/AccountEndpoints.cs`](../src/MailWinnow.Web/Security/AccountEndpoints.cs) | Account Endpoints |
| [`src/MailWinnow.Web/Security/AdministrationEndpoints.cs`](../src/MailWinnow.Web/Security/AdministrationEndpoints.cs) | Administration Endpoints |
| [`src/MailWinnow.Web/Security/MailboxEndpoints.cs`](../src/MailWinnow.Web/Security/MailboxEndpoints.cs) | Mailbox Endpoints |
| [`src/MailWinnow.Web/Security/ReviewEndpoints.cs`](../src/MailWinnow.Web/Security/ReviewEndpoints.cs) | Dedicated form target for creating and previewing reusable rules from the Rules page. |
| [`src/MailWinnow.Web/wwwroot/accessibility.css`](../src/MailWinnow.Web/wwwroot/accessibility.css) | accessibility |
| [`src/MailWinnow.Web/wwwroot/app.css`](../src/MailWinnow.Web/wwwroot/app.css) | app |
| [`src/MailWinnow.Web/wwwroot/brand.css`](../src/MailWinnow.Web/wwwroot/brand.css) | brand |
| [`src/MailWinnow.Web/wwwroot/mobile.css`](../src/MailWinnow.Web/wwwroot/mobile.css) | mobile |
| [`src/MailWinnow.Worker/Program.cs`](../src/MailWinnow.Worker/Program.cs) | Program |
| [`src/MailWinnow.Worker/Worker.cs`](../src/MailWinnow.Worker/Worker.cs) | Worker |
| [`tools/MailWinnow.DbMigrator/Program.cs`](../tools/MailWinnow.DbMigrator/Program.cs) | Program |
| [`tools/MailWinnow.DeploymentProbe/Program.cs`](../tools/MailWinnow.DeploymentProbe/Program.cs) | Program |
| [`tools/MailWinnow.SqlProvisioner/Program.cs`](../tools/MailWinnow.SqlProvisioner/Program.cs) | Program |
