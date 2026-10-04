using System.Globalization;

namespace CadHarness.Cli
{
    internal static class PlanRuntimeBridge
    {
        internal static int Execute(PlannedIntent plan, string statePath)
        {
            // Reuse the entire verified CLI runtime, including its attach, state, edit and rollback paths.
            // Arguments can only be manufactured from validated, immutable domain intents.
            if (plan.Mode == PlannedMode.Create && plan.Create != null && plan.Edit == null)
                return Program.ExecuteVerifiedRuntime(statePath == null ? new[] { "create-canonical" } : new[] { "create-canonical", statePath });
            if (plan.Mode == PlannedMode.Edit && plan.Edit != null && plan.Create == null)
            {
                string value = plan.Edit.ValueMm.ToString("R", CultureInfo.InvariantCulture);
                return Program.ExecuteVerifiedRuntime(statePath == null ? new[] { "edit", plan.Edit.ParameterSemanticId, value } :
                    new[] { "edit", plan.Edit.ParameterSemanticId, value, statePath });
            }
            throw new PlannerFailure("PLANNER_TYPED_INTENT_INVALID", "No validated typed create/edit intent is available.", true, false);
        }
    }
}
