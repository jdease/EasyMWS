﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarketplaceWebService;
using MountainWarehouse.EasyMWS.Data;
using MountainWarehouse.EasyMWS.Helpers;
using MountainWarehouse.EasyMWS.ReportProcessors;
using MountainWarehouse.EasyMWS.Services;
using Newtonsoft.Json;

namespace MountainWarehouse.EasyMWS
{
	/// <summary>Client for Amazon Marketplace Web Services</summary>
	public class EasyMwsClient
	{
		private IMarketplaceWebServiceClient _mwsClient;
		private IReportRequestCallbackService _reportRequestCallbackService;
		private IFeedSubmissionCallbackService _feedSubmissionCallbackService;
		private CallbackActivator _callbackActivator;
		private string _merchantId;
		private AmazonRegion _amazonRegion;
		private IRequestReportProcessor _requestReportProcessor;
		private EasyMwsOptions _options;

		public AmazonRegion AmazonRegion => _amazonRegion;

		internal EasyMwsClient(AmazonRegion region, string merchantId, string accessKeyId, string mwsSecretAccessKey, IFeedSubmissionCallbackService feedSubmissionCallbackService, IReportRequestCallbackService reportRequestCallbackService, IMarketplaceWebServiceClient marketplaceWebServiceClient, IRequestReportProcessor requestReportProcessor, EasyMwsOptions options = null) 
			: this(region, merchantId, accessKeyId, mwsSecretAccessKey, options)
		{
			_reportRequestCallbackService = reportRequestCallbackService;
			_feedSubmissionCallbackService = feedSubmissionCallbackService;
			_requestReportProcessor = requestReportProcessor;
			_mwsClient = marketplaceWebServiceClient;
		}

		/// <param name="region">The region of the account</param>
		/// <param name="merchantId"></param>
		/// <param name="accessKeyId">Your specific access key</param>
		/// <param name="mwsSecretAccessKey">Your specific secret access key</param>
		/// <param name="options">Configuration options for EasyMwsClient</param>
		public EasyMwsClient(AmazonRegion region, string merchantId, string accessKeyId, string mwsSecretAccessKey, EasyMwsOptions options = null)
		{
			_options = options ?? EasyMwsOptions.Defaults;
			_merchantId = merchantId;
			_amazonRegion = region;
			_mwsClient = new MarketplaceWebServiceClient(accessKeyId, mwsSecretAccessKey, CreateConfig(region));
			_reportRequestCallbackService = _reportRequestCallbackService ?? new ReportRequestCallbackService();
			_feedSubmissionCallbackService = _feedSubmissionCallbackService ?? new FeedSubmissionCallbackService();
			_callbackActivator = new CallbackActivator();
			_requestReportProcessor = new RequestReportProcessor(_mwsClient, _reportRequestCallbackService, _options);
		}

		/// <summary>
		/// Method that handles querying amazon for reports that are queued for download with the EasyMwsClient.QueueReport method.
		/// It is handling the following operations : 
		/// 1. Requests the next report from report request queue from Amazon, if a ReportRequestId is successfully generated by amazon then the ReportRequest is moved in a queue of reports awaiting Amazon generation.
		///		If a ReportRequestId is not generated by amazon, a retry policy will be applied (retrying to get a ReportRequestId from amazon at after 30m, 1h, 4h intervals.)
		/// 2. Query amazon if any of the reports that are pending generation, were generated.
		///		If any reports were successfully generated (returned report processing status is "_DONE_"), those reports are moved to a queue of reports that await downloading.
		///		If any reports requests were canceled by amazon (returned report processing status is "_CANCELLED_"), then those ReportRequests are moved back to the report request queue.
		///		If amazon returns a processing status any other than "_DONE_" or "_CANCELLED_" for any report requests, those ReportRequests are moved back to the report request queue.
		/// 3. Downloads the next report from amazon (which is the next report ReportRequest in the queue of reports awaiting download).
		/// 4. Perform a callback of the handler method provided as argument when QueueReport was called. The report content can be obtained by reading the stream argument of the callback method.
		/// </summary>
		public void Poll()
		{
			PollReports();
			PollFeeds();
		}

