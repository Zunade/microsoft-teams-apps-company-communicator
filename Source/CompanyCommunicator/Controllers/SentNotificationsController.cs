﻿// <copyright file="SentNotificationsController.cs" company="Microsoft">
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// </copyright>

namespace Microsoft.Teams.Apps.CompanyCommunicator.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using System.Web;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Graph;
    using Microsoft.Teams.Apps.CompanyCommunicator.Authentication;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Extensions;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.ExportData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.NotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.SentNotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.TeamData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.MessageQueues.DataQueue;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.MessageQueues.PrepareToSendQueue;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.MicrosoftGraph;
    using Microsoft.Teams.Apps.CompanyCommunicator.Controllers.Options;
    using Microsoft.Teams.Apps.CompanyCommunicator.Models;
    using Newtonsoft.Json;

    /// <summary>
    /// Controller for the sent notification data.
    /// </summary>
    [Authorize(PolicyNames.MustBeValidUpnPolicy)]
    [Route("api/sentNotifications")]
    public class SentNotificationsController : ControllerBase
    {
        private readonly INotificationDataRepository notificationDataRepository;
        private readonly ISentNotificationDataRepository sentNotificationDataRepository;
        private readonly ITeamDataRepository teamDataRepository;
        private readonly IPrepareToSendQueue prepareToSendQueue;
        private readonly IDataQueue dataQueue;
        private readonly double forceCompleteMessageDelayInSeconds;
        private readonly IGroupsService groupsService;
        private readonly IExportDataRepository exportDataRepository;
        private readonly IAppCatalogService appCatalogService;
        private readonly IAppSettingsService appSettingsService;
        private readonly UserAppOptions userAppOptions;
        private readonly ILogger<SentNotificationsController> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SentNotificationsController"/> class.
        /// </summary>
        /// <param name="notificationDataRepository">Notification data repository service that deals with the table storage in azure.</param>
        /// <param name="sentNotificationDataRepository">Sent notification data repository.</param>
        /// <param name="teamDataRepository">Team data repository instance.</param>
        /// <param name="prepareToSendQueue">The service bus queue for preparing to send notifications.</param>
        /// <param name="dataQueue">The service bus queue for the data queue.</param>
        /// <param name="dataQueueMessageOptions">The options for the data queue messages.</param>
        /// <param name="groupsService">The groups service.</param>
        /// <param name="exportDataRepository">The Export data repository instance.</param>
        /// <param name="appCatalogService">App catalog service.</param>
        /// <param name="appSettingsService">App settings service.</param>
        /// <param name="userAppOptions">User app options.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SentNotificationsController(
            INotificationDataRepository notificationDataRepository,
            ISentNotificationDataRepository sentNotificationDataRepository,
            ITeamDataRepository teamDataRepository,
            IPrepareToSendQueue prepareToSendQueue,
            IDataQueue dataQueue,
            IOptions<DataQueueMessageOptions> dataQueueMessageOptions,
            IGroupsService groupsService,
            IExportDataRepository exportDataRepository,
            IAppCatalogService appCatalogService,
            IAppSettingsService appSettingsService,
            IOptions<UserAppOptions> userAppOptions,
            ILoggerFactory loggerFactory)
        {
            if (dataQueueMessageOptions is null)
            {
                throw new ArgumentNullException(nameof(dataQueueMessageOptions));
            }

            this.notificationDataRepository = notificationDataRepository ?? throw new ArgumentNullException(nameof(notificationDataRepository));
            this.sentNotificationDataRepository = sentNotificationDataRepository ?? throw new ArgumentNullException(nameof(sentNotificationDataRepository));
            this.teamDataRepository = teamDataRepository ?? throw new ArgumentNullException(nameof(teamDataRepository));
            this.prepareToSendQueue = prepareToSendQueue ?? throw new ArgumentNullException(nameof(prepareToSendQueue));
            this.dataQueue = dataQueue ?? throw new ArgumentNullException(nameof(dataQueue));
            this.forceCompleteMessageDelayInSeconds = dataQueueMessageOptions.Value.ForceCompleteMessageDelayInSeconds;
            this.groupsService = groupsService ?? throw new ArgumentNullException(nameof(groupsService));
            this.exportDataRepository = exportDataRepository ?? throw new ArgumentNullException(nameof(exportDataRepository));
            this.appCatalogService = appCatalogService ?? throw new ArgumentNullException(nameof(appCatalogService));
            this.appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
            this.userAppOptions = userAppOptions?.Value ?? throw new ArgumentNullException(nameof(userAppOptions));
            this.logger = loggerFactory?.CreateLogger<SentNotificationsController>() ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        /// <summary>
        /// Send a notification, which turns a draft to be a sent notification.
        /// </summary>
        /// <param name="draftNotification">An instance of <see cref="DraftNotification"/> class.</param>
        /// <returns>The result of an action method.</returns>
        [HttpPost]
        public async Task<IActionResult> CreateSentNotificationAsync(
            [FromBody] DraftNotification draftNotification)
        {
            if (draftNotification == null)
            {
                throw new ArgumentNullException(nameof(draftNotification));
            }

            // TODO: double-check it
           // draftNotification.Buttons = this.GetButtonTrackingUrl(draftNotification);

            var draftNotificationDataEntity = await this.notificationDataRepository.GetAsync(
                NotificationDataTableNames.DraftNotificationsPartition,
                draftNotification.Id);
            if (draftNotificationDataEntity == null)
            {
                return this.NotFound($"Draft notification, Id: {draftNotification.Id}, could not be found.");
            }

            var newSentNotificationId =
                await this.notificationDataRepository.MoveDraftToSentPartitionAsync(draftNotificationDataEntity);

            // Ensure the data table needed by the Azure Functions to send the notifications exist in Azure storage.
            await this.sentNotificationDataRepository.EnsureSentNotificationDataTableExistsAsync();

            // Update user app id if proactive installation is enabled.
            await this.UpdateUserAppIdAsync();

            var prepareToSendQueueMessageContent = new PrepareToSendQueueMessageContent
            {
                NotificationId = newSentNotificationId,
            };

            await this.prepareToSendQueue.SendAsync(prepareToSendQueueMessageContent);

            // Send a "force complete" message to the data queue with a delay to ensure that
            // the notification will be marked as complete no matter the counts
            var forceCompleteDataQueueMessageContent = new DataQueueMessageContent
            {
                NotificationId = newSentNotificationId,
                ForceMessageComplete = true,
            };
            await this.dataQueue.SendDelayedAsync(
                forceCompleteDataQueueMessageContent,
                this.forceCompleteMessageDelayInSeconds);

            return this.Ok();
        }

        /// <summary>
        /// Get most recently sent notification summaries.
        /// </summary>
        /// <returns>A list of <see cref="SentNotificationSummary"/> instances.</returns>
        [HttpGet]
        public async Task<IEnumerable<SentNotificationSummary>> GetSentNotificationsAsync()
        {
            var notificationEntities = await this.notificationDataRepository.GetMostRecentSentNotificationsAsync();

            var result = new List<SentNotificationSummary>();
            foreach (var notificationEntity in notificationEntities)
            {
                var summary = new SentNotificationSummary
                {
                    Id = notificationEntity.Id,
                    Title = notificationEntity.Title,
                    CreatedDateTime = notificationEntity.CreatedDate,
                    SentDate = notificationEntity.SentDate,
                    Succeeded = notificationEntity.Succeeded,
                    Failed = notificationEntity.Failed,
                    Unknown = this.GetUnknownCount(notificationEntity),
                    TotalMessageCount = notificationEntity.TotalMessageCount,
                    SendingStartedDate = notificationEntity.SendingStartedDate,
                    Status = notificationEntity.GetStatus(),
                    Reads = notificationEntity.Reads,
                };

                result.Add(summary);
            }

            return result;
        }

        /// <summary>
        /// Get most recently sent notification summaries.
        /// </summary>
        /// <param name="channelId">Channel Id to filter notifications.</param>
        /// <returns>A list of <see cref="SentNotificationSummary"/> instances.</returns>
        [HttpGet("channel/{channelId}")]
        public async Task<IEnumerable<SentNotificationSummary>> GetChannelSentNotificationsAsync(string channelId)
        {
            var notificationEntities = await this.notificationDataRepository.GetMostRecentChannelSentNotificationsAsync(channelId);

            var result = new List<SentNotificationSummary>();
            foreach (var notificationEntity in notificationEntities)
            {
                var summary = new SentNotificationSummary
                {
                    Id = notificationEntity.Id,
                    Title = notificationEntity.Title,
                    CreatedDateTime = notificationEntity.CreatedDate,
                    SentDate = notificationEntity.SentDate,
                    Succeeded = notificationEntity.Succeeded,
                    Failed = notificationEntity.Failed,
                    Unknown = this.GetUnknownCount(notificationEntity),
                    TotalMessageCount = notificationEntity.TotalMessageCount,
                    SendingStartedDate = notificationEntity.SendingStartedDate,
                    Status = notificationEntity.GetStatus(),
                };

                result.Add(summary);
            }

            return result;
        }

        /// <summary>
        /// Record a read for the message with a specific id. This web method is used as part of the simple tracking/analytics for CC.
        /// </summary>
        /// <param name="id">The id of the sent message where the read is being tracked for analytics.</param>
        /// <param name="key">The key of the message instance that was sent to a specific user.</param>
        /// <param name="buttonid">buttonid.</param>
        /// <param name="redirecturl">redirecturl.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        [HttpGet]
        [Route("trackingbutton")]
        [AllowAnonymous]
        public async Task<IActionResult> TrackButtonClick(string id, string key, string buttonid, string redirecturl)
        {

            // id cannot be null
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            // key cannot be null
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            // buttonid actually is the button name
            if (string.IsNullOrWhiteSpace(buttonid))
            {
                throw new ArgumentNullException(nameof(buttonid));
            }

            // redirecturl cannot be null
            if (string.IsNullOrWhiteSpace(redirecturl))
            {
                throw new ArgumentNullException(nameof(redirecturl));
            }

            // gets the sent notification summary that needs to be updated
            var notificationEntity = await this.notificationDataRepository.GetAsync(
                NotificationDataTableNames.SentNotificationsPartition,
                id);

            // if the notification entity is null it means it doesnt exist or is not a sent message yet
            if (notificationEntity != null)
            {

                List<TrackingButtonClicks> result;

                if (notificationEntity.ButtonTrackingClicks is null)
                {

                    result = new List<TrackingButtonClicks>();

                    var click = new TrackingButtonClicks { name = buttonid, clicks = 1 };
                    result.Add(click);
                }
                else
                {
                    result = JsonConvert.DeserializeObject<List<TrackingButtonClicks>>(notificationEntity.ButtonTrackingClicks);

                    var button = result.Find(p => p.name == buttonid);

                    if (button == null)
                    {
                        result.Add(new TrackingButtonClicks { name = buttonid, clicks = 1 });
                    }
                    else
                    {
                        button.clicks++;
                    }

                }

                notificationEntity.ButtonTrackingClicks = JsonConvert.SerializeObject(result);

                // persists the change
                await this.notificationDataRepository.CreateOrUpdateAsync(notificationEntity);

                // save the user button clicked
                await this.UpdateButtonClickedByUser(id, key, buttonid);

            }

            return this.Redirect(WebUtility.UrlDecode(redirecturl));
        }

        private async Task UpdateButtonClickedByUser(string id, string key, string buttonid)
        {
            // gets the sent notification object for the message sent
            var sentnotificationEntity = await this.sentNotificationDataRepository.GetAsync(id, key);

            // if we have a instance that was sent to a user
            if (sentnotificationEntity != null)
            {

                List<TrackingUserClicks> result;

                if (sentnotificationEntity.ButtonTracking is null)
                {

                    result = new List<TrackingUserClicks>();

                    var click = new TrackingUserClicks { name = buttonid, clicks = 1, datetime = DateTime.Now };
                    result.Add(click);
                }
                else
                {

                    result = JsonConvert.DeserializeObject<List<TrackingUserClicks>>(sentnotificationEntity.ButtonTracking);

                    var button = result.Find(p => p.name == buttonid);

                    if (button == null)
                    {
                        result.Add(new TrackingUserClicks { name = buttonid, clicks = 1, datetime = DateTime.Now });
                    }
                    else
                    {
                        button.clicks++;
                        button.datetime = DateTime.Now;
                    }

                }

                sentnotificationEntity.ButtonTracking = JsonConvert.SerializeObject(result);

                await this.sentNotificationDataRepository.CreateOrUpdateAsync(sentnotificationEntity);
            }
        }

        /// <summary>
        /// Record a read for the message with a specific id. This web method is used as part of the simple tracking/analytics for CC.
        /// </summary>
        /// <param name="id">The id of the sent message where the read is being tracked for analytics.</param>
        /// <param name="key">The key of the message instance that was sent to a specific user.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        [HttpGet]
        [Route("tracking")]
        [AllowAnonymous]
        public async Task<IActionResult> TrackRead(string id, string key)
        {
            // id cannot be null
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            // key cannot be null
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            try
            {
                // gets the sent notification object for the message sent
                var sentnotificationEntity = await this.sentNotificationDataRepository.GetAsync(id, key);

                // if we have a instance that was sent to a user
                if (sentnotificationEntity != null)
                {
                    // if the message was not read yet
                    if (sentnotificationEntity.ReadStatus != true)
                    {
                        sentnotificationEntity.ReadStatus = true;
                        sentnotificationEntity.ReadDate = DateTime.UtcNow;

                        await this.sentNotificationDataRepository.CreateOrUpdateAsync(sentnotificationEntity);

                        // gets the sent notification summary that needs to be updated
                        var notificationEntity = await this.notificationDataRepository.GetAsync(
                            NotificationDataTableNames.SentNotificationsPartition,
                            id);

                        // if the notification entity is null it means it doesnt exist or is not a sent message yet
                        if (notificationEntity != null)
                        {
                            // increment the number of reads
                            notificationEntity.Reads++;

                            // persists the change
                            await this.notificationDataRepository.CreateOrUpdateAsync(notificationEntity);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // Failed to track the reading.
                this.logger.LogError(exception, $"Failed to track the reading of the message. Error message: {exception.Message}.");
            }

            return this.Ok();
        }

        /// <summary>
        /// Get a sent notification by Id.
        /// </summary>
        /// <param name="id">Id of the requested sent notification.</param>
        /// <returns>Required sent notification.</returns>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetSentNotificationByIdAsync(string id)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            var notificationEntity = await this.notificationDataRepository.GetAsync(
                NotificationDataTableNames.SentNotificationsPartition,
                id);
            if (notificationEntity == null)
            {
                return this.NotFound();
            }

            var groupNames = await this.groupsService.
                GetByIdsAsync(notificationEntity.Groups).
                Select(x => x.DisplayName).
                ToListAsync();

            var userId = this.HttpContext.User.FindFirstValue(Common.Constants.ClaimTypeUserId);
            var userNotificationDownload = await this.exportDataRepository.GetAsync(userId, id);

            var result = new SentNotification
            {
                Id = notificationEntity.Id,
                Title = notificationEntity.Title,
                ImageLink = notificationEntity.ImageLink,
                Summary = notificationEntity.Summary,
                Author = notificationEntity.Author,
                ButtonTitle = notificationEntity.ButtonTitle,
                ButtonLink = notificationEntity.ButtonLink,
                Buttons = notificationEntity.Buttons,
                ChannelId = notificationEntity.ChannelId,
                IsScheduled = notificationEntity.IsScheduled,
                IsImportant = notificationEntity.IsImportant,
                CreatedDateTime = notificationEntity.CreatedDate,
                SentDate = notificationEntity.SentDate,
                Succeeded = notificationEntity.Succeeded,
                Failed = notificationEntity.Failed,
                Unknown = this.GetUnknownCount(notificationEntity),
                TeamNames = await this.teamDataRepository.GetTeamNamesByIdsAsync(notificationEntity.Teams),
                RosterNames = await this.teamDataRepository.GetTeamNamesByIdsAsync(notificationEntity.Rosters),
                GroupNames = groupNames,
                AllUsers = notificationEntity.AllUsers,
                SendingStartedDate = notificationEntity.SendingStartedDate,
                ErrorMessage = notificationEntity.ErrorMessage,
                WarningMessage = notificationEntity.WarningMessage,
                CanDownload = userNotificationDownload == null,
                SendingCompleted = notificationEntity.IsCompleted(),
                Reads = notificationEntity.Reads,
                CsvUsers = notificationEntity.CsvUsers,
                ButtonTrackingClicks = notificationEntity.ButtonTrackingClicks,
            };

            return this.Ok(result);
        }

        private int? GetUnknownCount(NotificationDataEntity notificationEntity)
        {
            var unknown = notificationEntity.Unknown;

            // In CC v2, the number of throttled recipients are counted and saved in NotificationDataEntity.Unknown property.
            // However, CC v1 saved the number of throttled recipients in NotificationDataEntity.Throttled property.
            // In order to make it backward compatible, we add the throttled number to the unknown variable.
            var throttled = notificationEntity.Throttled;
            if (throttled > 0)
            {
                unknown += throttled;
            }

            return unknown > 0 ? unknown : (int?)null;
        }

        /// <summary>
        /// Updates user app id if its not already synced.
        /// </summary>
        private async Task UpdateUserAppIdAsync()
        {
            // check if proactive installation is enabled.
            if (!this.userAppOptions.ProactivelyInstallUserApp)
            {
                return;
            }

            // check if we have already synced app id.
            var appId = await this.appSettingsService.GetUserAppIdAsync();
            if (!string.IsNullOrWhiteSpace(appId))
            {
                return;
            }

            try
            {
                // Fetch and store user app id in App Catalog.
                appId = await this.appCatalogService.GetTeamsAppIdAsync(this.userAppOptions.UserAppExternalId);

                // Graph SDK returns empty id if the app is not found.
                if (string.IsNullOrEmpty(appId))
                {
                    this.logger.LogError($"Failed to find an app in AppCatalog with external Id: {this.userAppOptions.UserAppExternalId}");
                    return;
                }

                await this.appSettingsService.SetUserAppIdAsync(appId);
            }
            catch (ServiceException exception)
            {
                // Failed to fetch app id.
                this.logger.LogError(exception, $"Failed to get catalog app id. Error message: {exception.Message}.");
            }
        }
    }
}
