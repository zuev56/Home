using Home.Data.Abstractions;
using Home.Data.Models.Vk;
using Home.Services.Vk.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Zs.Common.Abstractions;
using Zs.Common.Extensions;
using Zs.Common.Models;
using User = Home.Data.Models.Vk.User;

namespace Home.Services.Vk
{
    public class ActivityAnalyzerService : IActivityAnalyzerService
    {
        private readonly IActivityLogItemsRepository _vkActivityLogRepo;
        private readonly IVkUsersRepository _vkUsersRepo;
        private readonly ILogger<ActivityAnalyzerService> _logger;
        private readonly DateTime _tmpMinLogDate = new DateTime(2020, 10, 01);

        public ActivityAnalyzerService(
            IActivityLogItemsRepository vkActivityLogRepo,
            IVkUsersRepository vkUsersRepo,
            ILogger<ActivityAnalyzerService> logger = null)
        {
            _vkActivityLogRepo = vkActivityLogRepo ?? throw new ArgumentNullException(nameof(vkActivityLogRepo));
            _vkUsersRepo = vkUsersRepo ?? throw new ArgumentNullException(nameof(vkUsersRepo));
            _logger = logger;
        }


        /// <inheritdoc/>
        public async Task<IOperationResult<DetailedUserActivity>> GetFullTimeUserStatisticsAsync(int userId)
        {
            try
            {
                if (userId == default)
                    throw new ArgumentOutOfRangeException(nameof(userId));

                var user = await _vkUsersRepo.FindByIdAsync(userId);

                var orderedLogForUser = await GetOrderedLog(_tmpMinLogDate, DateTime.UtcNow, userId);
                if (!orderedLogForUser.Any())
                    return ServiceResult<DetailedUserActivity>.Warning(new DetailedUserActivity(), "No data");

                var activityDetails = new DetailedUserActivity
                {
                    UserName          = $"{user.FirstName} {user.LastName}",
                    AnalyzedDaysCount = (int)(orderedLogForUser.Max(l => l.InsertDate.Date) - orderedLogForUser.Min(l => l.InsertDate.Date)).TotalDays,
                    ActivityDaysCount = orderedLogForUser.Select(l => l.InsertDate.Date).Distinct().Count(),
                    VisitsFromSite    = orderedLogForUser.Count(l => l.IsOnline == true && !l.IsOnlineMobile),
                    VisitsFromApp     = orderedLogForUser.Count(l => l.IsOnline == true && l.IsOnlineMobile),
                    //ActivityCalendar  = GetActivityForEveryDay(orderedLogForUser), Пока не используется
                    TimeInSite        = TimeSpan.FromSeconds(GetActivitySeconds(orderedLogForUser.ToList(), Device.PC)),
                    TimeInApp         = TimeSpan.FromSeconds(GetActivitySeconds(orderedLogForUser.ToList(), Device.Mobile)),
                    Url               = $"https://vk.com/id{JsonDocument.Parse(user.RawData).RootElement.GetProperty("id")}"
                };

                return ServiceResult<DetailedUserActivity>.Success(activityDetails);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetDetailedUserActivity error");
                return ServiceResult<DetailedUserActivity>.Error("Failed to get detailed user activity");
            }
        }

        /// <inheritdoc/>
        public async Task<IOperationResult<List<UserWithActivity>>> GetUsersWithActivityAsync(string filterText, DateTime fromDate, DateTime toDate)
        {
            try
            {
                var usersResult = await GetUsersAsync(filterText);

                var log = await GetOrderedLog(fromDate, toDate, usersResult.Value.Select(u => u.Id).ToArray());
                var onlineUserIds = await GetOnlineUserIdsAsync();

                var userActivityBag = new ConcurrentBag<UserWithActivity>();
                usersResult.Value.AsParallel().ForAll(user =>
                {
                    var orderedLogForUser = log.Where(l => l.UserId == user.Id).OrderBy(l => l.LastSeen).ToList();

                    AddToLogClosingIntervalItem(orderedLogForUser, toDate);

                    userActivityBag.Add(new UserWithActivity
                    {
                        User = user,
                        ActivitySec = GetActivitySeconds(orderedLogForUser, Device.All),
                        isOnline = onlineUserIds.Any(id => id == user.Id)
                    });
                });
                
                var orderedUserList = userActivityBag
                    .OrderByDescending(i => i.ActivitySec).ThenBy(i => i.User.FirstName).ThenBy(i => i.User.LastName)
                    .ToList();

                return ServiceResult<List<UserWithActivity>>.Success(orderedUserList);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetVkUsersWithActivity error");
                return ServiceResult<List<UserWithActivity>>.Error("Failed to get users list with activity time");
            }
        }

