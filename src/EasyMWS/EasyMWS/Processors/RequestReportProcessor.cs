﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using MountainWarehouse.EasyMWS.Data;
using MountainWarehouse.EasyMWS.Enums;
using MountainWarehouse.EasyMWS.Helpers;
using MountainWarehouse.EasyMWS.Logging;
using MountainWarehouse.EasyMWS.Model;
using MountainWarehouse.EasyMWS.Services;
using MountainWarehouse.EasyMWS.WebService.MarketplaceWebService;
using MountainWarehouse.EasyMWS.WebService.MarketplaceWebService.Model;

namespace MountainWarehouse.EasyMWS.Processors
{
    internal class RequestReportProcessor : IRequestReportProcessor
    {
	    private readonly IMarketplaceWebServiceClient _marketplaceWebServiceClient;
	    private readonly IEasyMwsLogger _logger;
		private readonly EasyMwsOptions _options;
	    private readonly AmazonRegion _region;
	    private readonly string _merchantId;

	    internal RequestReportProcessor(AmazonRegion region, string merchantId, IMarketplaceWebServiceClient marketplaceWebServiceClient, IEasyMwsLogger logger, EasyMwsOptions options)
	    {
		    _region = region;
		    _merchantId = merchantId;
		    _options = options;
		    _logger = logger;
			_marketplaceWebServiceClient = marketplaceWebServiceClient;
	    }

		public void RequestReportFromAmazon(IReportRequestEntryService reportRequestService, ReportRequestEntry reportRequestEntry)
	    {
		    var missingInformationExceptionMessage = "Cannot request report from amazon due to missing report request information.";

			if (reportRequestEntry?.ReportRequestData == null) throw new ArgumentNullException($"{missingInformationExceptionMessage}: Report request data is missing.");
		    if (string.IsNullOrEmpty(reportRequestEntry?.ReportType)) throw new ArgumentException($"{missingInformationExceptionMessage}: Report Type is missing.");
			if(string.IsNullOrEmpty(reportRequestEntry.MerchantId)) throw new ArgumentException($"{missingInformationExceptionMessage}: MerchantId is missing.");

			var reportRequestData = reportRequestEntry.GetPropertiesContainer();

			_logger.Info($"Attempting to request the next report in queue from Amazon: {reportRequestEntry.RegionAndTypeComputed}.");

			var reportRequest = new RequestReportRequest
			{
				Merchant = reportRequestEntry.MerchantId,
				ReportType = reportRequestEntry.ReportType
			};

		    if (reportRequestData.MarketplaceIdList != null)
			    reportRequest.MarketplaceIdList = new IdList {Id = reportRequestData.MarketplaceIdList};
			if (reportRequestData.StartDate.HasValue)
			    reportRequest.StartDate = reportRequestData.StartDate.Value;
		    if (reportRequestData.EndDate.HasValue)
			    reportRequest.EndDate = reportRequestData.EndDate.Value;
		    if (!string.IsNullOrEmpty(reportRequestData.ReportOptions))
			    reportRequest.ReportOptions = reportRequestData.ReportOptions;

		    try
		    {
			    var reportResponse = _marketplaceWebServiceClient.RequestReport(reportRequest);
			    reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;
			    reportRequestEntry.RequestReportId = reportResponse?.RequestReportResult?.ReportRequestInfo?.ReportRequestId;

				var requestId = reportResponse?.ResponseHeaderMetadata?.RequestId ?? "unknown";
			    var timestamp = reportResponse?.ResponseHeaderMetadata?.Timestamp ?? "unknown";

				if (string.IsNullOrEmpty(reportRequestEntry.RequestReportId))
			    {
					reportRequestEntry.ReportRequestRetryCount++;
					_logger.Warn($"AmazonMWS request succeeded for {reportRequestEntry.RegionAndTypeComputed}. But a ReportRequestId was not generated by Amazon. Placing report request in retry queue. Retry count is now : {reportRequestEntry.ReportRequestRetryCount}. [RequestId:'{requestId}',Timestamp:'{timestamp}']",
						new RequestInfo(timestamp, requestId));
				}
			    else
			    {
				    reportRequestEntry.ReportRequestRetryCount = 0;
					_logger.Info($"AmazonMWS request succeeded for {reportRequestEntry.RegionAndTypeComputed}. ReportRequestId:'{reportRequestEntry.RequestReportId}'. [RequestId:'{requestId}',Timestamp:'{timestamp}']",
						new RequestInfo(timestamp, requestId));
				}

			    reportRequestService.Update(reportRequestEntry);
			}
		    catch (MarketplaceWebServiceException e) when (e.StatusCode == HttpStatusCode.BadRequest && IsAmazonErrorCodeFatal(e.ErrorCode))
		    {
			    _logger.Error($"Request to MWS.RequestReport failed! [HttpStatusCode:'{e.StatusCode}', ErrorType:'{e.ErrorType}', ErrorCode:'{e.ErrorCode}', Message: '{e.Message}']", e);
			    reportRequestService.Delete(reportRequestEntry);
				_logger.Warn($"AmazonMWS request failed for {reportRequestEntry.RegionAndTypeComputed}. The report request was removed from queue.");
			}
		    catch (MarketplaceWebServiceException e) when (IsAmazonErrorCodeNonFatal(e.ErrorCode))
		    {
			    _logger.Error($"Request to MWS.RequestReport failed! [HttpStatusCode:'{e.StatusCode}', ErrorType:'{e.ErrorType}', ErrorCode:'{e.ErrorCode}', Message: '{e.Message}']", e);
			    reportRequestEntry.ReportRequestRetryCount++;
			    reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;
			    reportRequestService.Update(reportRequestEntry);
				_logger.Warn($"AmazonMWS request failed for {reportRequestEntry.RegionAndTypeComputed}. Reason: ReportRequestId not generated by Amazon. Placing report request in retry queue. Retry count is now : {reportRequestEntry.ReportRequestRetryCount}");
		    }
		    catch (Exception e)
		    {
			    _logger.Error($"Request to MWS.RequestReport failed! [Message: '{e.Message}']", e);
				reportRequestEntry.ReportRequestRetryCount++;
			    reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;
			    reportRequestService.Update(reportRequestEntry);
				_logger.Warn($"AmazonMWS request failed for {reportRequestEntry.RegionAndTypeComputed}. Reason: ReportRequestId not generated by Amazon. Placing report request in retry queue. Retry count is now : {reportRequestEntry.ReportRequestRetryCount}");
			}
		    finally
			{
			    reportRequestService.SaveChanges();
			}
	    }

