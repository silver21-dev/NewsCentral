using System.Text.Json.Serialization;
using NewsCentral.Security;

namespace NewsCentral.Models.IndexFile
{
    public class TeamIndexFile : ISignable
    {
        public string TeamFolderName { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string Version { get; set; } = "1.0.0";
        public string IndexHash { get; set; } = string.Empty;
        public List<PublishedAssignmentIndex> PublishedAssignments { get; set; } = new();
        public IndexStatistics Statistics { get; set; } = new();
        public string? Signature { get; set; }

        /// <summary>
        /// <see cref="KeyFingerprint.Of"/> of the public key that verifies <see cref="Signature"/>. Part of the signed
        /// payload; verifiers use it to select the configured key (no key travels with the index). Null on an unsigned
        /// index; <see cref="JsonIgnoreCondition.WhenWritingNull"/> keeps the canonical bytes of such an index unchanged.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? KeyId { get; set; }
    }
}