        //public async Task<IOperationResult<Table<UserWithActivity>>> GetUsersWithActivityTable(TableParameters tableParameters)
        //{
        //    try
        //    {
        //        if (tableParameters == null)
        //            throw new ArgumentNullException(nameof(tableParameters));
        //
        //        int userCount = await _vkUsersRepo.CountAsync();
        //        int skip = tableParameters.Paging.CurrentPage * tableParameters.Paging.RecordsOnPage;
        //        int take = tableParameters.Paging.RecordsOnPage;
        //
        //        var usersResult = await GetUsers(tableParameters.FilterText, skip, take);
        //
        //        var log = await GetOrderedLog(tableParameters.FromDate, tableParameters.ToDate, usersResult.Value.Select(u => u.Id).ToArray());
        //        var onlineUserIds = await GetOnlineUserIdsAsync();
        //
        //        var userActivityBag = new ConcurrentBag<UserWithActivity>();
        //        usersResult.Value.AsParallel().ForAll(user =>
        //        {
        //            var orderedLog = log.Where(l => l.UserId == user.Id).OrderBy(l => l.LastSeen).ToList();
        //
        //            AddToLogClosingIntervalItem(orderedLog, tableParameters.ToDate);
        //
        //            userActivityBag.Add(new UserWithActivity
        //            {
        //                User = user,
        //                ActivitySec = GetActivitySeconds(orderedLog, Device.All),
        //                isOnline = onlineUserIds.Any(id => id == user.Id)
        //            });
        //        });
        //
        //        var orderedUserList = userActivityBag
        //            .OrderByDescending(i => i.ActivitySec).ThenBy(i => i.User.FirstName).ThenBy(i => i.User.LastName)
        //            .ToList();
        //
        //        return ServiceResult<List<UserWithActivity>>.Success(orderedUserList);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger?.LogError(ex, "GetVkUsersWithActivity error");
        //        return ServiceResult<List<UserWithActivity>>.Error("Failed to get users list with activity time");
        //    }
        //}

        /// <inheritdoc/>
        public async Task<IOperationResult<List<User>>> GetUsersAsync(string filterText = null, int? skip = null, int? take = null)
        {
            try
            {
                var users = !string.IsNullOrWhiteSpace(filterText)
                    ? await _vkUsersRepo.FindAllWhereNameLikeValueAsync(filterText, skip, take)
                    : await _vkUsersRepo.FindAllAsync(skip, take);

                return ServiceResult<List<User>>.Success(users);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetVkUsers error");
                return ServiceResult<List<User>>.Error("Failed to get users");
            }
        }

        private async Task<int[]> GetOnlineUserIdsAsync(params int[] userIds)
        {
            var lastUsersActivity = await _vkActivityLogRepo.FindLastUsersActivity(userIds);

            return lastUsersActivity.Where(i => i.IsOnline == true && DateTime.UtcNow - i.InsertDate < TimeSpan.FromDays(1))
                .Select(i => i.UserId).ToArray();
        }

        private static void AddToLogClosingIntervalItem(List<ActivityLogItem> orderedLog, DateTime toDate)
        {
            // Для корректного отображения времени активности пользователей, которые в данный момент онлайн
            // может потребоваться фейковая запись в журнал
            var lastLogItem = orderedLog.LastOrDefault();
            var endInterval = DateTime.UtcNow < toDate ? DateTime.UtcNow.ToUnixEpoch() : toDate.ToUnixEpoch();
            if (lastLogItem?.IsOnline == true)
                orderedLog.Add(new ActivityLogItem { IsOnline = false, LastSeen = endInterval, InsertDate = DateTime.UtcNow });
        }

        private Dictionary<DateTime, TimeSpan> GetActivityForEveryDay(List<ActivityLogItem> log)
        {
            // Вычисление активности за каждый день должно начинаться с начала суток, если предыдущие сутки закончились онлайн
            if (log == null)
                throw new ArgumentOutOfRangeException(nameof(log));

            var resultMap = new Dictionary<DateTime, TimeSpan>();

            bool prevDayEndedOnlineFromPC = false;
            bool prevDayEndedOnlineFromMobile = false;
            log.Select(l => l.InsertDate.Date).Distinct().ToList().ForEach(day =>
            {
                int secondsADay = 0;
                var dailyLog = log.Where(l => l.InsertDate.Date == day).OrderBy(l => l.InsertDate).ToList();

                if (prevDayEndedOnlineFromPC || prevDayEndedOnlineFromMobile)
                    secondsADay += dailyLog[0].LastSeen - day.ToUnixEpoch();

                // TODO: разделить подсчёт времени с браузера и мобильного
                secondsADay += GetActivitySeconds(dailyLog, Device.All);

                // Фиксируем, как закончился предыдущий день
                prevDayEndedOnlineFromPC = false;
                prevDayEndedOnlineFromMobile = false;
                var last = dailyLog.Last();
                if (last.IsOnline == true)
                {
                    if (last.IsOnlineMobile) prevDayEndedOnlineFromMobile = true;
                    else prevDayEndedOnlineFromPC = true;

                    secondsADay += (day + TimeSpan.FromDays(1)).ToUnixEpoch() - last.LastSeen;
                }
            });

            return resultMap;
        }

