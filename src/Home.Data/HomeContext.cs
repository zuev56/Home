using Home.Data.Models.Vk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Data.Common;
using System.Reflection;
using System.Text;
using Zs.Bot.Data.PostgreSQL;
using Zs.Common.Extensions;

namespace Home.Data
{
    public partial class HomeContext : DbContext
    {
        public DbSet<ActivityLogItem> VkActivityLog { get; set; }
        public DbSet<User> VkUsers { get; set; }

        public HomeContext()
        {
        }

        public HomeContext(DbContextOptions<HomeContext> options)
           : base(options)
        {
        }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseSerialColumns();

            PostgreSqlBotContext.ConfigureEntities(modelBuilder);
            PostgreSqlBotContext.SeedData(modelBuilder);

            SetDefaultValues(modelBuilder);
        }

        private void SetDefaultValues(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ActivityLogItem>().Property(b => b.InsertDate).HasDefaultValueSql("now()");

            modelBuilder.Entity<User>().Property(b => b.UpdateDate).HasDefaultValueSql("now()");
            modelBuilder.Entity<User>().Property(b => b.InsertDate).HasDefaultValueSql("now()");
        }

        public static string GetOtherSqlScripts(string configPath)
        {
            var configuration = new ConfigurationBuilder()
                   .AddJsonFile(System.IO.Path.GetFullPath(configPath))
                   .Build();

            var connectionStringBuilder = new DbConnectionStringBuilder()
            {
                ConnectionString = configuration.GetSecretValue("ConnectionStrings:Default")
            };
            var dbName = connectionStringBuilder["Database"] as string;

            var resources = new[]
            {
                "Priveleges.sql",
                "Indexes.sql",
                "ForeignTales.sql",
                "StoredFunctions.sql",
                "Views.sql"
            };

            var sb = new StringBuilder();
            foreach (var resourceName in resources)
            {
                var sqlScript = Assembly.GetExecutingAssembly().ReadResource(resourceName);
                sb.Append(sqlScript + Environment.NewLine);
            }

            if (!string.IsNullOrWhiteSpace(dbName))
                sb.Replace("DefaultDbName", dbName);

            return sb.ToString();
        }
    }
}