		/// <summary>
		/// Add a new ReportRequest to a queue of requests that are going to be processed, with the final result of trying to download the respective report from Amazon.
		/// </summary>
		/// <param name="reportRequestContainer">An object that contains the arguments required to request the report from Amazon. This object is meant to be obtained by calling a ReportRequestFactory, ex: IReportRequestFactoryFba.</param>
		/// <param name="callbackMethod">A delegate for a method that is going to be called once a report has been downloaded from amazon. The 'Stream' argument of that method will contain the actual report content.</param>
		/// <param name="callbackData">An object any argument(s) needed to invoke the delegate 'callbackMethod'</param>
		public void QueueReport(ReportRequestPropertiesContainer reportRequestContainer, Action<Stream, object> callbackMethod, object callbackData)
		{
			_reportRequestCallbackService.Create(GetSerializedReportRequestCallback(reportRequestContainer, callbackMethod, callbackData));
			_reportRequestCallbackService.SaveChanges();
		}

		/// <summary>
		/// Add a new FeedSubmissionRequest to a queue of feeds to be submitted to amazon, with the final result of obtaining of posting the feed data to amazon and obtaining a response.
		/// </summary>
		/// <param name="feedSubmissionContainer"></param>
		/// <param name="callbackMethod"></param>
		/// <param name="callbackData"></param>
		public void QueueFeed(FeedSubmissionPropertiesContainer feedSubmissionContainer, Action<Stream, object> callbackMethod, object callbackData)
		{
			_feedSubmissionCallbackService.Create(GetSerializedFeedSubmissionCallback(feedSubmissionContainer, callbackMethod, callbackData));
			_feedSubmissionCallbackService.SaveChanges();
		}

		private void PollReports()
		{
			CleanUpReportRequestQueue();
			RequestNextReportInQueueFromAmazon();
			RequestReportStatusesFromAmazon();
			var generatedReportRequestCallback = DownloadNextGeneratedRequestReportInQueueFromAmazon();
			PerformCallback(generatedReportRequestCallback.reportRequestCallback, generatedReportRequestCallback.stream);
			_reportRequestCallbackService.SaveChanges();
		}

		private void PollFeeds()
		{
			CleanUpFeedSubmissionQueue();
		}

		private void CleanUpFeedSubmissionQueue()
		{
			
		}

		private void RequestNextReportInQueueFromAmazon()
		{
			var reportRequestCallbackReportQueued = _requestReportProcessor.GetNonRequestedReportFromQueue(_amazonRegion);

			if (reportRequestCallbackReportQueued == null)
				return;

			var reportRequestId = _requestReportProcessor.RequestSingleQueuedReport(reportRequestCallbackReportQueued, _merchantId);

			reportRequestCallbackReportQueued.LastRequested = DateTime.UtcNow;
			_reportRequestCallbackService.Update(reportRequestCallbackReportQueued);
			
			if (string.IsNullOrEmpty(reportRequestId))
			{
				_requestReportProcessor.AllocateReportRequestForRetry(reportRequestCallbackReportQueued);
			}
			else
			{
				_requestReportProcessor.MoveToNonGeneratedReportsQueue(reportRequestCallbackReportQueued, reportRequestId);
			}
		}

		private void CleanUpReportRequestQueue()
		{
			var expiredReportRequests = _reportRequestCallbackService.GetAll()
				.Where(rrc => rrc.RequestRetryCount > _options.MaxRequestRetryCount);

			foreach (var reportRequest in expiredReportRequests)
			{
				_reportRequestCallbackService.Delete(reportRequest);
			}
		}

		private (ReportRequestCallback reportRequestCallback, Stream stream) DownloadNextGeneratedRequestReportInQueueFromAmazon()
		{
			var generatedReportRequest = _requestReportProcessor.GetReadyForDownloadReports(_amazonRegion);

			if (generatedReportRequest == null)
				return (null, null);
			
			var stream = _requestReportProcessor.DownloadGeneratedReport(generatedReportRequest, _merchantId);
			
			return (generatedReportRequest, stream);
		}

