using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Home.Data.Abstractions;
using Home.Data.Models.Vk;
using Home.Data.Models.VkAPI;
using Home.Services.Vk.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Zs.Common.Abstractions;
using Zs.Common.Enums;
using Zs.Common.Extensions;
using Zs.Common.Models;
using Zs.Common.Services.WebAPI;
using User = Home.Data.Models.Vk.User;

namespace Home.Services.Vk;

public class ActivityLoggerService : IActivityLoggerService
{
    private readonly IConfiguration _configuration;
    private readonly IActivityLogItemsRepository _vkActivityLogRepo;
    private readonly IVkUsersRepository _vkUsersRepo;
    private readonly ILogger<ActivityLoggerService> _logger;
    private readonly float? _version;
    private readonly string _accessToken;

    public ActivityLoggerService(
        IConfiguration configuration,
        IActivityLogItemsRepository vkActivityLogRepo,
        IVkUsersRepository vkUsersRepo,
        ILogger<ActivityLoggerService> logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _vkActivityLogRepo = vkActivityLogRepo ?? throw new ArgumentNullException(nameof(vkActivityLogRepo));
        _vkUsersRepo = vkUsersRepo ?? throw new ArgumentNullException(nameof(vkUsersRepo));
        _logger = logger;

        _version = _configuration["Home:Vk:Version"] != null ? float.Parse(_configuration["Home:Vk:Version"], CultureInfo.InvariantCulture) : null;
        _accessToken = _configuration.GetSecretValue("Home:Vk:AccessToken");
    }

    /// <inheritdoc/>
    public async Task<IOperationResult<List<User>>> AddNewUsersAsync(params int[] userIds)
    {
        if (userIds == null || userIds.Length == 0 || _accessToken == null || _version == null)
            return ServiceResult<List<User>>.Error(ErrorMessage.CANT_ACCESS_API_WITHOUT_REQUIRED_PARAMS);

        var resultUsersList = new List<User>();
        var result = ServiceResult<List<User>>.Success(resultUsersList);
        try
        {
            var url = $"https://api.vk.com/method/users.get?user_ids={string.Join(',', userIds)}"
                    + "&fields=photo_id,verified,sex,bdate,city,country,home_town,photo_max_orig,online,domain,has_mobile,"
                    + "contacts,site,education,universities,schools,status,last_seen,followers_count,occupation,nickname,"
                    + "relatives,relation,personal,connections,exports,activities,interests,music,movies,tv,books,games,"
                    + "about,quotes,can_post,can_see_all_posts,can_see_audio,can_write_private_message,can_send_friend_request,"
                    + "is_favorite,is_hidden_from_feed,timezone,screen_name,maiden_name,is_friend,friend_status,career,military,"
                    + $"blacklisted,blacklisted_by_me,can_be_invited_group&access_token={_accessToken}&v={_version.Value.ToString(CultureInfo.InvariantCulture)}";

            var response = await ApiHelper.GetAsync<ApiResponse>(url, throwExceptionOnError: true);

            if (response is null)
                return ServiceResult<List<User>>.Error("Response is null");

            var existingDbUsers = await _vkUsersRepo.FindAllAsync(userIds);

            if (existingDbUsers?.Count > 0)
                result.AddMessage($"Existing users won't be added. Existing users IDs: {string.Join(',', existingDbUsers.Select(u => u.Id))}", InfoMessageType.Warning);

            var usersForSave = response.Users.Where(u => !existingDbUsers.Select(eu => eu.Id).Contains(u.Id)).Select(u => (User)u);
            var savedSuccessfully = await _vkUsersRepo.SaveRangeAsync(usersForSave);

            if (savedSuccessfully)
            {
                resultUsersList.AddRange(usersForSave);
                return result;
            }
            else
                return ServiceResult<List<User>>.Error("User saving failed");
        }
        catch (Exception ex)
        {
            _logger?.LogErrorIfNeed(ex, "New users saving failed");
            return ServiceResult<List<User>>.Error("New users saving failed");
        }
    }

