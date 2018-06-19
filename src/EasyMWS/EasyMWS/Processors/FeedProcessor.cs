﻿using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using MountainWarehouse.EasyMWS.CallbackLogic;
using MountainWarehouse.EasyMWS.Data;
using MountainWarehouse.EasyMWS.Enums;
using MountainWarehouse.EasyMWS.Helpers;
using MountainWarehouse.EasyMWS.Logging;
using MountainWarehouse.EasyMWS.Model;
using MountainWarehouse.EasyMWS.Services;
using MountainWarehouse.EasyMWS.WebService.MarketplaceWebService;
using Newtonsoft.Json;

namespace MountainWarehouse.EasyMWS.Processors
{
	internal class FeedProcessor : IFeedQueueingProcessor
	{
		private readonly IFeedSubmissionProcessor _feedSubmissionProcessor;
		private readonly ICallbackActivator _callbackActivator;
		private readonly IEasyMwsLogger _logger;

		private readonly AmazonRegion _region;
		private readonly string _merchantId;
		private readonly EasyMwsOptions _options;

		internal FeedProcessor(AmazonRegion region, string merchantId, EasyMwsOptions options, IMarketplaceWebServiceClient mwsClient, IFeedSubmissionProcessor feedSubmissionProcessor, ICallbackActivator callbackActivator, IEasyMwsLogger logger)
		  : this(region, merchantId, options, mwsClient, logger)
		{
			_feedSubmissionProcessor = feedSubmissionProcessor;
			_callbackActivator = callbackActivator;
		}

		internal FeedProcessor(AmazonRegion region, string merchantId, EasyMwsOptions options, IMarketplaceWebServiceClient mwsClient, IEasyMwsLogger logger)
		{
			_region = region;
			_merchantId = merchantId;
			_options = options;
			_logger = logger;

			_callbackActivator = _callbackActivator ?? new CallbackActivator();

			_feedSubmissionProcessor = _feedSubmissionProcessor ?? new FeedSubmissionProcessor(_region, _merchantId, mwsClient, _logger, _options);
		}

		public void PollFeeds(IFeedSubmissionEntryService feedSubmissionService)
		{
			_feedSubmissionProcessor.CleanUpFeedSubmissionQueue(feedSubmissionService);

			SubmitNextFeedInQueueToAmazon(feedSubmissionService);

			RequestFeedSubmissionStatusesFromAmazon(feedSubmissionService);

			RequestNextFeedSubmissionInQueueFromAmazon(feedSubmissionService);

			PerformCallbacksForPreviouslySubmittedFeeds(feedSubmissionService);
		}

		private void PerformCallbacksForPreviouslySubmittedFeeds(IFeedSubmissionEntryService feedSubmissionService)
		{
			var previouslySubmittedFeeds = feedSubmissionService.GetAllFromQueueOfFeedsReadyForCallback(_options, _merchantId, _region);

			foreach (var feedSubmissionEntry in previouslySubmittedFeeds)
			{
				try
				{
					_logger.Info($"Attempting to perform method callback for the next submitted feed in queue : {feedSubmissionEntry.RegionAndTypeComputed}.");
					var callback = new Callback(feedSubmissionEntry.TypeName, feedSubmissionEntry.MethodName, feedSubmissionEntry.Data, feedSubmissionEntry.DataTypeName);
					var unzippedFeedSubmissionReport = ZipHelper.ExtractArchivedSingleFileToStream(feedSubmissionEntry.Details.FeedSubmissionReport);
					_callbackActivator.CallMethod(callback, unzippedFeedSubmissionReport);
					feedSubmissionService.Delete(feedSubmissionEntry);
				}
				catch (Exception e)
				{
					_logger.Error($"Method callback failed for {feedSubmissionEntry.RegionAndTypeComputed}. Current retry count is :{feedSubmissionEntry.FeedSubmissionRetryCount}. {e.Message}", e);
					feedSubmissionEntry.FeedSubmissionRetryCount++;
					feedSubmissionService.Update(feedSubmissionEntry);
				}
			}

			feedSubmissionService.SaveChanges();
		}

