using System;
using System.Collections.Generic;
using System.Linq;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    internal static class RentalDateRules
    {
        private static readonly TimeSpan BusinessOffset = TimeSpan.FromHours(8);

        public static DateTime ToBusinessDate(DateTime value)
        {
            if (value.TimeOfDay == TimeSpan.Zero)
            {
                return value.Date;
            }

            // Date picker values were historically stored as UTC instants for China-local dates.
            return value.Add(BusinessOffset).Date;
        }

        public static DateTime Today(DateTime utcNow) => utcNow.Add(BusinessOffset).Date;

        public static DateTime EndOfBusinessDay(DateTime value) =>
            ToBusinessDate(value).AddDays(1).AddTicks(-1);

        public static bool IsBusinessDateWithin(DateTime value, DateTime from, DateTime to)
        {
            var date = ToBusinessDate(value);
            return date >= from.Date && date <= to.Date;
        }

        public static bool Overlaps(DateTime startDate, DateTime expectedEndDate, DateTime from, DateTime to) =>
            ToBusinessDate(startDate) <= to.Date && ToBusinessDate(expectedEndDate) >= from.Date;

        public static DateTime OccupancyStartDate(DateTime startDate, IEnumerable<RentalShipment> shipments)
        {
            var start = ToBusinessDate(startDate);
            var firstOutbound = shipments
                .Where(shipment => shipment.Direction == ShipmentDirection.Outbound)
                .OrderBy(shipment => shipment.ShippedAt)
                .Select(shipment => (DateTime?)ToBusinessDate(shipment.ShippedAt))
                .FirstOrDefault();

            return firstOutbound.HasValue && firstOutbound.Value < start
                ? firstOutbound.Value
                : start;
        }

        public static DateTime OccupancyEndDate(
            DateTime expectedEndDate,
            DateTime? actualEndDate,
            DateTime? returnedAt = null,
            DateTime? openEndedUntil = null) =>
            ToBusinessDate(returnedAt ?? actualEndDate ?? openEndedUntil ?? expectedEndDate);

        public static DateTime? OpenEndedUntil(
            DateTime? actualEndDate,
            DateTime? returnedAt,
            bool hasRentalStarted,
            DateTime rangeEnd) =>
            hasRentalStarted && actualEndDate == null && returnedAt == null
                ? rangeEnd
                : null;

        public static bool IsReturningBusinessDate(DateTime expectedEndDate, DateTime day) =>
            day.Date > ToBusinessDate(expectedEndDate);

        public static bool OccupiesBusinessDate(
            DateTime startDate,
            DateTime expectedEndDate,
            IEnumerable<RentalShipment> shipments,
            DateTime day,
            DateTime? actualEndDate = null,
            DateTime? returnedAt = null,
            DateTime? openEndedUntil = null)
        {
            var businessDay = day.Date;
            return OccupancyStartDate(startDate, shipments) <= businessDay
                && OccupancyEndDate(expectedEndDate, actualEndDate, returnedAt, openEndedUntil) >= businessDay;
        }

        public static string Format(DateTime value) => ToBusinessDate(value).ToString("yyyy-MM-dd");

        public static string FormatDateTime(DateTime value) =>
            value.Add(BusinessOffset).ToString("yyyy-MM-dd HH:mm");

        public static DateTime DefaultExpectedShipDate(DateTime startDate) =>
            ToBusinessDate(startDate).AddDays(-1);

        public static bool IsOverdue(DateTime expectedEndDate, DateTime utcNow) =>
            ToBusinessDate(expectedEndDate).AddDays(1) < Today(utcNow);

        public static DateTime LeadUntilDate(DateTime utcNow, int hours) =>
            utcNow.AddHours(Math.Max(1, hours)).Add(BusinessOffset).Date;
    }
}
