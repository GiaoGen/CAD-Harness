using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CadHarness.Cli
{
    internal sealed class RulePlanner : PlannerBase
    {
        protected override string Provider { get { return "rule"; } }
        private const string Number = @"[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?";
        private static readonly string[] Subjects = {
            @"(?:hole\s+spacing|spacing|孔距|孔间距|mounting_holes\.spacing)",
            @"(?:plate\s+thickness|thickness|板厚|板厚度|基础板厚度|厚度|base_plate\.thickness)",
            @"(?:hole\s+diameter|孔径|孔直径|种子孔直径|mounting_hole_seed\.diameter)",
            @"(?:fillet\s+radius|fillet|圆角半径|外侧圆角半径|圆角|outer_edge_fillet\.radius)"
        };
        protected override Task<string> ProduceAsync(string command, CadState state, PlannerMetrics metrics, CancellationToken cancellationToken)
        {
            string text = command.Trim();
            string compact = Regex.Replace(text.ToLowerInvariant(), @"\s+", "");
            if (compact == "createcanonicalpart" || compact == "createacanonicalpart" || compact == "createcanonicalplate" ||
                compact == "创建标准板件" || compact == "创建canonical零件" || compact == "创建标准零件" ||
                compact == "create80x50x10mmplatewith2ø8throughholes,40mmspacingandr3outerverticalfillets" ||
                compact == "create80×50×10mmplatewith2ø8throughholes,40mmspacingandr3outerverticalfillets" ||
                compact == "创建80×50×10mm板，两个ø8通孔，孔距40mm，四条外侧竖直边r3圆角")
                return Task.FromResult(IntentSchema.CanonicalJson());
            for (int i = 0; i < Subjects.Length; i++)
            {
                string pattern = @"^(?:(?:change|set)\s+(?:the\s+)?|(?:将|把))?" + Subjects[i] +
                    @"\s*(?:(?:(?:from|从)\s*)?" + Number + @"\s*(?:mm|毫米)?\s*(?:to|改为|改成|→|->)\s*)?" +
                    @"(?:to|改为|改成|设为|设置为|调整为|=)?\s*" + (i == 3 ? @"(?:R\s*)?" : "") +
                    @"(?<value>" + Number + @")\s*(?:mm|毫米)?[。.]?$";
                Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!match.Success) continue;
                double value;
                if (!Double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || Double.IsInfinity(value))
                    throw new PlannerFailure("PLANNER_NUMERIC_INVALID", "The requested length is not a finite number.", true, false);
                return Task.FromResult(IntentSchema.EditJson(IntentSchema.Parameters[i], value));
            }
            throw IntentSchema.Unsupported("RulePlanner supports the documented canonical create phrases and one absolute mm parameter edit per command.");
        }
    }
}
