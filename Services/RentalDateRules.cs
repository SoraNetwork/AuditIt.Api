using System;
using System.Collections.Generic;
using System.Linq;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    internal static class RentalDateRules
    {
        private static readonly TimeSpan BusinessOffset = TimeSpan.FromHours(8);
        public const string RemovedFromRentalReturnNote = "Removed from rental item list.";

        public static DateTime? EffectiveReleasedFromRentalAt(RentalItem rentalItem) =>
            rentalItem.ReleasedFromRentalAt ??
            (string.Equals(rentalItem.ReturnNotes, RemovedFromRentalReturnNote, StringComparison.Ordinal)
                ? rentalItem.ReturnedAt
                : null);

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

        public static DateTime EffectiveExpectedEndDate(
            DateTime expectedEndDate,
            bool hasRenewalIntent,
            DateTime? renewalIntentEndDate)
        {
            var expectedEnd = ToBusinessDate(expectedEndDate);
            if (!hasRenewalIntent || !renewalIntentEndDate.HasValue)
            {
                return expectedEnd;
            }

            var renewalIntentEnd = ToBusinessDate(renewalIntentEndDate.Value);
            return renewalIntentEnd > expectedEnd ? renewalIntentEnd : expectedEnd;
        }

        public static DateTime EffectiveExpectedEndDate(Rental rental) =>
            EffectiveExpectedEndDate(rental.ExpectedEndDate, rental.HasRenewalIntent, rental.RenewalIntentEndDate);

        public static bool HasOutboundShipment(Rental rental) =>
            rental.Shipments.Any(shipment => shipment.Direction == ShipmentDirection.Outbound);

        public static bool IsRenewal(Rental rental) =>
            rental.RenewedFromRentalId.HasValue;

        public static bool HasRentalStarted(Rental rental) =>
            IsRenewal(rental) || HasOutboundShipment(rental);

        public static bool ShouldUseReturnBuffer(Rental rental) =>
            rental.Status != RentalStatus.Renewed
            && !rental.RenewedToRentalId.HasValue;

        public static DateTime OccupancyStartDate(
            DateTime expectedShipDate,
            IEnumerable<RentalShipment> shipments,
            DateTime? historicalOccupancyStartDate = null)
        {
            var firstOutbound = shipments
                .Where(shipment => shipment.Direction == ShipmentDirection.Outbound)
                .OrderBy(shipment => shipment.ShippedAt)
                .Select(shipment => (DateTime?)ToBusinessDate(shipment.ShippedAt))
                .FirstOrDefault();

            // A booked item is reserved on its scheduled shipping day. If that day
            // passes without an outbound shipment, do not leave a stale reservation
            // on the missed day: carry the reservation forward one business day at a
            // time until it is actually shipped.
            if (!firstOutbound.HasValue)
            {
                // Completed legacy rentals can predate shipment tracking. Keep their
                // recorded historical occupancy intact rather than projecting them
                // forward to today.
                if (historicalOccupancyStartDate.HasValue)
                {
                    return ToBusinessDate(historicalOccupancyStartDate.Value);
                }

                var expectedShipDay = ToBusinessDate(expectedShipDate);
                var today = Today(DateTime.UtcNow);
                return expectedShipDay < today ? today : expectedShipDay;
            }

            // The actual outbound timestamp is authoritative, including when the
            // shipment happens earlier or later than originally expected.
            return firstOutbound.Value;
        }

        public static DateTime OccupancyStartDate(Rental rental)
        {
            if (IsRenewal(rental))
            {
                return ToBusinessDate(rental.ExpectedShipDate);
            }

            return OccupancyStartDate(
                rental.ExpectedShipDate,
                rental.Shipments,
                rental.Status == RentalStatus.Returned ? rental.StartDate : null);
        }

        public static DateTime OccupancyEndDate(
            DateTime expectedEndDate,
            DateTime? actualEndDate,
            DateTime? returnedAt = null,
            DateTime? openEndedUntil = null,
            bool includeReturnBuffer = true,
            DateTime? releasedFromRentalAt = null)
        {
            var expectedEnd = ToBusinessDate(expectedEndDate);
            if (releasedFromRentalAt.HasValue)
            {
                return ToBusinessDate(releasedFromRentalAt.Value);
            }

            if (returnedAt.HasValue || actualEndDate.HasValue || !includeReturnBuffer)
            {
                return expectedEnd;
            }

            var bufferedEnd = expectedEnd.AddDays(1);
            if (!openEndedUntil.HasValue)
            {
                return bufferedEnd;
            }

            var openEnd = ToBusinessDate(openEndedUntil.Value);
            return openEnd > bufferedEnd ? openEnd : bufferedEnd;
        }

        public static DateTime OccupancyEndDate(Rental rental, DateTime openEndedRangeEnd) =>
            ExtendReturnedRenewalEndDate(
                rental,
                null,
                OccupancyEndDate(
                EffectiveExpectedEndDate(rental),
                rental.ActualEndDate,
                openEndedUntil: OpenEndedUntil(
                    rental.ActualEndDate,
                    null,
                    HasRentalStarted(rental) && rental.Items.Any(item => item.ReturnedAt == null),
                    openEndedRangeEnd),
                includeReturnBuffer: ShouldUseReturnBuffer(rental)));

        public static DateTime OccupancyEndDate(Rental rental, RentalItem rentalItem, DateTime openEndedRangeEnd) =>
            ExtendReturnedRenewalEndDate(
                rental,
                rentalItem,
                OccupancyEndDate(
                EffectiveExpectedEndDate(rental),
                rental.ActualEndDate,
                rentalItem.ReturnedAt,
                OpenEndedUntil(rental.ActualEndDate, rentalItem.ReturnedAt, HasRentalStarted(rental), openEndedRangeEnd),
                ShouldUseReturnBuffer(rental),
                EffectiveReleasedFromRentalAt(rentalItem)));

        private static DateTime ExtendReturnedRenewalEndDate(Rental rental, RentalItem? rentalItem, DateTime endDate)
        {
            if (!IsRenewal(rental) || rental.Status != RentalStatus.Returned)
            {
                return endDate;
            }

            var actualRelease = new[] { rental.ActualEndDate, rentalItem?.ReturnedAt }
                .Where(value => value.HasValue)
                .Select(value => ToBusinessDate(value!.Value))
                .DefaultIfEmpty(endDate)
                .Max();
            return actualRelease > endDate ? actualRelease : endDate;
        }

        public static DateTime? OpenEndedUntil(
            DateTime? actualEndDate,
            DateTime? returnedAt,
            bool hasRentalStarted,
            DateTime rangeEnd)
        {
            if (!hasRentalStarted || actualEndDate != null || returnedAt != null)
            {
                return null;
            }

            var requestedEnd = ToBusinessDate(rangeEnd);
            var today = Today(DateTime.UtcNow);
            return requestedEnd < today ? requestedEnd : today;
        }

        public static bool IsReturningBusinessDate(DateTime expectedEndDate, DateTime day) =>
            day.Date > ToBusinessDate(expectedEndDate);

        public static bool OccupiesBusinessDate(
            DateTime expectedShipDate,
            DateTime expectedEndDate,
            IEnumerable<RentalShipment> shipments,
            DateTime day,
            DateTime? actualEndDate = null,
            DateTime? returnedAt = null,
            DateTime? openEndedUntil = null,
            bool includeReturnBuffer = true,
            DateTime? releasedFromRentalAt = null,
            DateTime? historicalOccupancyStartDate = null)
        {
            var businessDay = day.Date;
            return OccupancyStartDate(expectedShipDate, shipments, historicalOccupancyStartDate) <= businessDay
                && OccupancyEndDate(expectedEndDate, actualEndDate, returnedAt, openEndedUntil, includeReturnBuffer, releasedFromRentalAt) >= businessDay;
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
