using System;
using System.Threading.Tasks;
using Home.Service.Tests.Data;
using Home.Services.Vk;
using Xunit;

namespace Home.Service.Tests
{
    public class ActivityAnalyzerServiceTests
    {
        private const int _dbEntitiesAmount = 1000;

        [Fact]
        public async Task GetFullTimeUserStatisticsAsync_CorrectUserId_ReturnsNotNull()
        {
            // Arrange
            var activityAnalyzerService = GetActivityAnalyzerService();
            var userId = Random.Shared.Next(1, _dbEntitiesAmount);

            // Act
            var fullTimeUserActivity = await activityAnalyzerService.GetFullTimeUserStatisticsAsync(userId);

            // Assert
            Assert.NotNull(fullTimeUserActivity);
        }

        private IActivityAnalyzerService GetActivityAnalyzerService()
        {
            var postgreSqlInMemory = new PostgreSqlInMemory();
            postgreSqlInMemory.FillWithFakeData(_dbEntitiesAmount);

            return new ActivityAnalyzerService(
                postgreSqlInMemory.ActivityLogItemsRepository,
                postgreSqlInMemory.VkUsersRepository);
        }
    }
}