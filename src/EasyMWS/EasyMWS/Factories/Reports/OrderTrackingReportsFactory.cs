﻿using System;
using System.Collections.Generic;
using System.Text;
using MountainWarehouse.EasyMWS.Model;

namespace MountainWarehouse.EasyMWS.Factories.Reports
{
	// When adding support for a new report type, ReportsPermittedMarketplacesMapper map also has to be updated to include the permitted marketplaces for that report.
	public class OrderTrackingReportsFactory : IOrderTrackingReportsFactory
	{
		public ReportRequestPropertiesContainer FlatFileArchivedOrdersReport(DateTime? startDate = null, DateTime? endDate = null, IEnumerable<MwsMarketplace> requestedMarketplacesGroup = null)
		{
			throw new NotImplementedException();
		}

		public ReportRequestPropertiesContainer FlatFileOrdersByLastUpdateReport(DateTime? startDate = null, DateTime? endDate = null, IEnumerable<MwsMarketplace> requestedMarketplacesGroup = null)
		{
			throw new NotImplementedException();
		}

		public ReportRequestPropertiesContainer FlatFileOrdersByOrderDateReport(DateTime? startDate = null, DateTime? endDate = null, IEnumerable<MwsMarketplace> requestedMarketplacesGroup = null)
		{
			throw new NotImplementedException();
		}

		public ReportRequestPropertiesContainer XMLOrdersByLastUpdateReport(DateTime? startDate = null, DateTime? endDate = null, IEnumerable<MwsMarketplace> requestedMarketplacesGroup = null)
		{
			throw new NotImplementedException();
		}

		public ReportRequestPropertiesContainer XMLOrdersByOrderDateReport(DateTime? startDate = null, DateTime? endDate = null, IEnumerable<MwsMarketplace> requestedMarketplacesGroup = null)
		{
			throw new NotImplementedException();
		}
	}
}
