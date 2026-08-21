using Microsoft.EntityFrameworkCore;
using SampleEfApp;

// Sample EF Core project used as a fixture target for EfAssist.
// Two contexts, so dbcontext discovery has more than one thing to find.
// ponytail: relative connection string, so this must be run from the project
// directory to see the same database dotnet ef uses. Switch to an absolute
// path if that ever bites; see README.md.
using var db = new BlogContext();
Console.WriteLine($"Applied migrations: {string.Join(", ", db.Database.GetAppliedMigrations())}");
Console.WriteLine($"Pending migrations: {string.Join(", ", db.Database.GetPendingMigrations())}");
