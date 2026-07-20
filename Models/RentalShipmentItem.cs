using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public class RentalShipmentItem
    {
        public int RentalShipmentId { get; set; }

        [ForeignKey(nameof(RentalShipmentId))]
        public virtual RentalShipment? RentalShipment { get; set; }

        public int RentalItemId { get; set; }

        [ForeignKey(nameof(RentalItemId))]
        public virtual RentalItem? RentalItem { get; set; }
    }
}