	    public List<(string ReportRequestId, string GeneratedReportId, string ReportProcessingStatus)> GetReportProcessingStatusesFromAmazon(IEnumerable<string> requestIdList, string merchant)
	    {
		    _logger.Info($"Attempting to request report processing statuses for all reports in queue.");

		    var request = new GetReportRequestListRequest() {ReportRequestIdList = new IdList(), Merchant = merchant};
		    request.ReportRequestIdList.Id.AddRange(requestIdList);

		    try
		    {
			    var response = _marketplaceWebServiceClient.GetReportRequestList(request);
			    var requestId = response?.ResponseHeaderMetadata?.RequestId ?? "unknown";
			    var timestamp = response?.ResponseHeaderMetadata?.Timestamp ?? "unknown";
			    _logger.Info(
				    $"Request to MWS.GetReportRequestList was successful! [RequestId:'{requestId}',Timestamp:'{timestamp}']",
				    new RequestInfo(timestamp, requestId));

			    var responseInformation =
				    new List<(string ReportRequestId, string GeneratedReportId, string ReportProcessingStatus)>();

			    if (response?.GetReportRequestListResult?.ReportRequestInfo != null)
			    {
				    foreach (var reportRequestInfo in response.GetReportRequestListResult.ReportRequestInfo)
				    {
					    responseInformation.Add(
						    (reportRequestInfo.ReportRequestId, reportRequestInfo.GeneratedReportId, reportRequestInfo
							    .ReportProcessingStatus));
				    }
			    }
			    else
			    {
					_logger.Warn("AmazonMWS GetReportRequestList response does not contain any results.");
				}

			    return responseInformation;

		    }
		    catch (MarketplaceWebServiceException e)
		    {
				_logger.Error($"Request to MWS.GetReportRequestList failed! [Message: '{e.Message}', HttpStatusCode:'{e.StatusCode}', ErrorType:'{e.ErrorType}', ErrorCode:'{e.ErrorCode}']", e);
			    return null;
			}
			catch (Exception e)
		    {
				_logger.Error($"Request to MWS.GetReportRequestList failed! [Message: '{e.Message}']", e);
			    return null;
			}
	    }

