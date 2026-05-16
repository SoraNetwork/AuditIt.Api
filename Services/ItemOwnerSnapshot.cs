namespace AuditIt.Api.Services
{
    public static class ItemOwnerSnapshot
    {
        private static readonly char[] Separators = [',', '\uFF0C'];

        public static string? Normalize(IEnumerable<string>? ownerNames)
        {
            if (ownerNames == null)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();

            foreach (var rawName in ownerNames)
            {
                foreach (var name in SplitRaw(rawName))
                {
                    if (seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names.Count == 0 ? null : string.Join(",", names);
        }

        public static IReadOnlyList<string> Split(string? snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return Array.Empty<string>();
            }

            return SplitRaw(snapshot).ToList();
        }

        private static IEnumerable<string> SplitRaw(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            foreach (var part in value.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    yield return part.Trim();
                }
            }
        }
    }
}