		private void PerformCallback(ReportRequestCallback reportRequestCallback, Stream stream)
		{
			if (reportRequestCallback == null || stream == null) return;

			var callback = new Callback(reportRequestCallback.TypeName, reportRequestCallback.MethodName,
				reportRequestCallback.Data, reportRequestCallback.DataTypeName);

			_callbackActivator.CallMethod(callback, stream);

			DequeueReport(reportRequestCallback);
		}

		private void RequestReportStatusesFromAmazon()
		{
			var reportRequestCallbacksPendingReports = _requestReportProcessor.GetAllPendingReport(_amazonRegion).ToList();

			if (!reportRequestCallbacksPendingReports.Any())
				return;

			var reportRequestIds = reportRequestCallbacksPendingReports.Select(x => x.RequestReportId);

			var reportRequestStatuses = _requestReportProcessor.GetReportRequestListResponse(reportRequestIds, _merchantId);

			_requestReportProcessor.MoveReportsToGeneratedQueue(reportRequestStatuses);
			_requestReportProcessor.MoveReportsBackToRequestQueue(reportRequestStatuses);

		}

		private void DequeueReport(ReportRequestCallback reportRequestCallback)
		{
			_requestReportProcessor.DequeueReportRequestCallback(reportRequestCallback);
		}

		private ReportRequestCallback GetSerializedReportRequestCallback(
			ReportRequestPropertiesContainer reportRequestContainer, Action<Stream, object> callbackMethod, object callbackData)
		{
			if (reportRequestContainer == null || callbackMethod == null) throw new ArgumentNullException();
			var serializedCallback = _callbackActivator.SerializeCallback(callbackMethod, callbackData);

			return new ReportRequestCallback(serializedCallback)
			{
				AmazonRegion = _amazonRegion,
				LastRequested = DateTime.MinValue,
				ContentUpdateFrequency = reportRequestContainer.UpdateFrequency,
				ReportRequestData = JsonConvert.SerializeObject(reportRequestContainer)
			};
		}

		private FeedSubmissionCallback GetSerializedFeedSubmissionCallback(
			FeedSubmissionPropertiesContainer propertiesContainer, Action<Stream, object> callbackMethod, object callbackData)
		{
			if (propertiesContainer == null || callbackMethod == null) throw new ArgumentNullException();
			var serializedCallback = _callbackActivator.SerializeCallback(callbackMethod, callbackData);

			return new FeedSubmissionCallback(serializedCallback)
			{
				AmazonRegion = _amazonRegion,
				FeedSubmissionData = JsonConvert.SerializeObject(propertiesContainer)
			};
		}

		#region Helpers for creating the MarketplaceWebServiceClient

		private MarketplaceWebServiceConfig CreateConfig(AmazonRegion region)
		{
			string rootUrl;
			switch (region)
			{
				case AmazonRegion.Australia:
					rootUrl = MwsEndpoint.Australia.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.China:
					rootUrl = MwsEndpoint.China.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.Europe:
					rootUrl = MwsEndpoint.Europe.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.India:
					rootUrl = MwsEndpoint.India.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.Japan:
					rootUrl = MwsEndpoint.Japan.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.NorthAmerica:
					rootUrl = MwsEndpoint.NorthAmerica.RegionOrMarketPlaceEndpoint;
					break;
				case AmazonRegion.Brazil:
					rootUrl = MwsEndpoint.Brazil.RegionOrMarketPlaceEndpoint;
					break;
				default:
					throw new ArgumentException($"{region} is unknown - EasyMWS doesn't know the RootURL");
			}

			var config = new MarketplaceWebServiceConfig
			{
				ServiceURL = rootUrl
			};
			config = config.WithUserAgent("EasyMWS");

			return config;
		}

		#endregion

	}
}