    /// <inheritdoc/>
    public async Task<IOperationResult> SaveVkUsersActivityAsync()
    {
        if (_accessToken == null || _version == null)
            return ServiceResult.Error(ErrorMessage.CANT_ACCESS_API_WITHOUT_REQUIRED_PARAMS);

        ServiceResult result = ServiceResult.Success();
        try
        {
            var vkUsers = await _vkUsersRepo.FindAllAsync();
            var userIds = vkUsers.Select(u => u.Id);

            if (!userIds.Any())
            {
                result.AddMessage("There are no users in the database", InfoMessageType.Warning);
                return result;
            }

            string url = $"https://api.vk.com/method/users.get?user_ids={string.Join(',', userIds)}"
                       + $"&fields=online,online_mobile,online_app,last_seen&access_token={_accessToken}&v={_version.Value.ToString(CultureInfo.InvariantCulture)}";

            var response = await ApiHelper.GetAsync<ApiResponse>(url, throwExceptionOnError: true);

            if (response is null)
            {
                bool saveResult = await SetUndefinedActivityToAllVkUsers();
                _logger?.LogWarning("Set undefined activity to all VkUsers (succeeded: {SaveResult})", saveResult);
                result.AddMessage(InfoMessage.Warning("Response is null. Setting undefined activity to all VkUsers"));
                return result;
            }

            int loggedItemsCount = await LogVkUsersActivityAsync(response.Users);

            result.AddMessage(InfoMessage.Warning($"Logged {loggedItemsCount} activities"));
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogErrorIfNeed(ex, "SaveVkUsersActivityAsync error");
            return ServiceResult.Error("Users activity saving error");
        }
    }

    /// <summary>Save undefined user activities to database</summary>
    private async Task<bool> SetUndefinedActivityToAllVkUsers()
    {
        var users = await _vkUsersRepo.FindAllAsync();

        var lastUsersActivityLogItems = await _vkActivityLogRepo.FindLastUsersActivity();

        var activityLogItems = new List<ActivityLogItem>();
        foreach (var user in users)
        {
            if (lastUsersActivityLogItems.First(i => i.UserId == user.Id).IsOnline == true)
                activityLogItems.Add(
                    new ActivityLogItem
                    {
                        UserId = user.Id,
                        IsOnline = null,
                        IsOnlineMobile = false,
                        OnlineApp = null,
                        LastSeen = -1,
                        InsertDate = DateTime.UtcNow
                    }
                );
        }

        return await _vkActivityLogRepo.SaveRangeAsync(activityLogItems);
    }

    /// <summary>Save user activities to database</summary>
    /// <param name="apiUsers">All users current state from VK API</param>
    /// <returns>Logged <see cref="ActivityLogItem">s count</returns>
    private async Task<int> LogVkUsersActivityAsync(List<ApiUser> apiUsers)
    {
        // TODO: Add user activity info (range) - ???
        var lastActivityLogItems = await _vkActivityLogRepo.FindLastUsersActivity();
        var activityLogItemsForSave = new List<ActivityLogItem>();

        foreach (var apiUser in apiUsers)
        {
            // When account is deleted or banned or smth else
            if (apiUser.LastSeenUnix == null)
                continue;

            var lastActivityLogItem = lastActivityLogItems.FirstOrDefault(i => i.UserId == apiUser.Id);
            var currentOnlineStatus = apiUser.Online == 1;
            var currentMobileStatus = apiUser.OnlineMobile == 1;
            var currentApp = apiUser.OnlineApp;

            if (lastActivityLogItem == null
                || lastActivityLogItem.IsOnline != currentOnlineStatus
                || lastActivityLogItem.IsOnlineMobile != currentMobileStatus
                || lastActivityLogItem.OnlineApp != currentApp)
            {
                // Vk corrects LastSeen, so we have to work with logged value, not current API value
                int lastSeenForLog = apiUser.LastSeenUnix?.Time ?? 0;
                if (lastActivityLogItem != null && apiUser.LastSeenUnix != null)
                    lastSeenForLog = Math.Max(lastActivityLogItem.LastSeen, apiUser.LastSeenUnix.Time);

                activityLogItemsForSave.Add(
                    new ActivityLogItem
                    {
                        UserId = apiUser.Id,
                        IsOnline = currentOnlineStatus,
                        IsOnlineMobile = currentMobileStatus,
                        OnlineApp = apiUser.OnlineApp,
                        LastSeen = lastSeenForLog,
                        InsertDate = DateTime.UtcNow
                    });
            }
        }

        return await _vkActivityLogRepo.SaveRangeAsync(activityLogItemsForSave)
            ? activityLogItemsForSave.Count
            : -1;
    }

}
