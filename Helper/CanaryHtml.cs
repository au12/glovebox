using CarCareTracker.Canary;
using Microsoft.AspNetCore.Html;
using System.Globalization;
using System.Net;
using System.Text;

namespace CarCareTracker.Helper
{
    /// <summary>The few drawn pieces the CANary pages share: status lamps, limit gauges and trend lines.</summary>
    public static class CanaryHtml
    {
        private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        private static string E(string s) => WebUtility.HtmlEncode(s);

        /// <summary>
        /// A status lamp. Shape carries the meaning as well as colour: octagon for critical, triangle for a
        /// warning, a round "i" for an advisory, a tick for passed.
        /// </summary>
        public static IHtmlContent Lamp(CanarySeverity severity, string extraClass = "")
        {
            string icon = severity switch
            {
                CanarySeverity.Critical => "bi-exclamation-octagon-fill",
                CanarySeverity.Warning => "bi-exclamation-triangle-fill",
                CanarySeverity.Advisory => "bi-info-circle-fill",
                CanarySeverity.Ok => "bi-check-circle-fill",
                CanarySeverity.Unavailable => "bi-dash-circle",
                _ => "bi-three-dots"
            };
            return new HtmlString($"<span class=\"cn-lamp cn-{CanaryFormat.SeverityClass(severity)} {extraClass}\" role=\"img\" aria-label=\"{CanaryFormat.SeverityWord(severity)}\"><i class=\"bi {icon}\"></i></span>");
        }

        public static IHtmlContent Gauge(CanaryFormat.Gauge? gauge, bool compact = false)
        {
            if (gauge == null)
            {
                return HtmlString.Empty;
            }
            var html = new StringBuilder();
            html.Append($"<div class=\"cn-limit\" role=\"img\" aria-label=\"{E(gauge.Spoken)}\"><div class=\"cn-track\">");
            foreach (var (width, cls) in gauge.Zones)
            {
                html.Append($"<i class=\"cn-zone cn-{cls}\" style=\"width:{N(width)}%\"></i>");
            }
            html.Append($"</div><i class=\"cn-needle\" style=\"left:{N(gauge.Needle)}%\"></i></div>");
            if (compact)
            {
                return new HtmlString(html.ToString());
            }
            var ticks = new StringBuilder("<div class=\"cn-ticks\" aria-hidden=\"true\">");
            foreach (var (at, label) in gauge.Ticks)
            {
                ticks.Append($"<span style=\"left:{N(at)}%\">{E(label)}</span>");
            }
            ticks.Append("</div>");
            return new HtmlString($"<div class=\"cn-limit-wrap\">{html}{ticks}</div>");
        }

        public static IHtmlContent Trend(CanaryFormat.Trend? trend)
        {
            if (trend == null)
            {
                return HtmlString.Empty;
            }
            var html = new StringBuilder();
            html.Append($"<svg class=\"cn-trend\" viewBox=\"0 0 {N(trend.Width)} {N(trend.Height)}\" width=\"{N(trend.Width)}\" height=\"{N(trend.Height)}\" role=\"img\" aria-label=\"{E(trend.Words.Length > 0 ? trend.Words : "Weekly trend")}\">");
            html.Append($"<path d=\"{trend.Path}\"/>");
            foreach (var (x, y, cls, title) in trend.Dots)
            {
                html.Append($"<circle class=\"cn-{cls}\" cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"3\"><title>{E(title)}</title></circle>");
            }
            html.Append("</svg>");
            return new HtmlString(html.ToString());
        }
    }
}
