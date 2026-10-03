using System.Text.RegularExpressions;

namespace Lilium
{

    /// <summary>
    /// 左右の相手の名前（Left ↔ Right、_L ↔ _R、.L ↔ .R、L_ ↔ R_ など）。左右の書き方が無ければ null（体の中心）
    /// </summary>
    public static class MirrorNames
    {
        static readonly (Regex pattern, string replacement)[] kRules = {
            (new Regex ("Left"), "Right"),
            (new Regex ("Right"), "Left"),
            (new Regex ("left"), "right"),
            (new Regex ("right"), "left"),
            (new Regex ("(?<=[._\\- ])L$"), "R"),
            (new Regex ("(?<=[._\\- ])R$"), "L"),
            (new Regex ("(?<=[._\\- ])l$"), "r"),
            (new Regex ("(?<=[._\\- ])r$"), "l"),
            (new Regex ("^L(?=[._\\- ])"), "R"),
            (new Regex ("^R(?=[._\\- ])"), "L"),
            (new Regex ("^l(?=[._\\- ])"), "r"),
            (new Regex ("^r(?=[._\\- ])"), "l"),
        };

        public static string Swap (string name)
        {
            if (string.IsNullOrEmpty (name)) return null;
            foreach ((Regex pattern, string replacement) rule in kRules) {
                if (rule.pattern.IsMatch (name)) return rule.pattern.Replace (name, rule.replacement, 1);
            }
            return null;
        }
    }

}