	    public void CleanupReportRequests(IReportRequestEntryService reportRequestService)
	    {
			_logger.Info("Executing cleanup of report requests queue.");
		    var allReportRequestEntries = reportRequestService.GetAll();
		    var removedEntriesIds = new List<int>();

		    var expiredReportRequests = allReportRequestEntries.Where(rrc => IsMatchForRegionAndMerchantId(rrc) && IsRequestRetryCountExceeded(rrc));
			var expiredReportRequestsDeleteReason = $"Failure while trying to request report from Amazon. ReportRequestMaxRetryCount exceeded.";
		    MarkEntriesAsDeleted(reportRequestService, expiredReportRequests, removedEntriesIds, expiredReportRequestsDeleteReason);

		    var entriesWithDownloadRetryCountExceeded = allReportRequestEntries.Where(rrc => IsMatchForRegionAndMerchantId(rrc) && IsDownloadRetryCountExceeded(rrc));
		    var entriesWithDownloadRetryCountExceededDeleteReason = $"Failure while trying to download the report from Amazon. ReportDownloadMaxRetryCount exceeded.";
		    MarkEntriesAsDeleted(reportRequestService, entriesWithDownloadRetryCountExceeded, removedEntriesIds, entriesWithDownloadRetryCountExceededDeleteReason);

		    var entriesWithProcessingRetryCountExceeded = allReportRequestEntries.Where(rrc => IsMatchForRegionAndMerchantId(rrc) && IsProcessingRetryCountExceeded(rrc));
			var entriesWithProcessingRetryCountExceededDeleteReason = $"Failure while trying to obtain _DONE_ processing status from amazon. ReportProcessingMaxRetryCount exceeded.";
		    MarkEntriesAsDeleted(reportRequestService, entriesWithProcessingRetryCountExceeded, removedEntriesIds, entriesWithProcessingRetryCountExceededDeleteReason);

		    var entriesWithExpirationPeriodExceeded = allReportRequestEntries.Where(rrc => IsMatchForRegionAndMerchantId(rrc) && IsExpirationPeriodExceeded(rrc));
		    var entriesWithExpirationPeriodExceededDeleteReason = $"Expiration period ReportDownloadRequestEntryExpirationPeriod was exceeded.";
		    MarkEntriesAsDeleted(reportRequestService, entriesWithExpirationPeriodExceeded, removedEntriesIds, entriesWithExpirationPeriodExceededDeleteReason);

		    var entriesWithCallbackRetryCountExceeded = allReportRequestEntries.Where(rrc => IsMatchForRegionAndMerchantId(rrc) &&  IsCallbackInvocationRetryCountExceeded(rrc));
		    var entriesWithCallbackRetryCountExceededDeleteReason = $"The report was downloaded successfully but the callback method invocation failed. InvokeCallbackMaxRetryCount exceeded.";
		    MarkEntriesAsDeleted(reportRequestService, entriesWithCallbackRetryCountExceeded, removedEntriesIds, entriesWithCallbackRetryCountExceededDeleteReason);

			reportRequestService.SaveChanges();
	    }

