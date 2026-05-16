using System.Text.Json;

namespace AuditIt.Api.Services
{
    public static class ItemOwnerNamesJson
    {
        public static (IReadOnlyList<string>? OwnerNames, string? Error) Parse(string? ownerUserNamesJson)
        {
            if (string.IsNullOrWhiteSpace(ownerUserNamesJson))
            {
                return (null, null);
            }

            try
            {
                var ownerNames = JsonSerializer.Deserialize<List<string>>(ownerUserNamesJson);
                var normalized = ItemOwnerSnapshot.Normalize(ownerNames);
                return (ItemOwnerSnapshot.Split(normalized), null);
            }
            catch (JsonException)
            {
                return (null, "Owner user names json is invalid.");
            }
        }
    }
}
