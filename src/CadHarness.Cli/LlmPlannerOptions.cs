using System;
using System.Globalization;

namespace CadHarness.Cli
{
    internal sealed class LlmPlannerOptions
    {
        internal Uri BaseUrl;
        internal string ApiKey;
        internal string Model;
        internal string OutputMode = "json_schema";
        internal int TimeoutSeconds = 30;
        internal int MaxOutputTokens = 512;

        internal static LlmPlannerOptions FromEnvironment()
        {
            Uri baseUrl;
            if (!Uri.TryCreate(Environment.GetEnvironmentVariable("CAD_HARNESS_LLM_BASE_URL"), UriKind.Absolute, out baseUrl)) throw Config();
            LlmPlannerOptions options = new LlmPlannerOptions {
                BaseUrl=baseUrl, Model=Environment.GetEnvironmentVariable("CAD_HARNESS_LLM_MODEL"),
                ApiKey=Environment.GetEnvironmentVariable("CAD_HARNESS_LLM_API_KEY")
            };
            string outputMode = Environment.GetEnvironmentVariable("CAD_HARNESS_LLM_OUTPUT_MODE");
            if (!String.IsNullOrEmpty(outputMode)) options.OutputMode = outputMode;
            options.TimeoutSeconds = IntegerSetting("CAD_HARNESS_LLM_TIMEOUT_SECONDS", 30);
            options.MaxOutputTokens = IntegerSetting("CAD_HARNESS_LLM_MAX_OUTPUT_TOKENS", 512);
            options.RequireValid(); return options;
        }
        internal void RequireValid()
        {
            if (BaseUrl == null || !BaseUrl.IsAbsoluteUri || (BaseUrl.Scheme != "https" && !(BaseUrl.Scheme == "http" && BaseUrl.IsLoopback)) ||
                !String.IsNullOrEmpty(BaseUrl.UserInfo) || !String.IsNullOrEmpty(BaseUrl.Query) || !String.IsNullOrEmpty(BaseUrl.Fragment) ||
                String.IsNullOrWhiteSpace(Model) || Model.Length > 128 ||
                (!BaseUrl.IsLoopback && String.IsNullOrWhiteSpace(ApiKey)) ||
                (ApiKey != null && (ApiKey.Contains("\r") || ApiKey.Contains("\n"))) ||
                (OutputMode != "json_schema" && OutputMode != "json_object") || TimeoutSeconds < 1 || TimeoutSeconds > 120 ||
                MaxOutputTokens < 128 || MaxOutputTokens > 4096) throw Config();
        }
        internal Uri ChatCompletionsUrl { get { return new Uri(BaseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions"); } }
        private static int IntegerSetting(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (String.IsNullOrEmpty(raw)) return fallback;
            int value;
            if (!Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw Config();
            return value;
        }
        private static PlannerFailure Config()
        {
            return new PlannerFailure("PLANNER_CONFIG_INVALID", "Configure an absolute LLM base URL, model and API key (key optional for loopback); use json_schema or json_object, timeout 1..120 seconds, and max output tokens 128..4096.", false, false);
        }
    }
}
