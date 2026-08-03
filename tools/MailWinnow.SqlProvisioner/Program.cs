using Microsoft.Data.SqlClient;

static string Required(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Missing required environment variable: {name}")
        : value;
}

var adminConnectionString = Required("MAILWINNOW_SQL_ADMIN_CONNECTION");
var databaseName = Required("MAILWINNOW_SQL_DATABASE");
var loginName = Required("MAILWINNOW_SQL_LOGIN");
var loginPassword = Required("MAILWINNOW_SQL_PASSWORD");

await using var connection = new SqlConnection(adminConnectionString);
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = """
SET NOCOUNT ON;

DECLARE @database sysname = @databaseName;
DECLARE @login sysname = @loginName;
DECLARE @password nvarchar(128) = @loginPassword;

IF @database IS NULL OR @database = N'' OR @login IS NULL OR @login = N''
    THROW 50000, 'Database and login names are required.', 1;

IF HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') <> 1
    OR HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN') <> 1
    OR HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY SERVER ROLE') <> 1
    OR HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER') <> 1
    THROW 50001, 'The administrator connection lacks required server provisioning permissions.', 1;

DECLARE @sql nvarchar(max);

IF DB_ID(@database) IS NULL
BEGIN
    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@database) + N';';
    EXEC(@sql);
END;

IF SUSER_ID(@login) IS NULL
BEGIN
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login)
        + N' WITH PASSWORD = ' + QUOTENAME(@password, '''')
        + N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;';
    EXEC(@sql);
END
ELSE
BEGIN
    SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login)
        + N' WITH PASSWORD = ' + QUOTENAME(@password, '''')
        + N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;';
    EXEC(@sql);
END;

SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login)
    + N' WITH DEFAULT_DATABASE = ' + QUOTENAME(@database) + N';';
EXEC(@sql);
SET @sql = N'DENY VIEW ANY DATABASE TO ' + QUOTENAME(@login) + N';';
EXEC(@sql);

DECLARE @dropServerRoles nvarchar(max) = N'';
SELECT @dropServerRoles += N'ALTER SERVER ROLE ' + QUOTENAME(rolePrincipal.name)
    + N' DROP MEMBER ' + QUOTENAME(@login) + N';'
FROM sys.server_role_members membership
JOIN sys.server_principals rolePrincipal ON rolePrincipal.principal_id = membership.role_principal_id
JOIN sys.server_principals memberPrincipal ON memberPrincipal.principal_id = membership.member_principal_id
WHERE memberPrincipal.name = @login;
IF @dropServerRoles <> N'' EXEC(@dropServerRoles);

DECLARE @databaseSql nvarchar(max) = N'USE ' + QUOTENAME(@database) + N';
DECLARE @userSql nvarchar(max);
IF USER_ID(@login) IS NULL
BEGIN
    SET @userSql = N''CREATE USER '' + QUOTENAME(@login) + N'' FOR LOGIN '' + QUOTENAME(@login) + N'' WITH DEFAULT_SCHEMA = [dbo];'';
    EXEC(@userSql);
END
ELSE
BEGIN
    SET @userSql = N''ALTER USER '' + QUOTENAME(@login) + N'' WITH LOGIN = '' + QUOTENAME(@login) + N'', DEFAULT_SCHEMA = [dbo];'';
    EXEC(@userSql);
END;

DECLARE @dropDatabaseRoles nvarchar(max) = N'''';
SELECT @dropDatabaseRoles += N''ALTER ROLE '' + QUOTENAME(rolePrincipal.name)
    + N'' DROP MEMBER '' + QUOTENAME(@login) + N'';''
FROM sys.database_role_members membership
JOIN sys.database_principals rolePrincipal ON rolePrincipal.principal_id = membership.role_principal_id
JOIN sys.database_principals memberPrincipal ON memberPrincipal.principal_id = membership.member_principal_id
WHERE memberPrincipal.name = @login;
IF @dropDatabaseRoles <> N'''' EXEC(@dropDatabaseRoles);

SET @userSql = N''ALTER ROLE [db_owner] ADD MEMBER '' + QUOTENAME(@login) + N'';'';
EXEC(@userSql);';

EXEC sys.sp_executesql
    @databaseSql,
    N'@login sysname',
    @login = @login;
""";
command.Parameters.AddWithValue("@databaseName", databaseName);
command.Parameters.AddWithValue("@loginName", loginName);
command.Parameters.AddWithValue("@loginPassword", loginPassword);
await command.ExecuteNonQueryAsync();

Console.WriteLine($"Provisioned database '{databaseName}' and its dedicated migration login '{loginName}'.");
