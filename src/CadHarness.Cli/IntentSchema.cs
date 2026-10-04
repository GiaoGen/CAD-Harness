using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CadHarness.Cli
{
    internal static class IntentSchema
    {
        internal const double MaximumLengthMm = 1000;
        internal static readonly string[] Parameters = { "mounting_holes.spacing", "base_plate.thickness", "mounting_hole_seed.diameter", "outer_edge_fillet.radius" };
        internal static readonly string[] Keys = { "intentVersion", "mode", "units", "createPreset", "parameter", "valueMm" };

        internal static object JsonSchema()
        {
            // Required nullable fields keep this compatible with strict structured-output providers.
            return new { type = "object", additionalProperties = false, required = Keys,
                properties = new Dictionary<string, object> {
                    { "intentVersion", new { type = "string", @enum = new[] { "0.1" } } },
                    { "mode", new { type = "string", @enum = new[] { "create", "edit" } } },
                    { "units", new { type = "string", @enum = new[] { "mm" } } },
                    { "createPreset", new { type = new[] { "string", "null" }, @enum = new object[] { "canonical", null } } },
                    { "parameter", new { type = new[] { "string", "null" }, @enum = new object[] { Parameters[0], Parameters[1], Parameters[2], Parameters[3], null } } },
                    { "valueMm", new { type = new[] { "number", "null" }, exclusiveMinimum = 0, maximum = MaximumLengthMm } }
                }
            };
        }

        internal static PlannedIntent Validate(string json)
        {
            if (json == null || json.Length > 8192) throw Schema("Intent output is missing or exceeds 8192 characters.");
            Dictionary<string, object> value = StrictJson.Parse(json) as Dictionary<string, object>;
            if (value == null || value.Count != Keys.Length) throw Schema("Intent must contain exactly the six declared schema fields.");
            foreach (string key in Keys) if (!value.ContainsKey(key)) throw Schema("Intent is missing a required field.");
            if (!(value["intentVersion"] is string) || (string)value["intentVersion"] != "0.1") throw Schema("Unknown intent version.");
            if (!(value["units"] is string) || (string)value["units"] != "mm") throw Schema("Only mm is supported.");
            string mode = value["mode"] as string;
            if (mode != "create" && mode != "edit") throw Unsupported("Only canonical create and the four parameter edits are supported.");
            if (mode == "create")
            {
                if (!(value["createPreset"] is string) || (string)value["createPreset"] != "canonical") throw Unsupported("Only the canonical create preset is supported.");
                if (value["parameter"] != null || value["valueMm"] != null) throw Schema("Create cannot include edit parameters.");
                return PlannedIntent.Canonical();
            }
            if (value["createPreset"] != null) throw Schema("Edit cannot include a create preset.");
            string parameter = value["parameter"] as string;
            if (Array.IndexOf(Parameters, parameter) < 0) throw Unsupported("Unknown semantic parameter.");
            object raw = value["valueMm"];
            if (!(raw is long) && !(raw is double)) throw Numeric();
            double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || number <= 0 || number > MaximumLengthMm) throw Numeric();
            return PlannedIntent.Parameter(ParameterEditIntent.ParseCli(parameter, number.ToString("R", CultureInfo.InvariantCulture)));
        }

        internal static object Document(PlannedIntent intent)
        {
            return new { intentVersion = "0.1", mode = intent.Mode == PlannedMode.Create ? "create" : "edit", units = "mm",
                createPreset = intent.Mode == PlannedMode.Create ? "canonical" : null,
                parameter = intent.Edit == null ? null : intent.Edit.ParameterSemanticId,
                valueMm = intent.Edit == null ? (double?)null : intent.Edit.ValueMm };
        }
        internal static string CanonicalJson() { return new JavaScriptSerializer().Serialize(Document(PlannedIntent.Canonical())); }
        internal static string EditJson(string parameter, double value)
        {
            return new JavaScriptSerializer().Serialize(new { intentVersion="0.1", mode="edit", units="mm", createPreset=(string)null, parameter=parameter, valueMm=value });
        }
        internal static PlannerFailure Unsupported(string message) { return new PlannerFailure("PLANNER_UNSUPPORTED_INTENT", message, false, true); }
        private static PlannerFailure Numeric() { return new PlannerFailure("PLANNER_NUMERIC_INVALID", "Edit value must be a JSON number in the range 0 < mm <= 1000.", true, false); }
        private static PlannerFailure Schema(string message) { return new PlannerFailure("PLANNER_SCHEMA_INVALID", message, true, false); }
    }
}