		public void QueueReportsAccordingToProcessingStatus(IReportRequestEntryService reportRequestService,
			List<(string ReportRequestId, string GeneratedReportId, string ReportProcessingStatus)> reportGenerationStatuses)
	    {
			foreach (var reportGenerationInfo in reportGenerationStatuses)
			{
				var reportGenerationCallback = reportRequestService.FirstOrDefault(rrc =>
					rrc.RequestReportId == reportGenerationInfo.ReportRequestId
					&& rrc.GeneratedReportId == null);
				if (reportGenerationCallback == null) continue;

				if (reportGenerationInfo.ReportProcessingStatus == "_DONE_")
				{
					reportGenerationCallback.GeneratedReportId = reportGenerationInfo.GeneratedReportId;
					reportGenerationCallback.ReportProcessRetryCount = 0;
					reportRequestService.Update(reportGenerationCallback);
					_logger.Info(
						$"Report was successfully generated by Amazon for {reportGenerationCallback.RegionAndTypeComputed}. GeneratedReportId:'{reportGenerationInfo.GeneratedReportId}'. ProcessingStatus:'{reportGenerationInfo.ReportProcessingStatus}'.");
				}
				else if (reportGenerationInfo.ReportProcessingStatus == "_DONE_NO_DATA_")
				{
					reportRequestService.Delete(reportGenerationCallback);
					_logger.Warn(
						$"Report was successfully generated by Amazon for {reportGenerationCallback.RegionAndTypeComputed} but it didn't contain any data. ProcessingStatus:'{reportGenerationInfo.ReportProcessingStatus}'. Report request is now dequeued.");
				}
				else if (reportGenerationInfo.ReportProcessingStatus == "_SUBMITTED_" ||
				            reportGenerationInfo.ReportProcessingStatus == "_IN_PROGRESS_")
				{
					_logger.Info(
						$"Report generation by Amazon is still in progress for {reportGenerationCallback.RegionAndTypeComputed}. ProcessingStatus:'{reportGenerationInfo.ReportProcessingStatus}'.");
				}
				else if (reportGenerationInfo.ReportProcessingStatus == "_CANCELLED_")
				{
					reportGenerationCallback.RequestReportId = null;
					reportGenerationCallback.GeneratedReportId = null;
					reportGenerationCallback.ReportProcessRetryCount++;
					reportRequestService.Update(reportGenerationCallback);
					_logger.Warn(
						$"Report generation was canceled by Amazon for {reportGenerationCallback.RegionAndTypeComputed}. ProcessingStatus:'{reportGenerationInfo.ReportProcessingStatus}'. Placing report back in report request queue! Retry count is now ReportProcessRetryCount='{reportGenerationCallback.ReportProcessRetryCount}'.");
				}
				else
				{
					reportGenerationCallback.RequestReportId = null;
					reportGenerationCallback.GeneratedReportId = null;
					reportGenerationCallback.ReportProcessRetryCount++;
					reportRequestService.Update(reportGenerationCallback);
					_logger.Warn(
						$"Report status returned by amazon is {reportGenerationInfo.ReportProcessingStatus} for {reportGenerationCallback.RegionAndTypeComputed}. This status is not yet handled by EasyMws. Placing report back in report request queue! Retry count is now ReportProcessRetryCount='{reportGenerationCallback.ReportProcessRetryCount}'");
				}
			}
			reportRequestService.SaveChanges();
	    }

		public void DownloadGeneratedReportFromAmazon(IReportRequestEntryService reportRequestService, ReportRequestEntry reportRequestEntry)
	    {
		    var missingInformationExceptionMessage = "Cannot request report from amazon due to missing report request information.";
			_logger.Info($"Attempting to download the next report in queue from Amazon: {reportRequestEntry.RegionAndTypeComputed}.");

		    if (string.IsNullOrEmpty(reportRequestEntry?.GeneratedReportId)) throw new ArgumentException($"{missingInformationExceptionMessage}: GeneratedReportId is missing.");
			if (string.IsNullOrEmpty(reportRequestEntry.MerchantId)) throw new ArgumentException($"{missingInformationExceptionMessage}: MerchantId is missing.");

			var reportResultStream = new MemoryStream();
		    var getReportRequest = new GetReportRequest
		    {
			    ReportId = reportRequestEntry.GeneratedReportId,
			    Report = reportResultStream,
			    Merchant = reportRequestEntry.MerchantId
		    };

		    try
		    {
			    var response = _marketplaceWebServiceClient.GetReport(getReportRequest);
			    reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;

				
				var requestId = response?.ResponseHeaderMetadata?.RequestId ?? "unknown";
			    var timestamp = response?.ResponseHeaderMetadata?.Timestamp ?? "unknown";
			    _logger.Info(
				    $"Report download from Amazon has succeeded for {reportRequestEntry.RegionAndTypeComputed}.[RequestId:'{requestId}',Timestamp:'{timestamp}']",
				    new RequestInfo(timestamp, requestId));
			    reportResultStream.Position = 0;

				var md5Hash = response?.GetReportResult?.ContentMD5;
				var hasValidHash = MD5ChecksumHelper.IsChecksumCorrect(reportResultStream, md5Hash);
			    if (hasValidHash)
			    {
				    _logger.Info($"Checksum verification succeeded for report {reportRequestEntry.RegionAndTypeComputed}");
				    reportRequestEntry.ReportDownloadRetryCount = 0;

					using (var streamReader = new StreamReader(reportResultStream))
				    {
					    var zippedReport = ZipHelper.CreateArchiveFromContent(streamReader.ReadToEnd());
					    reportRequestEntry.Details = new ReportRequestDetails { ReportContent = zippedReport };
				    }
			    }
			    else
			    {
				    _logger.Warn($"Checksum verification failed for report {reportRequestEntry.RegionAndTypeComputed}. Placing report download in retry queue. Retry count : {reportRequestEntry.ReportDownloadRetryCount}");
					reportRequestEntry.ReportDownloadRetryCount++;
				}

			    reportRequestService.Update(reportRequestEntry);
			}
		    catch (MarketplaceWebServiceException e) when (e.StatusCode == HttpStatusCode.BadRequest && IsAmazonErrorCodeFatal(e.ErrorCode))
		    {
			    reportRequestService.Delete(reportRequestEntry);
			    _logger.Error($"AmazonMWS report download failed for {reportRequestEntry.RegionAndTypeComputed}! [HttpStatusCode:'{e.StatusCode}', ErrorType:'{e.ErrorType}', ErrorCode:'{e.ErrorCode}', Message: '{e.Message}']. The report request was removed from queue.", e);
			}
		    catch (MarketplaceWebServiceException e) when (IsAmazonErrorCodeNonFatal(e.ErrorCode))
			{
				reportRequestEntry.ReportDownloadRetryCount++;
				reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;
				reportRequestService.Update(reportRequestEntry);
				_logger.Error($"AmazonMWS report download failed for {reportRequestEntry.RegionAndTypeComputed}! [HttpStatusCode:'{e.StatusCode}', ErrorType:'{e.ErrorType}', ErrorCode:'{e.ErrorCode}', Message: '{e.Message}']. Placing report download in retry queue. Retry count : {reportRequestEntry.ReportDownloadRetryCount}", e);
			}
			catch (Exception e)
		    {
			    reportRequestEntry.ReportDownloadRetryCount++;
			    reportRequestEntry.LastAmazonRequestDate = DateTime.UtcNow;
			    reportRequestService.Update(reportRequestEntry);
				_logger.Error($"AmazonMWS report download failed for {reportRequestEntry.RegionAndTypeComputed}! [Reason: '{e.Message}']", e);
		    }
		    finally
		    {
			    reportRequestService.SaveChanges();
		    }
		}


