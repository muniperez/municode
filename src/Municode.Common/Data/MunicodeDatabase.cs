using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Municode.Common.Data;

public static class MunicodeDatabase
{
    public const string MigrationsHistoryTable = "__MunicodeMigrationsHistory";

    public static IServiceCollection AddMunicodeDatabase(this IServiceCollection services) =>
        services.AddScoped<WorkspaceDbContext>(provider => Create(provider.GetRequiredService<IConfiguration>()));

    private static WorkspaceDbContext Create(IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("Municode");

        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var localPath = Path.Combine(FindWorkingRoot(), ".local", "artifacts", "data", "municode.db");
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = localPath }.ToString();
            }

            var options = new DbContextOptionsBuilder<WorkspaceSqliteDbContext>();
            ConfigureSqlite(options, connectionString);
            return new WorkspaceSqliteDbContext(options.Options);
        }
        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Configure ConnectionStrings:Municode for PostgreSQL.");

            if (configuration["Database:PasswordFile"] is { Length: > 0 } path)
            {
                var password = File.ReadAllText(path).TrimEnd('\r', '\n');
                if (password.Length == 0)
                    throw new InvalidOperationException("The Municode database password file is empty.");
                connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
            }

            var options = new DbContextOptionsBuilder<WorkspaceDbContext>();
            ConfigurePostgres(options, connectionString);
            return new WorkspaceDbContext(options.Options);
        }
        throw new InvalidOperationException("Database:Provider must be Sqlite or Postgres.");
    }

    private static string FindWorkingRoot()
    {
        // dotnet run starts in the project directory. In a checkout, put the
        // default database beside the other build and test artifacts.
        var workingDirectory = Directory.GetCurrentDirectory();
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Municode.slnx")))
                return directory.FullName;
        }
        return workingDirectory;
    }

    public static void ConfigureSqlite(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlite(SqliteDatabaseConfigurator.ConfigureConnectionString(connectionString),
            provider => provider.MigrationsHistoryTable(MigrationsHistoryTable));

    public static void ConfigurePostgres(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(MigrationsHistoryTable, "public"));
}