		public void QueueFeed(IFeedSubmissionEntryService feedSubmissionService, FeedSubmissionPropertiesContainer propertiesContainer, Action<Stream, object> callbackMethod, object callbackData)
		{
			try
			{
				if (callbackMethod == null)
				{
					throw new ArgumentNullException(nameof(callbackMethod), "The callback method cannot be null, as it has to be invoked once the report has been downloaded, in order to provide access to the report content.");
				}

				if (propertiesContainer == null) throw new ArgumentNullException();

				var serializedPropertiesContainer = JsonConvert.SerializeObject(propertiesContainer);

				var feedSubmission = new FeedSubmissionEntry(serializedPropertiesContainer)
				{
					AmazonRegion = _region,
					MerchantId = _merchantId,
					LastSubmitted = DateTime.MinValue,
					DateCreated = DateTime.UtcNow,
					IsProcessingComplete = false,
					HasErrors = false,
					SubmissionErrorData = null,
					FeedSubmissionRetryCount = 0,
					FeedSubmissionId = null,
					FeedType = propertiesContainer.FeedType,
					Details = new FeedSubmissionDetails
					{
						FeedContent = ZipHelper.CreateArchiveFromContent(propertiesContainer.FeedContent)
					}
				};
				
				var serializedCallback = _callbackActivator.SerializeCallback(callbackMethod, callbackData);
				feedSubmission.Data = serializedCallback.Data;
				feedSubmission.TypeName = serializedCallback.TypeName;
				feedSubmission.MethodName = serializedCallback.MethodName;
				feedSubmission.DataTypeName = serializedCallback.DataTypeName;

				feedSubmissionService.Create(feedSubmission);
				feedSubmissionService.SaveChanges();

				_logger.Info($"The following feed was queued for submission to Amazon {feedSubmission.RegionAndTypeComputed}.");
			}
			catch (Exception e)
			{
				_logger.Error(e.Message, e);
			}
		}

		public void PurgeQueue(IFeedSubmissionEntryService feedSubmissionService)
		{
			var entriesToDelete = feedSubmissionService.GetAll().Where(rre => rre.AmazonRegion == _region && rre.MerchantId == _merchantId);
			feedSubmissionService.DeleteRange(entriesToDelete);
			feedSubmissionService.SaveChanges();
		}

		public void SubmitNextFeedInQueueToAmazon(IFeedSubmissionEntryService feedSubmissionService)
		{
			var feedSubmission = feedSubmissionService.GetNextFromQueueOfFeedsToSubmit(_options, _merchantId, _region);

			if (feedSubmission == null) return;
		
			_feedSubmissionProcessor.SubmitFeedToAmazon(feedSubmissionService, feedSubmission);
		}

		public void RequestFeedSubmissionStatusesFromAmazon(IFeedSubmissionEntryService feedSubmissionService)
		{
			var feedSubmissionIds = feedSubmissionService.GetIdsForSubmittedFeedsFromQueue(_options, _merchantId, _region).ToList();

			if (!feedSubmissionIds.Any())
				return;

			var feedSubmissionResults = _feedSubmissionProcessor.RequestFeedSubmissionStatusesFromAmazon(feedSubmissionIds, _merchantId);

			if (feedSubmissionResults != null)
			{
				_feedSubmissionProcessor.QueueFeedsAccordingToProcessingStatus(feedSubmissionService, feedSubmissionResults);
			}
		}

		public void RequestNextFeedSubmissionInQueueFromAmazon(IFeedSubmissionEntryService feedSubmissionService)
		{
			var nextFeedWithProcessingComplete = feedSubmissionService.GetNextFromQueueOfProcessingCompleteFeeds(_options, _merchantId, _region);
			if (nextFeedWithProcessingComplete == null) return;

			var processingReportInfo = _feedSubmissionProcessor.GetFeedSubmissionResultFromAmazon(nextFeedWithProcessingComplete);
			if (processingReportInfo.processingReport == null)
			{
				_logger.Warn($"AmazonMWS feed submission result request failed for {nextFeedWithProcessingComplete.RegionAndTypeComputed}");
				return;
			}

			var hasValidHash = MD5ChecksumHelper.IsChecksumCorrect(processingReportInfo.processingReport, processingReportInfo.md5hash);
			if (hasValidHash)
			{
				_logger.Info($"Checksum verification succeeded for feed submission report for {nextFeedWithProcessingComplete.RegionAndTypeComputed}");
				nextFeedWithProcessingComplete.Details.FeedContent = null;

				using (var streamReader = new StreamReader(processingReportInfo.processingReport))
				{
					var reportContent = streamReader.ReadToEnd();
					var zippedProcessingReport = ZipHelper.CreateArchiveFromContent(reportContent);
					nextFeedWithProcessingComplete.Details.FeedSubmissionReport = zippedProcessingReport;
				}

				feedSubmissionService.Update(nextFeedWithProcessingComplete);
				feedSubmissionService.SaveChanges();
			}
			else
			{
				_logger.Warn($"Checksum verification failed for feed submission report for {nextFeedWithProcessingComplete.RegionAndTypeComputed}");
				//_feedSubmissionProcessor.MoveToRetryQueue(feedSubmissionService, nextFeedWithProcessingComplete);
			}
		}
	}
}
