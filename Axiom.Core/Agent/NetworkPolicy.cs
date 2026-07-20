using System;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Controls outbound HTTP (download_file, fetch_url, web_search).
    /// </summary>
    public sealed class NetworkPolicy
    {
        /// <summary>When true, all network tools fail immediately.</summary>
        public bool Offline { get; set; }

        /// <summary>
        /// When true (default), network tools require ApprovalHandler when mode is Ask,
        /// or always force-ask for download/fetch in Auto (optional strict).
        /// </summary>
        public bool RequireApproval { get; set; }

        public string? Block(string toolName)
        {
            if (!Offline)
                return null;
            return $"Error: network offline mode is on — '{toolName}' blocked. Use /network on.";
        }

        public bool IsNetworkTool(string name) => name is
            "download_file" or "fetch_url" or "web_search" or "open_pr";
    }
}