        /// <summary>Gets log and order it</summary>
        private async Task<List<ActivityLogItem>> GetOrderedLog(DateTime fromDate, DateTime toDate, params int[] userIds)
        {
            int fromDateUnix = fromDate.ToUnixEpoch();
            int toDateUnix = toDate.ToUnixEpoch();

            var log = await _vkActivityLogRepo.FindAllByIdsInDateRangeAsync(userIds, fromDate, toDate);

            return log.OrderBy(l => l.LastSeen)
                      .SkipWhile(l => l.IsOnline != true)
                      .ToList();
        }

        /// <summary>Get activity time from list of <see cref="ActivityLogItem"/>s in seconds</summary>
        /// <param name="orderedLog">Ordered list of <see cref="ActivityLogItem"/>s</param>
        /// <param name="device">The device type from which the site was used</param>
        private int GetActivitySeconds(List<ActivityLogItem> orderedLog, Device device)
        {
            // Проверка:
            //  - Первый элемент списка должен быть IsOnline == true
            //  - Каждый последующий элемент обрабатывается опираясь на предыдущий
            // Обработка ситуаций:
            //  - Предыдущий IsOnline + Mobile  -> Текущий IsOnline + !Mobile
            //  - Предыдущий IsOnline + Mobile  -> Текущий !IsOnline
            //  - Предыдущий IsOnline + !Mobile -> Текущий IsOnline + Mobile
            //  - Предыдущий IsOnline + !Mobile -> Текущий !IsOnline
            //  - Предыдущий !IsOnline          -> Текущий IsOnline + Mobile
            //  - Предыдущий !IsOnline          -> Текущий IsOnline + !Mobile

            int seconds = 0;
            for (int i = 1; i < orderedLog.Count; i++)
            {
                var prev = orderedLog[i - 1];
                var cur = orderedLog[i];

                switch (device)
                {
                    case Device.PC:
                        if (prev.IsOnline == true && !prev.IsOnlineMobile && (cur.IsOnline == true && cur.IsOnlineMobile || cur.IsOnline == false))
                            seconds += cur.LastSeen - prev.LastSeen;
                        break;
                    case Device.Mobile:
                        if (prev.IsOnline == true && prev.IsOnlineMobile && (cur.IsOnline == true && !cur.IsOnlineMobile || cur.IsOnline == false))
                            seconds += cur.LastSeen - prev.LastSeen;
                        break;
                    case Device.All:
                        if (prev.IsOnline == true)
                            seconds += cur.LastSeen - prev.LastSeen;
                        break;
                }
            }

            // Для корректного отображения времени активности пользователей, которые в данный момент онлайн
            // надо прибавлять секунды с момента их входа в Вк до текущего момента (!!!Можно дописывать в журнал фейковую запись о выходе!!!).
            // Но это решение портит результат, когда анализируется отрезок времени, заканчивающийся в прошлом.
            //var lastLogItem = log.LastOrDefault();
            //if (lastLogItem?.IsOnline == true && lastLogItem.InsertDate...)
            //    seconds += DateTime.UtcNow.ToUnixEpoch() - log.Last().InsertDate.ToUnixEpoch();

            return seconds;
        }

        /// <inheritdoc/>
        public async Task<IOperationResult<SimpleUserActivity>> GetUserStatisticsForPeriodAsync(int userId, DateTime fromDate, DateTime toDate)
        {
            try
            {
                fromDate = fromDate > _tmpMinLogDate ? fromDate : _tmpMinLogDate;

                var user = await _vkUsersRepo.FindByIdAsync(userId);
                if (user == null)
                    return ServiceResult<SimpleUserActivity>.Error("User is not found");

                var orderedLogForUser = await GetOrderedLog(fromDate, toDate, userId);                
                var userStatResult = GetUserStatistics(userId, $"{user.FirstName} {user.LastName}", orderedLogForUser.ToList());

                if (!userStatResult.IsSuccess)
                    return ServiceResult<SimpleUserActivity>.ErrorFrom(userStatResult);

                return ServiceResult<SimpleUserActivity>.Success(userStatResult.Value);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetPeriodUserStatistics error");
                return ServiceResult<SimpleUserActivity>.Error("Failed to get period statistics");
            }
        }

        private IOperationResult<SimpleUserActivity> GetUserStatistics(int userId, string userName, List<ActivityLogItem> orderedLogForUser)
        {
            try
            {
                int browserActivitySec = GetActivitySeconds(orderedLogForUser, Device.PC);
                int mobileActivitySec = GetActivitySeconds(orderedLogForUser, Device.Mobile);

                var periodUserActivity = new SimpleUserActivity()
                {
                    UserId = userId,
                    UserName = userName,
                    TimeInSite = TimeSpan.FromSeconds(browserActivitySec),
                    TimeInApp = TimeSpan.FromSeconds(mobileActivitySec),
                    VisitsCount = orderedLogForUser.Count(l => l.IsOnline == true)
                };

                return ServiceResult<SimpleUserActivity>.Success(periodUserActivity);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetUserStatistics error");
                return ServiceResult<SimpleUserActivity>.Error("Failed to get statistics");
            }
        }

    }
}
