using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    // New orchestrator built from the verified shape of the WPF app's WorkplaceView.SendQueryAsync
    // (Architect -> Builder -> Critic, confidence-routed revision) rather than extracted from it —
    // that method is ~1,500 lines interleaved line-by-line with WPF UI calls across a 21k-line
    // file, so this reproduces its control flow and prompt contracts as clean code instead. Scoped
    // to what a CLI coding agent needs: general chat answers, and connected-workspace coding tasks
    // that resolve to an [[AXIOM_CODEBASE_PATCH]] proposal via WorkspaceAccessService. The
    // artifact-canvas/document/local-model-segmentation paths in the original are out of scope
    // for v1 (see plan).
    public sealed class CouncilOrchestrator
    {
        // Matches WorkplaceView.xaml.cs's MaxBuilderRetryAttempts (confirmed by direct read of the
        // WPF source): at most 2 repair passes before the council keeps its best output instead of
        // looping indefinitely.
        private const int MaxBuilderRetryAttempts = 2;
        private const int TargetedPatchIssueCeiling = 2; // 1-2 issues => targeted patch
        private const int WorkspaceContextBudgetChars = 60_000;

        private readonly IChatPipeline _pipeline;
        private readonly string _modelId;
        private readonly WorkspaceAccessService _workspace;

        public CouncilOrchestrator(IChatPipeline pipeline, string modelId, WorkspaceAccessService? workspace = null)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _modelId = modelId;
            _workspace = workspace ?? new WorkspaceAccessService();
        }

        public async Task<CouncilResult> RunAsync(
            CouncilRequest request,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            bool isWorkspaceTask = request.Workspace is { CodebaseEditAccessEnabled: true };
            string workspaceContext = string.Empty;
            IReadOnlyList<string> workspaceFilesRead = Array.Empty<string>();

            if (isWorkspaceTask)
            {
                WorkspaceContextResult context = _workspace.BuildContextPacket(
                    request.Workspace!, request.UserPrompt, WorkspaceContextBudgetChars);
                workspaceContext = context.Packet;
                workspaceFilesRead = context.FilesRead;
                Report(progress, CouncilEventKind.Status,
                    workspaceFilesRead.Count > 0
                        ? $"Connected codebase context attached: {workspaceFilesRead.Count} file(s)."
                        : "Codebase Edit Access is enabled, but no readable local code files are connected yet.");
            }

            // ── Architect: plan ────────────────────────────────────────────────
            Report(progress, CouncilEventKind.Status, "Architect is planning...");
            string architectSystem = FoundationSystemPrompt.Apply(
                "You are the Architect. Read the user's request and any attached context, then " +
                "produce a concise numbered implementation plan (3-8 steps). Do not write final " +
                "code or prose output — only the plan.");
            string architectInput = BuildContextualInput(request.UserPrompt, workspaceContext);
            string architectPlan = await CallRoleAsync(architectSystem, architectInput, progress, cancellationToken);
            Report(progress, CouncilEventKind.ArchitectOutput, architectPlan);

            // ── Builder: implement ──────────────────────────────────────────────
            string builderSystem = BuildBuilderSystemPrompt(isWorkspaceTask);
            string builderInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext);

            Report(progress, CouncilEventKind.Status, "Builder is implementing...");
            string builderOutput = await CallRoleAsync(builderSystem, builderInput, progress, cancellationToken);
            Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

            WorkspacePatchProposal? patch = null;
            if (isWorkspaceTask)
            {
                patch = await ResolvePatchWithRetryAsync(
                    request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken);
                if (patch == null)
                {
                    return new CouncilResult(
                        Success: false,
                        FinalText: builderOutput,
                        Patch: null,
                        FinalCriticReport: new CriticReport { Status = "issues" });
                }
            }

            // ── Critic: review + confidence-routed revision ─────────────────────
            int retries = 0;
            CriticReport report;
            while (true)
            {
                Report(progress, CouncilEventKind.Status, "Critic is reviewing...");
                string criticInput = BuildCriticInput(request.UserPrompt, isWorkspaceTask, builderOutput);
                string criticOutput = await CallRoleAsync(CriticSystemPrompt, criticInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.CriticOutput, criticOutput);
                report = CriticContractParser.Parse(criticOutput);

                int issueCount = report.Issues?.Count ?? 0;
                if (issueCount == 0)
                {
                    Report(progress, CouncilEventKind.Status, "Critic found no issues.");
                    break;
                }

                if (retries >= MaxBuilderRetryAttempts)
                {
                    Report(progress, CouncilEventKind.Warning,
                        $"Builder retry limit reached ({MaxBuilderRetryAttempts}); keeping current output. " +
                        $"{issueCount} Critic finding(s) remain.");
                    break;
                }

                retries++;
                bool fullRevision = issueCount > TargetedPatchIssueCeiling;
                Report(progress, CouncilEventKind.Status,
                    fullRevision
                        ? $"Critic found {issueCount} issues — running full revision ({retries}/{MaxBuilderRetryAttempts})..."
                        : $"Critic found {issueCount} issue(s) — running targeted patch ({retries}/{MaxBuilderRetryAttempts})...");

                string revisionSystem = fullRevision
                    ? builderSystem
                    : builderSystem + "\n" + TargetedPatchModeNote(isWorkspaceTask);
                string revisionInput = BuildRevisionInput(
                    request.UserPrompt, architectPlan, workspaceContext, builderOutput, criticOutput, fullRevision);

                builderOutput = await CallRoleAsync(revisionSystem, revisionInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);

                if (isWorkspaceTask)
                {
                    patch = await ResolvePatchWithRetryAsync(
                        request, workspaceContext, architectPlan, builderSystem, builderOutput, progress, cancellationToken);
                    if (patch == null)
                    {
                        return new CouncilResult(false, builderOutput, null, report);
                    }
                }
            }

            Report(progress, CouncilEventKind.Completed, "Council run complete.");
            return new CouncilResult(true, builderOutput, patch, report);
        }

        private async Task<WorkspacePatchProposal?> ResolvePatchWithRetryAsync(
            CouncilRequest request,
            string workspaceContext,
            string architectPlan,
            string builderSystem,
            string builderOutput,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt <= 1; attempt++)
            {
                if (_workspace.TryParsePatchProposal(builderOutput, out WorkspacePatchProposal? proposal, out string error))
                    return proposal;

                if (attempt == 1)
                {
                    Report(progress, CouncilEventKind.Failed, $"Builder did not return a valid patch: {error}");
                    return null;
                }

                Report(progress, CouncilEventKind.Warning, $"Patch did not parse ({error}); asking Builder to correct the format.");
                string retryInput = BuildBuilderInput(request.UserPrompt, architectPlan, workspaceContext)
                    + "\n\n[FORMAT CORRECTION NEEDED]\nYour previous output did not parse as a valid " +
                    "[[AXIOM_CODEBASE_PATCH]] envelope: " + error +
                    "\nOutput ONLY a corrected, valid patch envelope now.";
                builderOutput = await CallRoleAsync(builderSystem, retryInput, progress, cancellationToken);
                Report(progress, CouncilEventKind.BuilderOutput, builderOutput);
            }

            return null;
        }

        private async Task<string> CallRoleAsync(
            string systemPrompt,
            string userInput,
            IProgress<CouncilEvent>? progress,
            CancellationToken cancellationToken)
        {
            var request = new ChatPipelineRequest(systemPrompt, userInput);
            ChatPipelineResult result = await _pipeline.ExecuteAsync(request, onToken: null, cancellationToken);
            return result.ResponseText;
        }

        private static string BuildBuilderSystemPrompt(bool isWorkspaceTask)
        {
            string role = "You are the Builder. Implement the Architect's plan completely — no " +
                "TODO placeholders, no omitted sections, no pseudo-code.";
            return FoundationSystemPrompt.Apply(isWorkspaceTask
                ? role + "\n" + WorkspacePatchModeNote
                : role);
        }

        // The exact patch envelope contract is already embedded in WorkspaceAccessService's
        // context packet (BuildContextPacket), so this only needs to point the Builder at it —
        // duplicating the format instructions here would risk the two drifting apart.
        private const string WorkspacePatchModeNote =
            "This is a connected-codebase task. Follow the patch envelope format given in the " +
            "workspace context exactly. Output ONLY the [[AXIOM_CODEBASE_PATCH]] envelope — no " +
            "narration before or after it.";

        private static string TargetedPatchModeNote(bool isWorkspaceTask) => isWorkspaceTask
            ? "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output a complete, " +
              "valid [[AXIOM_CODEBASE_PATCH]] envelope — do not output a diff or partial snippet, " +
              "and do not change anything not mentioned in the findings."
            : "[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. Output the complete " +
              "corrected response and preserve unaffected content. Do not output a diff.";

        private static string CriticSystemPrompt => FoundationSystemPrompt.Apply(
            "You are the Critic. Review the Builder's output against the original request for " +
            "correctness, completeness, and whether it actually implements what was asked. " +
            "Be specific and evidence-based." + "\n" + CriticContractParser.ContractInstruction);

        private static string BuildContextualInput(string userPrompt, string workspaceContext)
        {
            return string.IsNullOrWhiteSpace(workspaceContext)
                ? userPrompt
                : userPrompt + "\n\n" + workspaceContext;
        }

        private static string BuildBuilderInput(string userPrompt, string architectPlan, string workspaceContext)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            sb.AppendLine("[ARCHITECT PLAN]").AppendLine(architectPlan);
            if (!string.IsNullOrWhiteSpace(workspaceContext))
                sb.AppendLine().AppendLine(workspaceContext);
            return sb.ToString();
        }

        private static string BuildCriticInput(string userPrompt, bool isWorkspaceTask, string builderOutput)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            sb.AppendLine(isWorkspaceTask ? "[BUILDER PATCH PROPOSAL]" : "[BUILDER OUTPUT]");
            sb.AppendLine(builderOutput);
            return sb.ToString();
        }

        private static string BuildRevisionInput(
            string userPrompt,
            string architectPlan,
            string workspaceContext,
            string builderOutput,
            string criticOutput,
            bool fullRevision)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[ORIGINAL REQUEST]").AppendLine(userPrompt).AppendLine();
            if (fullRevision)
                sb.AppendLine("[ARCHITECT PLAN]").AppendLine(architectPlan).AppendLine();
            sb.AppendLine("[PREVIOUS BUILDER OUTPUT]").AppendLine(builderOutput).AppendLine();
            sb.AppendLine("[CRITIC FINDINGS]").AppendLine(criticOutput);
            if (!string.IsNullOrWhiteSpace(workspaceContext))
                sb.AppendLine().AppendLine(workspaceContext);
            return sb.ToString();
        }

        private static void Report(IProgress<CouncilEvent>? progress, CouncilEventKind kind, string message)
            => progress?.Report(new CouncilEvent(kind, message));
    }
}