	    private bool IsAmazonErrorCodeFatal(string errorCode)
	    {
		    var fatalErrorCodes = new List<string>
		    {
			    "AccessToReportDenied",
			    "InvalidReportId",
			    "InvalidReportType",
			    "InvalidRequest",
			    "ReportNoLongerAvailable"
		    };

		    return fatalErrorCodes.Contains(errorCode);
	    }

	    private bool IsAmazonErrorCodeNonFatal(string errorCode)
	    {
		    var nonFatalErrorCodes = new List<string>
		    {
			    "ReportNotReady",
			    "InvalidScheduleFrequency"
		    };

		    return nonFatalErrorCodes.Contains(errorCode) || !IsAmazonErrorCodeFatal(errorCode);
	    }

	    private void MarkEntriesAsDeleted(IReportRequestEntryService reportRequestService, IQueryable<ReportRequestEntry> entriesToMarkAsDeleted, List<int> entriesIdsAlreadyMarkedAsDeleted, string deleteReason)
	    {
		    foreach (var entry in entriesToMarkAsDeleted)
		    {
			    if (entriesIdsAlreadyMarkedAsDeleted.Exists(e => e == entry.Id)) continue;
			    entriesIdsAlreadyMarkedAsDeleted.Add(entry.Id);
			    reportRequestService.Delete(entry);
			    _logger.Warn($"Report request entry {entry.RegionAndTypeComputed} deleted from queue. {deleteReason}");
		    }
	    }

	    private bool IsMatchForRegionAndMerchantId(ReportRequestEntry e) => e.AmazonRegion == _region && e.MerchantId == _merchantId;

	    private bool IsRequestRetryCountExceeded(ReportRequestEntry e) => e.ReportRequestRetryCount > _options.ReportRequestMaxRetryCount;

	    private bool IsDownloadRetryCountExceeded(ReportRequestEntry e) => e.ReportDownloadRetryCount > _options.ReportDownloadMaxRetryCount;

	    private bool IsProcessingRetryCountExceeded(ReportRequestEntry e) => e.ReportProcessRetryCount > _options.ReportProcessingMaxRetryCount;

	    private bool IsCallbackInvocationRetryCountExceeded(ReportRequestEntry e) => e.InvokeCallbackRetryCount > _options.InvokeCallbackMaxRetryCount;

	    private bool IsExpirationPeriodExceeded(ReportRequestEntry reportRequestEntry) =>
		    (DateTime.Compare(reportRequestEntry.DateCreated, DateTime.UtcNow.Subtract(_options.ReportDownloadRequestEntryExpirationPeriod)) < 0);

	}
}
